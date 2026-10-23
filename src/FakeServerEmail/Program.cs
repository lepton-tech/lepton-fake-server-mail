using FakeServerEmail.Core;
using FakeServerEmail.Hosting;
using FakeServerEmail.Mcp;

if (ComputerTrust.TryRunCommand(args, out var trustExitCode))
    return trustExitCode;

var options = CommandLineOptions.Parse(args);
var dataDirectory = DataDirectoryResolver.Resolve(options.DataDirectory);
return options.Mcp
    ? await McpStdioHost.RunAsync(dataDirectory)
    : await ConsoleHost.RunAsync(dataDirectory, options.OpenBrowser);
