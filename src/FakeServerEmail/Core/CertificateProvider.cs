using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FakeServerEmail.Core;

public enum CertificateTrust
{
    None,
    User,
    Computer,
}

public sealed class CertificateProvider : IDisposable
{
    public const string SubjectName = "CN=localhost, O=Fake Server Email";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const int KeySize = 2048;
    private const int ValidityYears = 5;
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(7);
    private static readonly TimeSpan TrustCacheDuration = TimeSpan.FromSeconds(5);

    private readonly string _pfxPath;
    private readonly Lock _trustLock = new();
    private CertificateTrust _trust;
    private DateTimeOffset _trustCheckedAt = DateTimeOffset.MinValue;

    public CertificateProvider(string dataDirectory)
    {
        _pfxPath = Path.Combine(dataDirectory, DataFiles.Certificate);
        Certificate = LoadOrCreate();
    }

    public X509Certificate2 Certificate { get; }

    public byte[] PublicCertificate => Certificate.Export(X509ContentType.Cert);

    public CertificateTrust Trust
    {
        get
        {
            lock (_trustLock)
            {
                if (DateTimeOffset.UtcNow - _trustCheckedAt > TrustCacheDuration)
                {
                    _trust = ReadTrust();
                    _trustCheckedAt = DateTimeOffset.UtcNow;
                }
                return _trust;
            }
        }
    }

    public void TrustForUser()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        using var publicOnly = X509CertificateLoader.LoadCertificate(PublicCertificate);
        store.Add(publicOnly);
        InvalidateTrust();
    }

    public void UntrustForUser()
    {
        var onComputer = ThumbprintsOf(StoreLocation.LocalMachine);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in store.Certificates.Where(c => IsOwnCertificate(c) && !onComputer.Contains(c.Thumbprint)))
            store.Remove(cert);
        InvalidateTrust();
    }

    public async Task TrustForComputerAsync(CancellationToken ct)
    {
        try
        {
            await ComputerTrust.RunElevatedAsync($"{ComputerTrust.InstallArgument} {Convert.ToBase64String(PublicCertificate)}", ct);
        }
        finally
        {
            InvalidateTrust();
        }
    }

    public async Task UntrustForComputerAsync(CancellationToken ct)
    {
        try
        {
            await ComputerTrust.RunElevatedAsync(ComputerTrust.RemoveArgument, ct);
        }
        finally
        {
            InvalidateTrust();
        }
    }

    public static bool IsOwnCertificate(X509Certificate2 cert)
    {
        if (cert.Subject != SubjectName || cert.Issuer != SubjectName)
            return false;
        var constraints = cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        var usages = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return constraints is { CertificateAuthority: false }
            && usages is not null
            && usages.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ServerAuthenticationOid);
    }

    public void Dispose() => Certificate.Dispose();

    private void InvalidateTrust()
    {
        lock (_trustLock)
            _trustCheckedAt = DateTimeOffset.MinValue;
    }

    private CertificateTrust ReadTrust()
    {
        if (ThumbprintsOf(StoreLocation.LocalMachine).Contains(Certificate.Thumbprint))
            return CertificateTrust.Computer;
        return ThumbprintsOf(StoreLocation.CurrentUser).Contains(Certificate.Thumbprint) ? CertificateTrust.User : CertificateTrust.None;
    }

    private static HashSet<string> ThumbprintsOf(StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Where(IsOwnCertificate).Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    private X509Certificate2 LoadOrCreate()
    {
        if (File.Exists(_pfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.Now.Add(RenewBefore) && existing.HasPrivateKey)
                    return existing;
                existing.Dispose();
            }
            catch (CryptographicException)
            {
            }
        }

        var pfx = Create();
        try
        {
            File.WriteAllBytes(_pfxPath, pfx);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    private static byte[] Create()
    {
        using var rsa = RSA.Create(KeySize);
        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        san.AddDnsName("host.docker.internal");
        try
        {
            san.AddDnsName(Environment.MachineName.ToLowerInvariant());
        }
        catch (ArgumentException)
        {
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthenticationOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(ValidityYears));
        return cert.Export(X509ContentType.Pfx);
    }
}
