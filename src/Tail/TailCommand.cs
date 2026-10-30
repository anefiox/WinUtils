using System.Globalization;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using WinUtils.Common.Text;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Tail;

public enum FollowMode { None, Descriptor, Name }

internal sealed record TailOptions(
    bool Bytes, long Count, bool FromStart, char Delimiter,
    FollowMode Follow, bool Retry, TimeSpan Interval, int? Pid, bool Headers);

public static class TailCommand
{
    private const string TryHelp = "Try 'tail --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("c", 'c', "bytes", Required),
        new("n", 'n', "lines", Required),
        new("f", 'f'),
        new("follow", null, "follow", Optional),
        new("F", 'F'),
        new("retry", null, "retry"),
        new("s", 's', "sleep-interval", Required),
        new("pid", null, "pid", Required),
        new("q", 'q', "quiet"),
        new("q", null, "silent"),
        new("v", 'v', "verbose"),
        new("z", 'z', "zero-terminated"),
        new("ignored", null, "max-unchanged-stats", Required),
        new("help", null, "help"),
        new("version", null, "version"),
    ], allowNumericOptions: true);

    /// <summary>Runs tail and returns its exit status: 0, or 1 if any input couldn't be read.</summary>
    /// <param name="cancel">Stops -f; the real program runs until Ctrl+C or --pid exits.</param>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr, CancellationToken cancel = default)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"tail: {message}\n");
            stderr.Flush();
        }

        bool bytes = false, fromStart = false, retry = false, quiet = false, verbose = false;
        long count = 10;
        char delimiter = '\n';
        var follow = FollowMode.None;
        // GNU polls once a second as a fallback to inotify; Windows has no inotify, so poll faster.
        var interval = TimeSpan.FromMilliseconds(250);
        int? pid = null;
        ParseResult parsed;
        try
        {
            parsed = Parser.Parse(args);
            foreach (var (id, value, _) in parsed.Options)
            {
                switch (id)
                {
                    case "c" or "n" or OptionParser.NumericId:
                        bytes = id == "c";
                        string number = value!;
                        // "+N" starts at line (or byte) N; "-N" is the same as N.
                        fromStart = number.StartsWith('+');
                        if (number.Length > 0 && number[0] is '-' or '+') number = number[1..];
                        if (!Sizes.TryParse(number, out count))
                            throw new OptionException($"invalid number of {(bytes ? "bytes" : "lines")}: '{value}'");
                        break;
                    case "f":
                        follow = FollowMode.Descriptor;
                        break;
                    case "follow":
                        follow = value switch
                        {
                            null or "descriptor" => FollowMode.Descriptor,
                            "name" => FollowMode.Name,
                            _ => throw new OptionException($"invalid argument '{value}' for '--follow'"),
                        };
                        break;
                    case "F":
                        follow = FollowMode.Name;
                        retry = true;
                        break;
                    case "retry": retry = true; break;
                    case "s":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
                            throw new OptionException($"invalid number of seconds: '{value}'");
                        interval = TimeSpan.FromSeconds(seconds);
                        break;
                    case "pid":
                        pid = int.TryParse(value, out int p) ? p : throw new OptionException($"invalid PID: '{value}'");
                        break;
                    case "q": (quiet, verbose) = (true, false); break;
                    case "v": (quiet, verbose) = (false, true); break;
                    case "z": delimiter = '\0'; break;
                    case "help":
                        stdout.Write(HelpText.Text);
                        return 0;
                    case "version":
                        stdout.Write(HelpText.Version);
                        return 0;
                }
            }
        }
        catch (OptionException e)
        {
            Error(e.Message);
            stderr.Write(TryHelp);
            return 1;
        }

        var files = Wildcards.ExpandAll(parsed.Operands);
        if (files.Count == 0) files.Add("-");
        // GNU accepts the old -NUM form only for a single file.
        if (files.Count > 1 && parsed.Options.FirstOrDefault(p => p.Id == OptionParser.NumericId) is { Value: { } digits })
        {
            Error($"option used in invalid context -- {digits[0]}");
            return 1;
        }
        // Like GNU tail, asking for nothing (without -f) reads nothing: no headers, no errors.
        if (count == 0 && !fromStart && follow == FollowMode.None) return 0;
        var o = new TailOptions(bytes, count, fromStart, delimiter, follow, retry, interval, pid,
            Headers: verbose || (files.Count > 1 && !quiet));

        var followed = new List<Followed>();
        bool firstHeader = true;
        int status = 0;
        foreach (string file in files)
        {
            string display = file == "-" ? "standard input" : file;
            Stream stream;
            try
            {
                stream = file == "-" ? stdin : Followed.Open(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error(Directory.Exists(file)
                    ? $"error reading '{file}': Is a directory"
                    : $"cannot open '{file}' for reading: {Describe(e)}");
                status = 1;
                // -F keeps watching for a file that doesn't exist yet.
                if (o.Follow == FollowMode.Name && o.Retry && file != "-") followed.Add(new Followed(file, display, stdout));
                continue;
            }

            if (o.Headers)
            {
                stdout.Write($"{(firstHeader ? "" : "\n")}==> {display} <==\n");
                firstHeader = false;
            }
            var writer = new ByteTextWriter(stdout);
            try
            {
                var lines = Output(stream, writer, o);
                // Only real files can be followed; a pipe has nothing more to say once it ends.
                if (o.Follow != FollowMode.None && stream is FileStream { CanSeek: true } fs && lines is not null)
                {
                    followed.Add(new Followed(file, display, stdout, fs, lines, writer));
                    continue;
                }
            }
            catch (IOException e)
            {
                Error($"error reading '{display}': {e.Message}");
                status = 1;
            }
            if (stream != stdin) stream.Dispose();
            stdout.Flush();
        }

        stdout.Flush();
        if (followed.Count > 0) new Follower(followed, o, stdout, Error).Run(cancel);
        return status;
    }

    /// <summary>Prints the requested part of an input; returns its delimiters, or null if it was empty.</summary>
    private static ByteLines? Output(Stream stream, ByteTextWriter writer, TailOptions o)
    {
        var head = new byte[64 * 1024];
        if (stream.CanSeek) stream.Position = 0;
        int length = stream.Read(head, 0, head.Length);
        var lines = ByteLines.Detect(head.AsSpan(0, length), o.Delimiter);
        writer.Start(lines);
        if (length == 0) return lines;

        if (!stream.CanSeek)
        {
            if (o.FromStart)
            {
                CopyFromStart(stream, head, length, lines, writer, o);
                return null;
            }
            // Keep only enough of the pipe to hold the last N lines, then treat it like a file.
            stream = Retain(stream, head, length, lines, o);
        }

        long dataStart = writer.Decoding ? lines.PreambleLength : 0;
        long end = stream.Length;
        long start;
        if (o.Bytes)
        {
            start = o.FromStart ? Math.Max(0, o.Count - 1) : end - o.Count;
            start = Math.Max(dataStart, start);
            if (writer.Decoding) start -= (start - dataStart) % lines.Unit; // whole UTF-16 code units
        }
        else
        {
            start = o.FromStart
                ? lines.SkipLines(stream, o.Count - 1, dataStart)
                : lines.FindStartOfLastLines(stream, o.Count, dataStart);
        }
        writer.CopyRange(stream, start, end);
        return lines;
    }

    /// <summary>"+N" on a pipe: skip N-1 lines or bytes as they stream past, then copy the rest.</summary>
    private static void CopyFromStart(Stream stream, byte[] buffer, int length, ByteLines lines, ByteTextWriter writer, TailOptions o)
    {
        long skip = Math.Max(0, o.Count - 1);
        int start = writer.Decoding ? Math.Min(lines.PreambleLength, length) : 0;
        long offset = 0;
        while (true)
        {
            var span = buffer.AsSpan(start, length - start);
            if (skip > 0 && o.Bytes)
            {
                int take = (int)Math.Min(span.Length, skip);
                skip -= take;
                span = span[take..];
            }
            else if (skip > 0)
            {
                int pos = 0;
                for (int idx; skip > 0 && (idx = lines.IndexOf(span[pos..], offset + start + pos)) >= 0; skip--)
                    pos += idx + lines.Unit;
                span = skip == 0 ? span[pos..] : [];
            }
            if (skip == 0) writer.Write(span);

            offset += length;
            start = 0;
            length = stream.Read(buffer, 0, buffer.Length);
            if (length == 0) return;
        }
    }

    /// <summary>Reads a pipe to the end, keeping only the trailing blocks that hold the last N lines or bytes.</summary>
    private static MemoryStream Retain(Stream stream, byte[] head, int length, ByteLines lines, TailOptions o)
    {
        var blocks = new Queue<(byte[] Data, long Delimiters)>();
        long totalBytes = 0, totalDelimiters = 0, offset = 0;
        void Add(byte[] block)
        {
            long delimiters = o.Bytes ? 0 : lines.Count(block, offset);
            offset += block.Length;
            blocks.Enqueue((block, delimiters));
            totalBytes += block.Length;
            totalDelimiters += delimiters;
            // UTF-16 keeps everything: dropping an odd-sized block would misalign the code units.
            while (blocks.Count > 1 && !lines.IsUtf16)
            {
                var first = blocks.Peek();
                bool enough = o.Bytes
                    ? totalBytes - first.Data.Length >= o.Count
                    : totalDelimiters - first.Delimiters >= o.Count + 1;
                if (!enough) break;
                blocks.Dequeue();
                totalBytes -= first.Data.Length;
                totalDelimiters -= first.Delimiters;
            }
        }

        Add(head[..length]);
        var buffer = new byte[64 * 1024];
        for (int n; (n = stream.Read(buffer, 0, buffer.Length)) > 0;) Add(buffer[..n]);

        var memory = new MemoryStream((int)totalBytes);
        foreach (var (data, _) in blocks) memory.Write(data);
        return memory;
    }

    internal static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };
}
