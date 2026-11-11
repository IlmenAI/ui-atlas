using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace UiAtlas.Core.Desktop;

/// <summary>One viewer per canonical map path in the current Windows session.</summary>
internal sealed class MapViewerInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _owner;

    internal MapViewerInstance(string path)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        _pipe = "UiAtlas.MapViewer." + System.Diagnostics.Process.GetCurrentProcess().SessionId + "." + key;
        _mutex = new Mutex(true, "Local\\" + _pipe, out _owner);
    }

    internal bool ActivateExisting()
    {
        if (_owner) return false;
        // Connect also waits for an earlier invocation still constructing its window.
        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect(15_000);
        client.WriteByte(1);
        return true;
    }

    internal void Listen(Window window)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token);
                    var input = new byte[1];
                    if (await server.ReadAsync(input, _stop.Token) == 1 && input[0] == 1)
                        await window.Dispatcher.InvokeAsync(() =>
                        {
                            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                            window.Show(); window.Activate();
                        });
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* A departing caller must not end the activation listener. */ }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_owner) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
