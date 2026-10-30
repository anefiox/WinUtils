using System.Globalization;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Cut;

public static class CutCommand
{
    private const string TryHelp = "Try 'cut --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("b", 'b', "bytes", Required),
        new("c", 'c', "characters", Required),
        new("f", 'f', "fields", Required),
        new("d", 'd', "delimiter", Required),
        new("s", 's', "only-delimited"),
        new("complement", null, "complement"),
        new("output-delimiter", null, "output-delimiter", Required),
        new("z", 'z', "zero-terminated"),
        new("n", 'n'),
        new("help", null, "help"),
        new("version", null, "version"),
    ]);

    private enum Mode { None, Bytes, Characters, Fields }

    /// <summary>Selected positions as sorted, non-overlapping, 1-based inclusive ranges.</summary>
    private sealed record Selection(List<(int Low, int High)> Ranges, bool Complement)
    {
        public bool Contains(int position)
        {
            bool inside = false;
            foreach (var (low, high) in Ranges)
            {
                if (position < low) break;
                if (position <= high)
                {
                    inside = true;
                    break;
                }
            }
            return inside != Complement;
        }
    }

    /// <summary>Runs cut and returns its exit status: 0, or 1 on error.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"cut: {message}\n");
            stderr.Flush();
        }

        var mode = Mode.None;
        string? list = null, delimiterText = null, outputDelimiter = null;
        bool onlyDelimited = false, complement = false;
        char terminator = '\n';
        ParseResult parsed;
        Selection selection;
        try
        {
            parsed = Parser.Parse(args);
            foreach (var (id, value, _) in parsed.Options)
            {
                switch (id)
                {
                    case "b" or "c" or "f":
                        if (mode != Mode.None) throw new OptionException("only one type of list may be specified");
                        mode = id switch { "b" => Mode.Bytes, "c" => Mode.Characters, _ => Mode.Fields };
                        list = value;
                        break;
                    case "d": delimiterText = value; break;
                    case "s": onlyDelimited = true; break;
                    case "complement": complement = true; break;
                    case "output-delimiter": outputDelimiter = value; break;
                    case "z": terminator = '\0'; break;
                    case "help":
                        stdout.Write(HelpText.Text);
                        return 0;
                    case "version":
                        stdout.Write(HelpText.Version);
                        return 0;
                }
            }
            if (mode == Mode.None) throw new OptionException("you must specify a list of bytes, characters, or fields");
            if (delimiterText is not null && mode != Mode.Fields)
                throw new OptionException("an input delimiter may be specified only when operating on fields");
            if (onlyDelimited && mode != Mode.Fields)
                throw new OptionException("suppressing non-delimited lines makes sense\n\tonly when operating on fields");
            if (delimiterText is not null && delimiterText.Length != 1)
                throw new OptionException("the delimiter must be a single character");
            selection = new Selection(ParseList(list!, mode == Mode.Fields), complement);
        }
        catch (OptionException e)
        {
            Error(e.Message);
            stderr.Write(TryHelp);
            return 1;
        }

        char delimiter = delimiterText?[0] ?? '\t';
        var files = Wildcards.ExpandAll(parsed.Operands);
        if (files.Count == 0) files.Add("-");
        int status = 0;
        foreach (string file in files)
        {
            Stream stream;
            try
            {
                stream = file == "-" ? stdin : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error($"{file}: {(e is FileNotFoundException or DirectoryNotFoundException ? "No such file or directory" : e.Message)}");
                status = 1;
                continue;
            }
            try
            {
                var reader = new LineReader(stream, terminator);
                stdout.ContentEncoding = reader.IsUtf16 ? OutputSink.Utf8 : reader.Encoding;
                // Bytes pass through raw, unless they must be shown as text (a console, or UTF-16 input).
                bool rawBytes = !stdout.IsConsole && !reader.IsUtf16;
                string fallback = terminator == '\0' ? "\0" : "\n";
                while (reader.ReadLine(out string line, out string lineEnd))
                {
                    if (lineEnd.Length > 0) fallback = lineEnd;
                    bool print = mode switch
                    {
                        Mode.Fields => CutFields(line, delimiter, outputDelimiter ?? delimiter.ToString(), selection, onlyDelimited, stdout),
                        Mode.Characters => CutPositions(line.Length, selection,
                            outputDelimiter is null ? null : () => stdout.WriteContent(outputDelimiter),
                            (from, length) => stdout.WriteContent(line.AsSpan(from, length))),
                        _ => CutBytes(line, reader, rawBytes, selection, outputDelimiter, stdout),
                    };
                    if (print) stdout.WriteContent(lineEnd.Length > 0 ? lineEnd : fallback);
                    if (stdout.IsConsole) stdout.Flush();
                }
            }
            finally
            {
                if (stream != stdin) stream.Dispose();
            }
        }
        stdout.Flush();
        return status;
    }

    private static bool CutFields(string line, char delimiter, string outputDelimiter, Selection selection, bool onlyDelimited, OutputSink output)
    {
        if (line.IndexOf(delimiter) < 0)
        {
            // A line without the delimiter is printed whole, unless -s.
            if (onlyDelimited) return false;
            output.WriteContent(line);
            return true;
        }
        int field = 0, start = 0;
        bool first = true;
        while (start <= line.Length)
        {
            field++;
            int end = line.IndexOf(delimiter, start);
            if (end < 0) end = line.Length;
            if (selection.Contains(field))
            {
                if (!first) output.WriteContent(outputDelimiter);
                output.WriteContent(line.AsSpan(start, end - start));
                first = false;
            }
            start = end + 1;
        }
        return true;
    }

    /// <summary>
    /// Calls <paramref name="emit"/> (0-based start, length) for each run of selected positions,
    /// and <paramref name="separator"/> between runs when --output-delimiter was given.
    /// </summary>
    private static bool CutPositions(int length, Selection selection, Action? separator, Action<int, int> emit)
    {
        int previousEnd = -1;
        for (int pos = 1; pos <= length;)
        {
            if (!selection.Contains(pos))
            {
                pos++;
                continue;
            }
            int runStart = pos;
            while (pos <= length && selection.Contains(pos)) pos++;
            // --output-delimiter goes between runs that weren't adjacent in the input.
            if (previousEnd >= 0 && separator is not null) separator();
            emit(runStart - 1, pos - runStart);
            previousEnd = pos;
        }
        return true;
    }

    private static bool CutBytes(string line, LineReader reader, bool rawBytes, Selection selection, string? outputDelimiter, OutputSink output)
    {
        byte[] bytes = reader.Encoding.GetBytes(line);
        void Separator() => output.WriteContent(outputDelimiter);
        void Emit(int from, int length)
        {
            if (rawBytes) output.WriteBytes(bytes.AsSpan(from, length));
            else output.WriteContent(reader.Encoding.GetString(bytes, from, length));
        }
        return CutPositions(bytes.Length, selection, outputDelimiter is null ? null : Separator, Emit);
    }

    /// <summary>Parses LIST: N, N-M, N-, -M, separated by commas.</summary>
    private static List<(int, int)> ParseList(string list, bool fields)
    {
        string what = fields ? "field value" : "byte/character position";
        var ranges = new List<(int Low, int High)>();
        foreach (string part in list.Split(','))
        {
            int dash = part.IndexOf('-');
            int low, high;
            if (dash < 0)
            {
                low = high = Number(part);
            }
            else
            {
                string a = part[..dash], b = part[(dash + 1)..];
                if (a.Length == 0 && b.Length == 0) throw new OptionException("invalid range with no endpoint: -");
                low = a.Length == 0 ? 1 : Number(a);
                high = b.Length == 0 ? int.MaxValue : Number(b);
                if (high < low) throw new OptionException("invalid decreasing range");
            }
            ranges.Add((low, high));
        }

        int Number(string text)
        {
            if (text.Length == 0 || !text.All(char.IsAsciiDigit))
                throw new OptionException(fields ? $"invalid field value '{text}'" : $"invalid {what} '{text}'");
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) n = int.MaxValue;
            if (n == 0) throw new OptionException(fields ? "fields are numbered from 1" : "byte/character positions are numbered from 1");
            return n;
        }

        // Sort and merge overlapping or adjacent ranges.
        ranges.Sort();
        var merged = new List<(int Low, int High)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.Low <= (long)merged[^1].High + 1)
                merged[^1] = (merged[^1].Low, Math.Max(merged[^1].High, r.High));
            else
                merged.Add(r);
        }
        return merged;
    }
}
