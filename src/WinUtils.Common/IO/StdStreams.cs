using Microsoft.Win32.SafeHandles;
using WinUtils.Common.Interop;

namespace WinUtils.Common.IO;

public static class StdStreams
{
    /// <summary>
    /// Standard input. When it's redirected from a file ("tail &lt; big.log") this is a seekable
    /// FileStream, so tools can jump to the end instead of reading everything.
    /// </summary>
    public static Stream OpenStdin()
    {
        if (OperatingSystem.IsWindows())
        {
            nint handle = Kernel32.GetStdHandle(Kernel32.STD_INPUT_HANDLE);
            if (Kernel32.GetFileType(handle) == Kernel32.FILE_TYPE_DISK)
                return new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Read, bufferSize: 0);
        }
        return Console.OpenStandardInput();
    }
}
