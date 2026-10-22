using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FakeServerEmail.Core;

public sealed class CertificateProvider : IDisposable
{
    private readonly string _pfxPath;

    public CertificateProvider(string dataDirectory)
    {
        _pfxPath = Path.Combine(dataDirectory, "certificado-localhost.pfx");
        Certificate = LoadOrCreate();
    }

    public X509Certificate2 Certificate { get; }

    public byte[] PublicCertificate => Certificate.Export(X509ContentType.Cert);

    public bool IsTrusted()
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, Certificate.Thumbprint, false).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Trust()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        using var publicOnly = X509CertificateLoader.LoadCertificate(PublicCertificate);
        store.Add(publicOnly);
    }

    public void Untrust()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in store.Certificates.Find(X509FindType.FindByThumbprint, Certificate.Thumbprint, false))
            store.Remove(cert);
    }

    public void Dispose() => Certificate.Dispose();

    private X509Certificate2 LoadOrCreate()
    {
        try
        {
            if (File.Exists(_pfxPath))
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, null, X509KeyStorageFlags.Exportable);
                if (existing.NotAfter > DateTime.Now.AddDays(7) && existing.HasPrivateKey)
                    return existing;
            }
        }
        catch
        {
        }

        var pfx = Create();
        try { File.WriteAllBytes(_pfxPath, pfx); } catch { }
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    private static byte[] Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost, O=Fake Server Email", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        try
        {
            san.AddDnsName(Environment.MachineName.ToLowerInvariant());
            san.AddDnsName("host.docker.internal");
        }
        catch
        {
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        return cert.Export(X509ContentType.Pfx);
    }
}
