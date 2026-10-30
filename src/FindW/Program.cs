using WinUtils.Common.IO;
using WinUtils.FindW;

using var stdout = OutputSink.OpenStdout();
using var stderr = OutputSink.OpenStderr();
try
{
    return FindCommand.Run(args, stdout, stderr);
}
catch (OutputClosedException)
{
    // The reader went away (e.g. "findw | head"); there's no one left to tell.
    return 0;
}
