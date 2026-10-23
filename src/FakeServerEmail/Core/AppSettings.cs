using System.Text.Json;
using System.Text.Json.Serialization;
using FakeServerEmail.Protocols;

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
    public int SmtpsPort { get; set; }
    public int Pop3Port { get; set; }
    public int Pop3sPort { get; set; }
    public int ImapPort { get; set; }
    public int ImapsPort { get; set; }
    public int HttpPort { get; set; } = 5080;
    public int HttpsPort { get; set; } = 5443;
    public bool AllowNetworkAccess { get; set; }
    public bool OpenBrowserOnStart { get; set; } = true;
    public bool SmtpRequireAuth { get; set; } = true;
    public bool OfferStartTls { get; set; } = true;
    public List<string> AuthMechanisms { get; set; } = [.. Sasl.Mechanisms];
    public bool AcceptAnyPassword { get; set; }
    public List<UserAccount> Users { get; set; } = [new UserAccount { Username = "teste@teste.local", Password = "teste" }];
    public bool ValidateOAuthTokens { get; set; } = true;
    public int TokenLifetimeSeconds { get; set; } = 3600;
    public bool SeparateMailboxesByRecipient { get; set; }
    public int MaxMessageSizeMb { get; set; } = 50;

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed record PortSetting(string Name, Func<AppSettings, int> Get, Action<AppSettings, int> Set, bool IsWeb = false, bool IsRequired = false)
{
    public const int MaxPort = 65535;

    public static IReadOnlyList<PortSetting> All { get; } =
    [
        new("smtpPort", s => s.SmtpPort, (s, v) => s.SmtpPort = v),
        new("smtpSubmissionPort", s => s.SmtpSubmissionPort, (s, v) => s.SmtpSubmissionPort = v),
        new("smtpsPort", s => s.SmtpsPort, (s, v) => s.SmtpsPort = v),
        new("pop3Port", s => s.Pop3Port, (s, v) => s.Pop3Port = v),
        new("pop3sPort", s => s.Pop3sPort, (s, v) => s.Pop3sPort = v),
        new("imapPort", s => s.ImapPort, (s, v) => s.ImapPort = v),
        new("imapsPort", s => s.ImapsPort, (s, v) => s.ImapsPort = v),
        new("httpPort", s => s.HttpPort, (s, v) => s.HttpPort = v, IsWeb: true, IsRequired: true),
        new("httpsPort", s => s.HttpsPort, (s, v) => s.HttpsPort = v, IsWeb: true),
    ];

    public bool IsValid(int port) => port is >= 1 and <= MaxPort || (!IsRequired && port == 0);

    public string InvalidMessage => IsRequired
        ? $"Porta inválida em {Name}: use um número de 1 a {MaxPort}."
        : $"Porta inválida em {Name}: use um número de 1 a {MaxPort}, ou 0 para desligar o serviço.";
}

public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private const int MinTokenLifetimeSeconds = 10;
    private const int MaxTokenLifetimeSeconds = 60 * 60 * 24 * 30;
    private const int MaxMessageSizeLimitMb = 1024;
    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _path;
    private readonly Lock _lock = new();
    private volatile AppSettings _current;

    public SettingsStore(string dataDirectory, Action<AppSettings>? configure = null)
    {
        _path = Path.Combine(dataDirectory, DataFiles.Settings);
        var loaded = Load(_path);
        configure?.Invoke(loaded);
        Normalize(loaded);
        _current = loaded;
        lock (_lock)
            TrySave(loaded);
    }

    public event Action<AppSettings>? Changed;

    public AppSettings Current => _current;

    public bool Update(AppSettings settings)
    {
        var copy = settings.Clone();
        Normalize(copy);
        bool saved;
        lock (_lock)
        {
            _current = copy;
            saved = TrySave(copy);
        }
        Changed?.Invoke(copy);
        return saved;
    }

    private static AppSettings Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        string json;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                json = File.ReadAllText(path);
                break;
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(ReadRetryDelay);
            }
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            try
            {
                File.Copy(path, path + DataFiles.InvalidSuffix, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return new AppSettings();
        }
    }

    private static void Normalize(AppSettings s)
    {
        var defaults = new AppSettings();
        foreach (var port in PortSetting.All.Where(p => !p.IsValid(p.Get(s))))
            port.Set(s, port.Get(defaults));
        s.Users ??= [];
        s.Users.RemoveAll(u => string.IsNullOrWhiteSpace(u.Username));
        s.AuthMechanisms = s.AuthMechanisms is null ? [.. Sasl.Mechanisms] : Sasl.Mechanisms.Where(m => s.AuthMechanisms.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        s.TokenLifetimeSeconds = Math.Clamp(s.TokenLifetimeSeconds, MinTokenLifetimeSeconds, MaxTokenLifetimeSeconds);
        s.MaxMessageSizeMb = Math.Clamp(s.MaxMessageSizeMb, 1, MaxMessageSizeLimitMb);
    }

    private bool TrySave(AppSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
