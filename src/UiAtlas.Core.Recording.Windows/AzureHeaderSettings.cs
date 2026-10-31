using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UiAtlas.Core.Recording.Windows;

public sealed record HeaderProviderReference(string Id, string Model, string Endpoint, string CredentialReference);
public sealed record AzureHeaderSettings(bool Enabled = false, string Endpoint = "", string Deployment = "gpt-5.6-sol",
    string Provider = "azure-openai", string? CredentialReference = null, bool AutoAnalyze = false,
    HeaderProviderReference[]? Providers = null)
{
    public AzureHeaderSettings Normalize()
    {
        if (!Uri.TryCreate(Endpoint?.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath.TrimEnd('/') is not ("" or "/openai/v1" or "/openai/v1/responses"))
            throw new AzureHeaderException("Enter an HTTPS Azure resource endpoint, optionally ending in /openai/v1/.");
        if (string.IsNullOrWhiteSpace(Deployment) || Deployment.Length > 128 || Deployment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new AzureHeaderException("Enter the Azure deployment name for GPT-5.6 Sol.");
        return this with { Endpoint = endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/'), Deployment = Deployment.Trim() };
    }
}

public interface IAzureHeaderCredentialStore
{
    string? Read(string target);
    void Write(string target, string key);
}

public sealed class AzureHeaderSettingsStore
{
    private readonly string _path;
    private readonly IAzureHeaderCredentialStore _credentials;
    public AzureHeaderSettingsStore(string? path = null, IAzureHeaderCredentialStore? credentials = null)
    {
        _path = path ?? Environment.GetEnvironmentVariable("UIATLAS_HEADER_SETTINGS_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UiAtlas", "azure-header-settings.json");
        _credentials = credentials ?? new WindowsHeaderCredentials();
    }
    public AzureHeaderSettings Load() => !File.Exists(_path) ? new() :
        JsonSerializer.Deserialize<AzureHeaderSettings>(File.ReadAllText(_path)) ?? throw new AzureHeaderException("Recorder header settings could not be read. Save them again.");
    public AzureHeaderSettings Resolve(AzureHeaderSettings settings)
    {
        if (settings.Provider is not ("openai" or "azure-openai")) throw new AzureHeaderException("Choose a header reading provider in Settings → Recording.");
        if (settings.Providers is null) return settings; // Standalone Atlas legacy configuration.
        var profile = settings.Providers.SingleOrDefault(p => p.Id == settings.Provider)
            ?? throw new AzureHeaderException("Configure this connection in Settings → AI providers.");
        if (string.IsNullOrWhiteSpace(profile.CredentialReference) || profile.CredentialReference.Length > 256 ||
            !(profile.CredentialReference.StartsWith("UIMaps/Skills/", StringComparison.Ordinal) ||
              profile.CredentialReference.StartsWith("UIMaps/AzureOpenAI/Grid/", StringComparison.Ordinal)))
            throw new AzureHeaderException("Configure the selected connection in Settings → AI providers.");
        return settings with { Endpoint = settings.Provider == "openai" ? "https://api.openai.com" : profile.Endpoint,
            Deployment = profile.Model, CredentialReference = profile.CredentialReference };
    }
    public bool HasKey(AzureHeaderSettings settings) { settings = Resolve(settings).Normalize(); return !string.IsNullOrWhiteSpace(_credentials.Read(settings.CredentialReference ?? Target(settings))); }
    public AzureOpenAiHeaderReader CreateReader(AzureHeaderSettings settings, string? newKey = null)
    {
        settings = Resolve(settings).Normalize();
        return new(settings, string.IsNullOrEmpty(newKey) ? _credentials.Read(settings.CredentialReference ?? Target(settings)) ?? "" : newKey);
    }
    public AzureOpenAiTableReader CreateTableReader()
    {
        var settings = Load();
        if (!settings.Enabled) throw new AzureHeaderException("Select OpenAI or Azure OpenAI in Settings → Recording before reading table data.");
        settings = Resolve(settings).Normalize();
        return new(settings, _credentials.Read(settings.CredentialReference ?? Target(settings)) ?? "");
    }
    public void Save(AzureHeaderSettings settings, string? newKey = null, bool validateConnection = true)
    {
        if (settings.Providers is not null && !string.IsNullOrEmpty(newKey)) throw new AzureHeaderException("Manage shared connection keys in Settings → AI providers.");
        if (settings.Enabled && validateConnection) settings = Resolve(settings).Normalize();
        // Disabling cloud reading must remain possible even if its configuration is invalid.
        if (settings.Enabled && !string.IsNullOrEmpty(newKey)) _credentials.Write(Target(settings), newKey);
        if (settings.Enabled && validateConnection && !HasKey(settings)) throw new AzureHeaderException("Configure the selected connection in Settings → AI providers before using it for header reading.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Update(Func<AzureHeaderSettings, AzureHeaderSettings> change, string? newKey = null, bool validateConnection = true)
    {
        var name = "Local\\UiAtlas.HeaderSettings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant())));
        using var mutex = new Mutex(false, name);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new AzureHeaderException("Header settings are busy. Try saving again.");
            Save(change(Load()), newKey, validateConnection);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private static string Target(AzureHeaderSettings settings) => "UiAtlas.AzureHeaders:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Endpoint)));

    private sealed class WindowsHeaderCredentials : IAzureHeaderCredentialStore
    {
        public string? Read(string target)
        {
            if (!CredRead(target, 1, 0, out var pointer))
            {
                if (Marshal.GetLastWin32Error() == 1168) return null;
                throw new AzureHeaderException("Windows could not read the saved Azure key.");
            }
            try { var credential = Marshal.PtrToStructure<Credential>(pointer);
                return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / 2)); }
            finally { CredFree(pointer); }
        }
        public void Write(string target, string key)
        {
            if (key.Length > 2500) throw new AzureHeaderException("The API key is too long.");
            var pointer = Marshal.StringToCoTaskMemUni(key);
            try
            {
                var credential = new Credential { Type = 1, Target = target, BlobSize = (uint)(key.Length * 2), Blob = pointer, Persist = 2, UserName = "Azure OpenAI" };
                if (!CredWrite(ref credential, 0)) throw new AzureHeaderException("Windows could not save the Azure key.");
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Credential
        {
            public uint Flags, Type; public string Target; public string? Comment; public long LastWritten;
            public uint BlobSize; public nint Blob; public uint Persist, AttributeCount; public nint Attributes;
            public string? Alias; public string UserName;
        }
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
        [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
    }
}
