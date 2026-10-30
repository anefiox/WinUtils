using WinUtils.Common.IO;
using WinUtils.Xargs;

using var stdout = OutputSink.OpenStdout();
using var stderr = OutputSink.OpenStderr();
try
{
    return XargsCommand.Run(args, Console.OpenStandardInput(), stdout, stderr, ReadAnswerFromConsole);
}
catch (OutputClosedException)
{
    // The reader went away; there's no one left to tell.
    return 0;
}

// -p reads its answers from the console, because standard input is the list of items.
static string? ReadAnswerFromConsole()
{
    try
    {
        using var console = new FileStream(@"\\.\CONIN$", FileMode.Open, FileAccess.Read);
        using var reader = new StreamReader(console);
        return reader.ReadLine();
    }
    catch (IOException)
    {
        return null;
    }
}
