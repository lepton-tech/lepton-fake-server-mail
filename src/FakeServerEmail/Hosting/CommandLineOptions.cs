namespace FakeServerEmail.Hosting;

public sealed record CommandLineOptions(string? DataDirectory, bool Mcp, bool OpenBrowser)
{
    public const string DataArgument = "--data";
    public const string NoBrowserArgument = "--no-browser";

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        var dataIndex = IndexOf(args, DataArgument);
        return new CommandLineOptions(
            dataIndex >= 0 && dataIndex + 1 < args.Count ? args[dataIndex + 1] : null,
            IndexOf(args, FakeMailServer.McpArgument) >= 0,
            IndexOf(args, NoBrowserArgument) < 0);
    }

    private static int IndexOf(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
