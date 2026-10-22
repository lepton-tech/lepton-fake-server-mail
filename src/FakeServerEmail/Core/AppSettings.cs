using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeServerEmail.Core;

public sealed class UserAccount
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class AppSettings
{
    public int SmtpPort { get; set; } = 25;
    public int SmtpSubmissionPort { get; set; } = 587;
    public int SmtpsPort { get; set; } = 465;
    public int Pop3Port { get; set; } = 110;
    public int Pop3sPort { get; set; } = 995;
    public int ImapPort { get; set; } = 143;
    public int ImapsPort { get; set; } = 993;
    public int HttpPort { get; set; } = 5080;
    public int HttpsPort { get; set; } = 5443;
    public bool AllowNetworkAccess { get; set; }
    public bool OpenBrowserOnStart { get; set; } = true;
    public bool SmtpRequireAuth { get; set; }
    public bool AcceptAnyPassword { get; set; } = true;
    public List<UserAccount> Users { get; set; } = [];
    public bool ValidateOAuthTokens { get; set; } = true;
    public int TokenLifetimeSeconds { get; set; } = 3600;
    public bool SeparateMailboxesByRecipient { get; set; }
    public int MaxMessageSizeMb { get; set; } = 50;

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public SettingsStore(string dataDirectory, Action<AppSettings>? configure = null)
    {
        _path = Path.Combine(dataDirectory, "settings.json");
        _current = Load(_path);
        configure?.Invoke(_current);
        Normalize(_current);
        TrySave(_current);
    }

    public event Action<AppSettings>? Changed;

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public void Update(AppSettings settings)
    {
        Normalize(settings);
        lock (_lock) _current = settings;
        TrySave(settings);
        Changed?.Invoke(settings);
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            try { File.Copy(path, path + ".invalido", true); } catch { }
        }
        return new AppSettings();
    }

    private static void Normalize(AppSettings s)
    {
        s.Users ??= [];
        s.Users.RemoveAll(u => string.IsNullOrWhiteSpace(u.Username));
        s.TokenLifetimeSeconds = Math.Clamp(s.TokenLifetimeSeconds, 10, 60 * 60 * 24 * 30);
        s.MaxMessageSizeMb = Math.Clamp(s.MaxMessageSizeMb, 1, 1024);
    }

    private void TrySave(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
        }
    }
}
