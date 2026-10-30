using System.Diagnostics;
using WinUtils.Common.IO;

namespace WinUtils.Tail;

/// <summary>A file being followed with -f or -F.</summary>
internal sealed class Followed
{
    public Followed(string path, string display, OutputSink sink)
    {
        Path = path;
        Display = display;
        Writer = new ByteTextWriter(sink);
        Missing = true;
    }

    public Followed(string path, string display, OutputSink sink, FileStream stream, ByteLines lines, ByteTextWriter writer)
    {
        Path = path;
        Display = display;
        Writer = writer;
        Stream = stream;
        Lines = lines;
        Identity = FileIdentity.Of(stream.SafeFileHandle);
        Position = stream.Length;
    }

    public string Path { get; }
    public string Display { get; }
    public ByteTextWriter Writer { get; }
    public FileStream? Stream { get; set; }
    public ByteLines? Lines { get; set; }
    public FileIdentity? Identity { get; set; }
    public long Position { get; set; }
    /// <summary>The file didn't exist (or was inaccessible) when last checked.</summary>
    public bool Missing { get; set; }

    /// <summary>Opens with full sharing so writers and log rotation aren't blocked by us.</summary>
    public static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
}

/// <summary>
/// Polls followed files for new data. In descriptor mode (-f) a renamed file is still followed.
/// In name mode (-F) the name is followed: after rotation or deletion it switches to the new
/// file once one appears.
/// </summary>
internal sealed class Follower(List<Followed> files, TailOptions o, OutputSink stdout, Action<string> error)
{
    private Followed? _lastPrinted = files.LastOrDefault(f => f.Stream is not null);

    public void Run(CancellationToken cancel)
    {
        while (true)
        {
            bool stop = o.Pid is int pid && !IsRunning(pid);
            foreach (var f in files)
            {
                if (o.Follow == FollowMode.Name) CheckName(f);
                Poll(f);
            }
            // After --pid exits, the pass above picked up its final writes.
            if (stop || cancel.WaitHandle.WaitOne(o.Interval)) return;
        }
    }

    private void Poll(Followed f)
    {
        if (f.Stream is null) return;
        long length;
        try
        {
            length = f.Stream.Length;
        }
        catch (IOException)
        {
            return;
        }

        if (length < f.Position)
        {
            error($"{f.Display}: file truncated");
            f.Position = 0;
        }
        if (length == f.Position) return;

        if (f.Lines is null || f.Position == 0)
        {
            // A file that was empty (or just reappeared) gets its encoding detected now.
            var head = new byte[Math.Min(4096, length)];
            f.Stream.Position = 0;
            f.Stream.ReadExactly(head);
            f.Lines = ByteLines.Detect(head, o.Delimiter);
            f.Writer.Start(f.Lines);
            if (f.Position == 0 && f.Writer.Decoding) f.Position = f.Lines.PreambleLength;
        }

        if (o.Headers && _lastPrinted != f)
        {
            stdout.Write($"\n==> {f.Display} <==\n");
            _lastPrinted = f;
        }
        f.Writer.CopyRange(f.Stream, f.Position, length);
        f.Position = length;
        stdout.Flush();
    }

    private void CheckName(Followed f)
    {
        if (!File.Exists(f.Path))
        {
            if (!f.Missing)
            {
                Poll(f); // anything written just before it went away
                error($"'{f.Display}' has become inaccessible: No such file or directory");
                f.Stream?.Dispose();
                f.Stream = null;
                f.Missing = true;
            }
            return;
        }

        FileStream fresh;
        try
        {
            fresh = Followed.Open(f.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var identity = FileIdentity.Of(fresh.SafeFileHandle);

        if (f.Stream is null)
        {
            error($"'{f.Display}' has appeared;  following new file");
        }
        else if (identity is not null && identity != f.Identity)
        {
            Poll(f); // finish the old file first
            error($"'{f.Display}' has been replaced;  following new file");
            f.Stream.Dispose();
        }
        else
        {
            fresh.Dispose();
            return;
        }
        f.Stream = fresh;
        f.Identity = identity;
        f.Position = 0;
        f.Lines = null;
        f.Missing = false;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
