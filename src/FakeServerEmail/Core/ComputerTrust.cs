using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FakeServerEmail.Core;

public static class ComputerTrust
{
    public const string InstallArgument = "--trust-computer-certificate";
    public const string RemoveArgument = "--untrust-computer-certificate";

    private const int Success = 0;
    private const int InvalidCertificate = 2;
    private const int AccessDenied = 3;
    private const int StoreFailure = 4;
    private const int CancelledByUser = 1223;
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int AccessDeniedWin32 = 5;

    public static bool TryRunCommand(string[] args, out int exitCode)
    {
        exitCode = args switch
        {
            [InstallArgument, var certificate] => Install(certificate),
            [RemoveArgument] => Remove(),
            _ => -1,
        };
        return exitCode >= 0;
    }

    public static async Task RunElevatedAsync(string arguments, CancellationToken ct)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Não foi possível localizar o FakeServerEmail.exe para pedir permissão de administrador.");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == CancelledByUser)
        {
            throw new InvalidOperationException("A permissão de administrador não foi concedida, então nada foi alterado.", ex);
        }

        using (process)
        {
            if (process is null)
                throw new InvalidOperationException("O Windows não iniciou o pedido de permissão de administrador.");
            await process.WaitForExitAsync(ct);
            var message = process.ExitCode switch
            {
                Success => null,
                InvalidCertificate => "O certificado recebido não é o do Fake Server Email, então nada foi alterado.",
                AccessDenied => "O Windows não deu acesso ao repositório de certificados do computador.",
                _ => "Não foi possível alterar o repositório de certificados do computador.",
            };
            if (message is not null)
                throw new InvalidOperationException(message);
        }
    }

    private static int Install(string base64)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return InvalidCertificate;
        }

        using (cert)
        {
            if (!CertificateProvider.IsOwnCertificate(cert))
                return InvalidCertificate;
            return WithComputerRootStore(store => store.Add(cert));
        }
    }

    private static int Remove() => WithComputerRootStore(store =>
    {
        foreach (var cert in store.Certificates.Where(CertificateProvider.IsOwnCertificate))
            store.Remove(cert);
    });

    private static int WithComputerRootStore(Action<X509Store> change)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            change(store);
            return Success;
        }
        catch (CryptographicException ex) when (ex.HResult is AccessDeniedHResult or AccessDeniedWin32)
        {
            return AccessDenied;
        }
        catch (CryptographicException)
        {
            return StoreFailure;
        }
    }
}
