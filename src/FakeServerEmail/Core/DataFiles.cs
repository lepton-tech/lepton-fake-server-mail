namespace FakeServerEmail.Core;

public static class DataFiles
{
    public const string Settings = "settings.json";
    public const string InvalidSuffix = ".invalido";
    public const string TemporarySuffix = ".tmp";
    public const string OAuthTokens = "oauth-tokens.json";
    public const string OAuthKey = "oauth-chave-privada.pem";
    public const string Certificate = "certificado-localhost.pfx";
    public const string RunningInstance = "em-execucao.json";
    public const string MessagesDirectory = "mensagens";
    public const string MessageIndex = "indice.json";
    public const string WriteProbePrefix = ".teste-escrita";

    public static IReadOnlyList<string> OwnedFiles { get; } =
    [
        Settings,
        Settings + InvalidSuffix,
        Settings + TemporarySuffix,
        OAuthTokens,
        OAuthTokens + InvalidSuffix,
        OAuthTokens + TemporarySuffix,
        OAuthKey,
        Certificate,
        RunningInstance,
        RunningInstance + TemporarySuffix,
    ];

    public static IReadOnlyList<string> OwnedDirectories { get; } = [MessagesDirectory];

    public static string ExtractionDirectory { get; } = Path.Combine(Path.GetTempPath(), ".net", "FakeServerEmail");
}
