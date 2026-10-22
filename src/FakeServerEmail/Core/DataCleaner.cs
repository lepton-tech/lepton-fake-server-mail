namespace FakeServerEmail.Core;

public static class DataCleaner
{
    private static readonly string[] OwnedFiles =
    [
        "settings.json",
        "settings.json.invalido",
        "oauth-tokens.json",
        "oauth-chave-privada.pem",
        "certificado-localhost.pfx",
        "em-execucao.json",
        ".teste-escrita",
    ];

    private static readonly string[] OwnedDirectories = ["mensagens"];

    public static List<string> RemoveOwnedData(string dataDirectory)
    {
        var failures = new List<string>();
        foreach (var name in OwnedDirectories)
            Try(Path.Combine(dataDirectory, name), path =>
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }, failures);

        foreach (var name in OwnedFiles)
            Try(Path.Combine(dataDirectory, name), File.Delete, failures);

        Try(dataDirectory, path =>
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }, failures);
        return failures;
    }

    private static void Try(string path, Action<string> action, List<string> failures)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                action(path);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                failures.Add($"{path}: {ex.Message}");
                return;
            }
        }
    }
}
