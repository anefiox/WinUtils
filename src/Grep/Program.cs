using WinUtils.Common.IO;
using WinUtils.Grep;

using var stdout = OutputSink.OpenStdout();
using var stderr = OutputSink.OpenStderr();
try
{
    return GrepCommand.Run(args, Console.OpenStandardInput(), stdout, stderr, FileIdentity.OfStdout());
}
catch (OutputClosedException)
{
    // The reader went away (e.g. "grep ... | head"); there's no one left to tell.
    return 0;
}
