namespace WinUtils.FindW;

/// <summary>A file or directory visited during the walk.</summary>
internal sealed class FileEntry
{
    private readonly FileSystemInfo? _target;

    public FileEntry(FileSystemInfo info, string path, string startPoint, int depth, bool followLinks)
    {
        Info = info;
        Path = path;
        StartPoint = startPoint;
        Depth = depth;
        // Symlinks and junctions are reparse points with a link target; other reparse points
        // (OneDrive placeholders, dedup) are ordinary files and folders.
        IsLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;
        if (IsLink && followLinks)
        {
            try
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is { Exists: true }) _target = target;
            }
            catch (IOException) { } // a broken link stays a link
        }
        else if (!IsLink)
        {
            _target = info;
        }
    }

    public FileSystemInfo Info { get; }

    /// <summary>The path as printed: the starting point joined with the names below it.</summary>
    public string Path { get; }

    public string StartPoint { get; }

    public int Depth { get; }

    public bool IsLink { get; }

    /// <summary>What tests like -size and -mtime look at: the link target when following links.</summary>
    public FileSystemInfo Stat => _target ?? Info;

    /// <summary>'d' directory, 'f' file, or 'l' for a link that isn't being followed (or is broken).</summary>
    public char Type => _target is null ? 'l' : _target is DirectoryInfo ? 'd' : 'f';

    public bool IsDirectory => Type == 'd';

    public string Name
    {
        get
        {
            if (Depth > 0) return Info.Name;
            string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
            return name.Length > 0 ? name : Path; // e.g. "C:\"
        }
    }

    public long Size => Stat is FileInfo file ? file.Length : 0;
}
