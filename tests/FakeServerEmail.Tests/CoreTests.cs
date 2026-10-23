using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using FakeServerEmail.Core;

namespace FakeServerEmail.Tests;

public sealed class CoreTests
{
    private static readonly MessageOrigin Origin = new("teste", "a@teste.local", ["b@teste.local"], null, null);

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"fse-core-{Guid.NewGuid():N}");

    private static byte[] RawMessage(string subject) => Encoding.ASCII.GetBytes($"Subject: {subject}\r\nFrom: a@teste.local\r\nTo: b@teste.local\r\n\r\ncorpo\r\n");

    [Fact]
    public void Encerramento_forcado_nao_reaproveita_UIDs_e_encerramento_limpo_mantem_a_sequencia()
    {
        var data = NewDirectory();
        try
        {
            uint nextBeforeCrash;
            string index;
            using (var store = new MessageStore(data))
            {
                store.Add(RawMessage("antes da queda"), Origin);
                nextBeforeCrash = store.NextUid;
                index = Path.Combine(store.MessagesDirectory, DataFiles.MessageIndex);
            }
            var state = JsonNode.Parse(File.ReadAllText(index))!;
            Assert.True(state["cleanShutdown"]!.GetValue<bool>());
            state["cleanShutdown"] = false;
            File.WriteAllText(index, state.ToJsonString());

            uint nextAfterCrash;
            using (var afterCrash = new MessageStore(data))
                nextAfterCrash = afterCrash.NextUid;
            Assert.True(nextAfterCrash >= nextBeforeCrash + 1000);

            using var reopened = new MessageStore(data);
            Assert.Equal(nextAfterCrash, reopened.NextUid);
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }

    [Fact]
    public void Apagar_pasta_apaga_as_mensagens_das_subpastas()
    {
        var data = NewDirectory();
        try
        {
            using var store = new MessageStore(data);
            store.CreateFolder("Projetos");
            store.CreateFolder("Projetos/2026");
            store.Add(RawMessage("na pasta"), Origin, "Projetos");
            store.Add(RawMessage("na subpasta"), Origin, "Projetos/2026");
            store.Add(RawMessage("na caixa de entrada"), Origin);

            Assert.True(store.DeleteFolder("projetos"));

            Assert.Equal(["na caixa de entrada"], store.List().Select(m => m.Subject));
            Assert.DoesNotContain(store.Folders, f => f.StartsWith("Projetos", StringComparison.OrdinalIgnoreCase));
            Assert.Single(Directory.GetFiles(store.MessagesDirectory, "*.eml"));
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }

    [Fact]
    public void Comando_de_administrador_so_aceita_o_certificado_do_proprio_programa()
    {
        Assert.False(ComputerTrust.TryRunCommand(["--data", "x"], out _));

        Assert.True(ComputerTrust.TryRunCommand([ComputerTrust.InstallArgument, "isto-nao-e-base64"], out var invalid));
        Assert.Equal(2, invalid);

        using var rsa = RSA.Create(2048);
        var foreignRequest = new CertificateRequest("CN=localhost, O=Outra Empresa", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var foreign = foreignRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        Assert.True(ComputerTrust.TryRunCommand([ComputerTrust.InstallArgument, Convert.ToBase64String(foreign.Export(X509ContentType.Cert))], out var refused));
        Assert.Equal(2, refused);

        var data = NewDirectory();
        Directory.CreateDirectory(data);
        try
        {
            using var own = new CertificateProvider(data);
            Assert.True(CertificateProvider.IsOwnCertificate(own.Certificate));
            Assert.False(CertificateProvider.IsOwnCertificate(foreign));
        }
        finally
        {
            ServerFixture.DeleteDirectory(data);
        }
    }
}
