using Microsoft.Win32.SafeHandles;
using WinUtils.Common.Interop;

namespace WinUtils.Common.IO;

/// <summary>Identifies a file on disk independent of the path used to open it.</summary>
public readonly record struct FileIdentity(uint Volume, ulong Index)
{
    public static FileIdentity? Of(SafeFileHandle handle)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return Of(handle.DangerousGetHandle());
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    /// <summary>The file standard output is redirected to, or null for a console or pipe.</summary>
    public static FileIdentity? OfStdout() =>
        OperatingSystem.IsWindows() ? Of(Kernel32.GetStdHandle(Kernel32.STD_OUTPUT_HANDLE)) : null;

    private static FileIdentity? Of(nint handle)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (Kernel32.GetFileType(handle) != Kernel32.FILE_TYPE_DISK) return null;
        if (!Kernel32.GetFileInformationByHandle(handle, out var info)) return null;
        return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }
}
