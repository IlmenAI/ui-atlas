using System.IO;
using System.IO.Pipes;

namespace UiAtlas.Presentation;

/// <summary>A recorder can only request Settings in the shell that launched it.</summary>
public sealed class AtlasSettingsNavigation : IDisposable
{
    public const string EnvironmentKey = "UIATLAS_SHELL_SETTINGS_PIPE";
    private static string? _currentPipe;
    public static string? CurrentPipeName => Volatile.Read(ref _currentPipe);
    private readonly CancellationTokenSource _stop = new();
    public string PipeName { get; } = "atlas-settings-" + Guid.NewGuid().ToString("N");
    public AtlasSettingsNavigation(Action openSettings)
    {
        Interlocked.Exchange(ref _currentPipe, PipeName); _ = ServeAsync(openSettings);
    }
    private async Task ServeAsync(Action openSettings)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                // A single fixed byte is the complete protocol; no commands, paths or payloads are accepted.
                var request = new byte[1]; await pipe.ReadExactlyAsync(request, _stop.Token);
                if (request[0] != 1) continue;
                openSettings(); await pipe.WriteAsync(new byte[] { 1 }, _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
        }
    }
    public static async Task<bool> RequestAsync(CancellationToken token = default)
    {
        var name = Environment.GetEnvironmentVariable(EnvironmentKey);
        if (string.IsNullOrWhiteSpace(name)) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token); await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
            var reply = new byte[1]; await pipe.ReadExactlyAsync(reply, deadline.Token); return reply[0] == 1;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException) { return false; }
    }
    public void Dispose() { Interlocked.CompareExchange(ref _currentPipe, null, PipeName); _stop.Cancel(); }
}
