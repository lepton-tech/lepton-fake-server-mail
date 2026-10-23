using FakeServerEmail.OAuth;
using FakeServerEmail.Protocols;

namespace FakeServerEmail.Core;

public sealed class ServerContext
{
    public required string DataDirectory { get; init; }
    public required SettingsStore Settings { get; init; }
    public required MessageStore Store { get; init; }
    public required TokenService Tokens { get; init; }
    public required Authenticator Authenticator { get; init; }
    public required SessionLog Log { get; init; }
    public required CertificateProvider Certificates { get; init; }
    public string HostName { get; init; } = "fakeserver.local";
    public string HttpBaseUrl { get; set; } = "http://localhost:5080";
    public string? HttpsBaseUrl { get; set; }

    public string OpenIdConfigurationUrl => (HttpsBaseUrl ?? HttpBaseUrl) + "/.well-known/openid-configuration";
}
