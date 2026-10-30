using WinUtils.Common.IO;
using WinUtils.Wc;

using var stdout = OutputSink.OpenStdout();
using var stderr = OutputSink.OpenStderr();
try
{
    return WcCommand.Run(args, StdStreams.OpenStdin(), stdout, stderr);
}
catch (OutputClosedException)
{
    // The reader went away; there's no one left to tell.
    return 0;
}
