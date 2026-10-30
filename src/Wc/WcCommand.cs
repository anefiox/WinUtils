using System.Globalization;
using System.Text;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Wc;

public static class WcCommand
{
    private const string TryHelp = "Try 'wc --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("c", 'c', "bytes"),
        new("m", 'm', "chars"),
        new("l", 'l', "lines"),
        new("w", 'w', "words"),
        new("L", 'L', "max-line-length"),
        new("files0-from", null, "files0-from", Required),
        new("total", null, "total", Required),
        new("help", null, "help"),
        new("version", null, "version"),
    ]);

    private enum Total { Auto, Always, Only, Never }

    private sealed record Input(string Path, string? Name);

    /// <summary>Runs wc and returns its exit status: 0, or 1 if any input couldn't be read.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"wc: {message}\n");
            stderr.Flush();
        }

        bool bytes = false, chars = false, lines = false, words = false, maxLength = false;
        string? files0From = null;
        var total = Total.Auto;
        ParseResult parsed;
        try
        {
            parsed = Parser.Parse(args);
            foreach (var (id, value, _) in parsed.Options)
            {
                switch (id)
                {
                    case "c": bytes = true; break;
                    case "m": chars = true; break;
                    case "l": lines = true; break;
                    case "w": words = true; break;
                    case "L": maxLength = true; break;
                    case "files0-from": files0From = value; break;
                    case "total":
                        total = value switch
                        {
                            "auto" => Total.Auto,
                            "always" => Total.Always,
                            "only" => Total.Only,
                            "never" => Total.Never,
                            _ => throw new OptionException($"invalid argument '{value}' for '--total'"),
                        };
                        break;
                    case "help":
                        stdout.Write(HelpText.Text);
                        return 0;
                    case "version":
                        stdout.Write(HelpText.Version);
                        return 0;
                }
            }
            if (files0From is not null && parsed.Operands.Count > 0)
                throw new OptionException($"extra operand '{parsed.Operands[0]}'\nfile operands cannot be combined with --files0-from");
        }
        catch (OptionException e)
        {
            Error(e.Message);
            stderr.Write(TryHelp);
            return 1;
        }
        if (!bytes && !chars && !lines && !words && !maxLength) lines = words = bytes = true;

        List<Input> inputs;
        try
        {
            inputs = files0From is not null
                ? ReadFileList(files0From, stdin).Select(f => new Input(f, f)).ToList()
                : Wildcards.ExpandAll(parsed.Operands).Select(f => new Input(f, f)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error($"cannot open '{files0From}' for reading: {Describe(e)}");
            return 1;
        }
        if (inputs.Count == 0 && files0From is null) inputs.Add(new Input("-", null));

        int columns = (lines ? 1 : 0) + (words ? 1 : 0) + (chars ? 1 : 0) + (bytes ? 1 : 0) + (maxLength ? 1 : 0);
        int width = columns == 1 && inputs.Count == 1 ? 1 : ColumnWidth(inputs, stdin);

        void Print(Counts c, string? name)
        {
            var sb = new StringBuilder();
            void Column(long value)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(value.ToString(CultureInfo.InvariantCulture).PadLeft(width));
            }
            if (lines) Column(c.Lines);
            if (words) Column(c.Words);
            if (chars) Column(c.Chars);
            if (bytes) Column(c.Bytes);
            if (maxLength) Column(c.MaxLineLength);
            if (name is not null) sb.Append(' ').Append(name);
            stdout.Write(sb.Append('\n').ToString());
        }

        var counter = new Counter(lines, words, chars, maxLength);
        var totals = new Counts();
        int status = 0;
        foreach (var input in inputs)
        {
            Counts counts;
            if (input.Path != "-" && Directory.Exists(input.Path))
            {
                // GNU wc reports the error and still prints a row of zeros.
                Error($"{input.Path}: Is a directory");
                status = 1;
                counts = new Counts();
            }
            else
            {
                try
                {
                    using var file = input.Path == "-" ? null
                        : new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, FileOptions.SequentialScan);
                    counts = counter.Count(file ?? stdin);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Error($"{input.Name ?? "-"}: {Describe(e)}");
                    status = 1;
                    continue;
                }
            }
            totals.Add(counts);
            if (total != Total.Only) Print(counts, input.Name);
        }

        if (total == Total.Always || (total == Total.Auto && inputs.Count > 1)) Print(totals, "total");
        else if (total == Total.Only) Print(totals, null);
        return status;
    }

    /// <summary>
    /// GNU's column width: enough digits for the combined size of the regular files, and at
    /// least 7 if any input isn't a regular file (a pipe or a directory), whose size is unknown.
    /// </summary>
    private static int ColumnWidth(List<Input> inputs, Stream stdin)
    {
        long regularTotal = 0;
        int minimum = 1;
        foreach (var input in inputs)
        {
            if (input.Path == "-")
            {
                if (stdin.CanSeek) regularTotal += stdin.Length;
                else minimum = 7;
                continue;
            }
            var info = new FileInfo(input.Path);
            if (info.Exists) regularTotal += info.Length;
            else if (Directory.Exists(input.Path)) minimum = 7;
        }
        int width = regularTotal.ToString(CultureInfo.InvariantCulture).Length;
        return Math.Max(width, minimum);
    }

    /// <summary>--files0-from: NUL-separated file names, "-" meaning standard input.</summary>
    private static List<string> ReadFileList(string path, Stream stdin)
    {
        using var file = path == "-" ? null : File.OpenRead(path);
        using var reader = new StreamReader(file ?? stdin, new UTF8Encoding(false));
        return reader.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };
}
