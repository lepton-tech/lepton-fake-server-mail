using FakeServerEmail.Core;

namespace FakeServerEmail.Hosting;

public static class DataDirectoryResolver
{
    private const string FolderName = "FakeServerEmail";
    private const string PortableFolderName = "FakeServerEmail-dados";

    public static string Resolve(string? requested)
    {
        foreach (var candidate in Candidates(requested))
        {
            if (IsWritable(candidate))
                return candidate;
        }
        return Path.GetTempPath();
    }

    private static IEnumerable<string> Candidates(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            yield return SingleInstance.NormalizeDirectory(requested);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, FolderName);
        yield return Path.Combine(AppContext.BaseDirectory, PortableFolderName);
        yield return Path.Combine(Path.GetTempPath(), FolderName);
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $"{DataFiles.WriteProbePrefix}-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
