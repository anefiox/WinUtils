using System.Globalization;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Uniq;

public static class UniqCommand
{
    private const string TryHelp = "Try 'uniq --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("c", 'c', "count"),
        new("d", 'd', "repeated"),
        new("D", 'D'),
        new("D", null, "all-repeated", Optional),
        new("u", 'u', "unique"),
        new("i", 'i', "ignore-case"),
        new("f", 'f', "skip-fields", Required),
        new("s", 's', "skip-chars", Required),
        new("w", 'w', "check-chars", Required),
        new("z", 'z', "zero-terminated"),
        new("group", null, "group", Optional),
        new("help", null, "help"),
        new("version", null, "version"),
    ], allowNumericOptions: true);

    private enum Separation { None, Prepend, Append, Separate, Both }

    private sealed record Line(string Text, string Terminator);

    /// <summary>Runs uniq and returns its exit status: 0, or 1 on error.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"uniq: {message}\n");
            stderr.Flush();
        }

        bool count = false, repeated = false, allRepeated = false, unique = false, ignoreCase = false, grouping = false;
        var separation = Separation.None;
        int skipFields = 0, skipChars = 0, checkChars = int.MaxValue;
        char terminator = '\n';
        ParseResult parsed;
        try
        {
            parsed = Parser.Parse(args);
            foreach (var (id, value, spelling) in parsed.Options)
            {
                switch (id)
                {
                    case "c": count = true; break;
                    case "d": repeated = true; break;
                    case "D":
                        allRepeated = true;
                        separation = value switch
                        {
                            null or "none" => Separation.None,
                            "prepend" => Separation.Prepend,
                            "separate" => Separation.Separate,
                            _ => throw new OptionException($"invalid argument '{value}' for '--all-repeated'"),
                        };
                        break;
                    case "u": unique = true; break;
                    case "i": ignoreCase = true; break;
                    case "f" or OptionParser.NumericId: skipFields = Count(value!, "number of fields to skip"); break;
                    case "s": skipChars = Count(value!, "number of bytes to skip"); break;
                    case "w": checkChars = Count(value!, "number of bytes to compare"); break;
                    case "z": terminator = '\0'; break;
                    case "group":
                        grouping = true;
                        separation = value switch
                        {
                            null or "separate" => Separation.Separate,
                            "prepend" => Separation.Prepend,
                            "append" => Separation.Append,
                            "both" => Separation.Both,
                            _ => throw new OptionException($"invalid argument '{value}' for '--group'"),
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
            if (grouping && (count || repeated || allRepeated || unique))
                throw new OptionException("--group is mutually exclusive with -c/-d/-D/-u");
            if (count && allRepeated)
                throw new OptionException("printing all duplicated lines and repeat counts is meaningless");
            if (parsed.Operands.Count > 2) throw new OptionException($"extra operand '{parsed.Operands[2]}'");
        }
        catch (OptionException e)
        {
            Error(e.Message);
            stderr.Write(TryHelp);
            return 1;
        }

        string inputName = parsed.Operands.Count > 0 ? parsed.Operands[0] : "-";
        string? outputName = parsed.Operands.Count > 1 && parsed.Operands[1] != "-" ? parsed.Operands[1] : null;
        Stream input;
        try
        {
            input = inputName == "-" ? stdin : new FileStream(inputName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error($"{inputName}: {Describe(e)}");
            return 1;
        }

        FileStream? outputFile = null;
        try
        {
            var reader = new LineReader(input, terminator);
            var output = stdout;
            if (outputName is not null)
            {
                outputFile = new FileStream(outputName, FileMode.Create, FileAccess.Write, FileShare.Read);
                output = OutputSink.ForStream(outputFile);
            }
            output.ContentEncoding = reader.IsUtf16 ? OutputSink.Utf8 : reader.Encoding;
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string newline = terminator == '\0' ? "\0" : "\n";
            bool printedGroup = false;

            void Write(Line line)
            {
                output.WriteContent(line.Text);
                output.WriteContent(line.Terminator.Length > 0 ? line.Terminator : newline);
            }

            void EndGroup(List<Line> group)
            {
                bool isRepeated = group.Count > 1;
                if (grouping || allRepeated)
                {
                    if (allRepeated && !isRepeated) return;
                    if (separation is Separation.Prepend or Separation.Both || (separation == Separation.Separate && printedGroup))
                        output.WriteContent(newline);
                    foreach (var line in group) Write(line);
                    if (separation == Separation.Append) output.WriteContent(newline);
                    printedGroup = true;
                    return;
                }
                if ((repeated && !isRepeated) || (unique && isRepeated)) return;
                if (count) output.Write(group.Count.ToString(CultureInfo.InvariantCulture).PadLeft(7) + " ");
                Write(group[0]);
            }

            // Keep every line of the group only when they're all printed (-D, --group).
            bool keepAll = grouping || allRepeated;
            var current = new List<Line>();
            int groupSize = 0;
            while (reader.ReadLine(out string text, out string lineEnd))
            {
                if (lineEnd.Length > 0) newline = lineEnd;
                var line = new Line(text, lineEnd);
                if (groupSize > 0 && !Key(current[0].Text, skipFields, skipChars, checkChars).Equals(Key(text, skipFields, skipChars, checkChars), comparison))
                {
                    EndGroup(Expand(current, groupSize));
                    current.Clear();
                    groupSize = 0;
                }
                if (groupSize == 0 || keepAll) current.Add(line);
                groupSize++;
                if (output.IsConsole) output.Flush();
            }
            if (groupSize > 0) EndGroup(Expand(current, groupSize));
            // --group=both: a blank line before every group and one after the last.
            if (separation == Separation.Both && printedGroup) output.WriteContent(newline);
            output.Flush();
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error($"{outputName ?? inputName}: {Describe(e)}");
            return 1;
        }
        finally
        {
            if (input != stdin) input.Dispose();
            outputFile?.Dispose();
        }
    }

    /// <summary>When only the first line was kept, pad the list so its count is the group size.</summary>
    private static List<Line> Expand(List<Line> kept, int size)
    {
        while (kept.Count < size) kept.Add(kept[0]);
        return kept;
    }

    /// <summary>The part of a line that's compared: after -f fields and -s characters, at most -w characters.</summary>
    private static ReadOnlySpan<char> Key(string line, int skipFields, int skipChars, int checkChars)
    {
        int pos = 0;
        for (int f = 0; f < skipFields && pos < line.Length; f++)
        {
            while (pos < line.Length && line[pos] is ' ' or '\t') pos++;
            while (pos < line.Length && line[pos] is not (' ' or '\t')) pos++;
        }
        pos = Math.Min(pos + skipChars, line.Length);
        return line.AsSpan(pos, Math.Min(checkChars, line.Length - pos));
    }

    private static int Count(string value, string what) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n
            : value.Length > 0 && value.All(char.IsAsciiDigit) ? int.MaxValue
            : throw new OptionException($"{value}: invalid {what}");

    private static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };
}
