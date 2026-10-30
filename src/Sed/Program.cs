using WinUtils.Common.IO;
using WinUtils.Sed;

using var stdout = OutputSink.OpenStdout();
using var stderr = OutputSink.OpenStderr();
try
{
    return SedCommand.Run(args, Console.OpenStandardInput(), stdout, stderr);
}
catch (OutputClosedException)
{
    // The reader went away (e.g. "sed ... | head"); there's no one left to tell.
    return 0;
}
