namespace FakeServerEmail.Core;

public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var temporary = path + DataFiles.TemporarySuffix;
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, true);
    }
}
