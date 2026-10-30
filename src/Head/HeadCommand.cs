using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using WinUtils.Common.Text;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Head;

public static class HeadCommand
{
    private const string TryHelp = "Try 'head --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("c", 'c', "bytes", Required),
        new("n", 'n', "lines", Required),
        new("q", 'q', "quiet"),
        new("q", null, "silent"),
        new("v", 'v', "verbose"),
        new("z", 'z', "zero-terminated"),
        new("help", null, "help"),
        new("version", null, "version"),
    ], allowNumericOptions: true);

    private sealed record Options(bool Bytes, long Count, bool AllButLast, char Delimiter);

    /// <summary>Runs head and returns its exit status: 0, or 1 if any input couldn't be read.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"head: {message}\n");
            stderr.Flush();
        }

        bool bytes = false, allButLast = false, quiet = false, verbose = false;
        long count = 10;
        char delimiter = '\n';
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
                        // "-n -5" means all but the last 5; a leading '+' is accepted and ignored.
                        allButLast = number.StartsWith('-');
                        if (number.Length > 0 && number[0] is '-' or '+') number = number[1..];
                        if (!Sizes.TryParse(number, out count))
                            throw new OptionException($"invalid number of {(bytes ? "bytes" : "lines")}: '{value}'");
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
        bool headers = verbose || (files.Count > 1 && !quiet);
        var options = new Options(bytes, count, allButLast, delimiter);
        var writer = new ByteTextWriter(stdout);
        bool firstHeader = true;
        int status = 0;

        foreach (string file in files)
        {
            string display = file == "-" ? "standard input" : file;
            Stream stream;
            try
            {
                stream = file == "-" ? stdin : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error(Directory.Exists(file)
                    ? $"error reading '{file}': Is a directory"
                    : $"cannot open '{file}' for reading: {Describe(e)}");
                status = 1;
                continue;
            }

            if (headers)
            {
                stdout.Write($"{(firstHeader ? "" : "\n")}==> {display} <==\n");
                firstHeader = false;
            }
            try
            {
                Copy(stream, writer, options);
            }
            catch (IOException e)
            {
                Error($"error reading '{display}': {e.Message}");
                status = 1;
            }
            finally
            {
                if (stream != stdin) stream.Dispose();
            }
            stdout.Flush();
        }
        return status;
    }

    private static void Copy(Stream stream, ByteTextWriter writer, Options o)
    {
        var buffer = new byte[64 * 1024];
        int length = stream.Read(buffer, 0, buffer.Length);
        if (length == 0) return;
        var lines = ByteLines.Detect(buffer.AsSpan(0, length), o.Delimiter);
        writer.Start(lines);
        // When output is decoded (UTF-16, or a console), the byte-order mark isn't copied.
        int dataStart = writer.Decoding ? Math.Min(lines.PreambleLength, length) : 0;

        if (o.AllButLast)
        {
            // Everything except the last N lines or bytes: find where they start, then copy up to it.
            var seekable = stream.CanSeek ? stream : Buffer(buffer, length, stream);
            long end = o.Bytes
                ? Math.Max(dataStart, seekable.Length - o.Count)
                : lines.FindStartOfLastLines(seekable, o.Count, dataStart);
            writer.CopyRange(seekable, dataStart, end);
            return;
        }

        long remaining = o.Bytes ? o.Count - dataStart : o.Count;
        long offset = 0; // file offset of buffer[0]
        int start = dataStart;
        while (remaining > 0)
        {
            var span = buffer.AsSpan(start, length - start);
            if (o.Bytes)
            {
                int take = (int)Math.Min(span.Length, remaining);
                writer.Write(span[..take]);
                remaining -= take;
                start += take;
            }
            else
            {
                int pos = 0;
                for (int idx; remaining > 0 && (idx = lines.IndexOf(span[pos..], offset + start + pos)) >= 0;)
                {
                    pos += idx + lines.Unit;
                    remaining--;
                }
                if (remaining == 0)
                {
                    writer.Write(span[..pos]);
                    return;
                }
                // In UTF-16 a read can end mid-character; keep the odd byte for the next block.
                int usable = span.Length - span.Length % lines.Unit;
                writer.Write(span[..usable]);
                start += usable;
            }

            int carry = length - start;
            if (carry > 0) buffer.AsSpan(start, carry).CopyTo(buffer);
            offset += start;
            int read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read == 0)
            {
                if (carry > 0 && !o.Bytes) writer.Write(buffer.AsSpan(0, carry));
                return;
            }
            length = carry + read;
            start = 0;
        }
    }

    /// <summary>Reads a non-seekable input fully into memory so it can be searched from the end.</summary>
    private static MemoryStream Buffer(byte[] head, int length, Stream rest)
    {
        var memory = new MemoryStream();
        memory.Write(head, 0, length);
        rest.CopyTo(memory);
        memory.Position = 0;
        return memory;
    }

    private static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };
}
