using WinUtils.Common.IO;
using WinUtils.Head;

using var stdout = OutputSink.OpenStdout();
using var stderr = OutputSink.OpenStderr();
try
{
    return HeadCommand.Run(args, StdStreams.OpenStdin(), stdout, stderr);
}
catch (OutputClosedException)
{
    // The reader went away; there's no one left to tell.
    return 0;
}
