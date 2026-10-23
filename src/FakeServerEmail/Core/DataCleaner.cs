namespace FakeServerEmail.Core;

public static class DataCleaner
{
    private const int Attempts = 10;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    public static async Task<List<string>> RemoveOwnedDataAsync(string dataDirectory)
    {
        var failures = new List<string>();
        foreach (var name in DataFiles.OwnedDirectories)
            await TryAsync(Path.Combine(dataDirectory, name), path =>
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }, failures);

        foreach (var name in DataFiles.OwnedFiles)
            await TryAsync(Path.Combine(dataDirectory, name), File.Delete, failures);

        if (Directory.Exists(dataDirectory))
            foreach (var probe in Directory.EnumerateFiles(dataDirectory, DataFiles.WriteProbePrefix + "*"))
                await TryAsync(probe, File.Delete, failures);

        await TryAsync(dataDirectory, path =>
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }, failures);

        RemoveOldExtractions();
        return failures;
    }

    private static void RemoveOldExtractions()
    {
        if (!Directory.Exists(DataFiles.ExtractionDirectory))
            return;
        try
        {
            Directory.Delete(DataFiles.ExtractionDirectory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task TryAsync(string path, Action<string> action, List<string> failures)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < Attempts)
            {
                await Task.Delay(RetryDelay);
            }
            catch (Exception ex)
            {
                failures.Add($"{path}: {ex.Message}");
                return;
            }
        }
    }
}
