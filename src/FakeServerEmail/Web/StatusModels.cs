using System.Text.Json.Serialization;
using FakeServerEmail.Core;

namespace FakeServerEmail.Web;

public static class TlsModes
{
    public const string Implicit = "implicit";
    public const string StartTls = "starttls";
    public const string None = "none";
}

public static class TrustScopes
{
    public const string None = "none";
    public const string User = "user";
    public const string Computer = "computer";
}

public sealed record ServerStatus(
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset ServerTime,
    string? ExecutablePath,
    McpStatus Mcp,
    string DataDirectory,
    string HostName,
    bool AllowNetworkAccess,
    AuthenticationStatus Authentication,
    WebStatus Web,
    IReadOnlyList<ServiceStatus> Services,
    [property: JsonPropertyName("oauth")] OAuthEndpoints OAuth,
    CertificateStatus Certificate,
    MessageCounts Counts);

public sealed record McpStatus(string Http, string? StdioCommand, IReadOnlyList<string> StdioArgs);

public sealed record AuthenticationStatus(bool AcceptAnyPassword, bool SmtpRequireAuth, string ExampleUser, string ExamplePassword);

public sealed record WebStatus(string Http, string? Https, int? HttpPort, int? HttpsPort, int ConfiguredHttpPort, int ConfiguredHttpsPort, string? Error);

public sealed record ServiceStatus(string Name, string Protocol, int ConfiguredPort, int? Port, string Tls, bool Running, string? Error, int Connections);

public sealed record OAuthEndpoints(string Discovery, string Authorize, string Token, string DeviceCode, string Jwks, string Userinfo, string MicrosoftAuthority, string MicrosoftToken, string HttpToken);

public sealed record CertificateStatus(bool Trusted, string TrustScope, string Thumbprint, string Subject, DateTime NotAfter)
{
    public static CertificateStatus From(CertificateProvider certificates)
    {
        var cert = certificates.Certificate;
        var trust = certificates.Trust;
        var scope = trust switch
        {
            CertificateTrust.Computer => TrustScopes.Computer,
            CertificateTrust.User => TrustScopes.User,
            _ => TrustScopes.None,
        };
        return new CertificateStatus(trust != CertificateTrust.None, scope, cert.Thumbprint, cert.Subject, cert.NotAfter);
    }
}

public sealed record SettingsUpdateResult(bool Saved, bool ServicesRestarted, bool RestartRequired, ServerStatus Status);

public sealed record SessionInfo(int Id, string Protocol, int Port, string Client, bool Secure, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    string? User, string? AuthMechanism, string? Error, int MessagesReceived, int LineCount, int Failures, IReadOnlyList<string> LastLines);

public sealed record SessionDetail(SessionInfo Session, IReadOnlyList<LogLine> Lines);
