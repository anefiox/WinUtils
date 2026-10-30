using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using WinUtils.Common.Text;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Grep;

/// <summary>A bad command line: reported with a usage hint, exit status 2.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>A fatal error that isn't a usage mistake (bad option value, unreadable -f file): exit status 2.</summary>
public sealed class FatalException(string message) : Exception(message);

internal static class GrepArguments
{
    private static readonly OptionSpec[] Specs =
    [
        new("E", 'E', "extended-regexp"),
        new("F", 'F', "fixed-strings"),
        new("G", 'G', "basic-regexp"),
        new("P", 'P', "perl-regexp"),
        new("e", 'e', "regexp", Required),
        new("f", 'f', "file", Required),
        new("i", 'i', "ignore-case"),
        new("i", 'y'),
        new("no-ignore-case", null, "no-ignore-case"),
        new("w", 'w', "word-regexp"),
        new("x", 'x', "line-regexp"),
        new("z", 'z', "null-data"),
        new("s", 's', "no-messages"),
        new("v", 'v', "invert-match"),
        new("V", 'V', "version"),
        new("help", null, "help"),
        new("m", 'm', "max-count", Required),
        new("b", 'b', "byte-offset"),
        new("n", 'n', "line-number"),
        new("line-buffered", null, "line-buffered"),
        new("H", 'H', "with-filename"),
        new("h", 'h', "no-filename"),
        new("label", null, "label", Required),
        new("o", 'o', "only-matching"),
        new("q", 'q', "quiet"),
        new("q", null, "silent"),
        new("binary-files", null, "binary-files", Required),
        new("a", 'a', "text"),
        new("I", 'I'),
        new("d", 'd', "directories", Required),
        new("D", 'D', "devices", Required),
        new("r", 'r', "recursive"),
        new("R", 'R', "dereference-recursive"),
        new("include", null, "include", Required),
        new("exclude", null, "exclude", Required),
        new("exclude-from", null, "exclude-from", Required),
        new("exclude-dir", null, "exclude-dir", Required),
        new("L", 'L', "files-without-match"),
        new("l", 'l', "files-with-matches"),
        new("c", 'c', "count"),
        new("T", 'T', "initial-tab"),
        new("Z", 'Z', "null"),
        new("B", 'B', "before-context", Required),
        new("A", 'A', "after-context", Required),
        new("C", 'C', "context", Required),
        new("color", null, "color", Optional),
        new("color", null, "colour", Optional),
        new("U", 'U', "binary"),
        new("group-separator", null, "group-separator", Required),
        new("no-group-separator", null, "no-group-separator"),
    ];

    private static readonly OptionParser Parser = new(Specs, allowNumericOptions: true);

    public static GrepOptions Parse(IReadOnlyList<string> args, Stream stdin)
    {
        ParseResult parsed;
        try
        {
            parsed = Parser.Parse(args);
        }
        catch (OptionException e)
        {
            throw new UsageException(e.Message);
        }

        var o = new GrepOptions();
        bool patternsGiven = false;
        int context = -1, before = -1, after = -1;

        foreach (var (id, value, spelling) in parsed.Options)
        {
            switch (id)
            {
                case "E": o.Syntax = PatternSyntax.Extended; break;
                case "F": o.Syntax = PatternSyntax.Fixed; break;
                case "G": o.Syntax = PatternSyntax.Basic; break;
                case "P": o.Syntax = PatternSyntax.Perl; break;
                case "e":
                    o.Patterns.Add(value!);
                    patternsGiven = true;
                    break;
                case "f":
                    o.Patterns.AddRange(ReadLines(value!, stdin));
                    patternsGiven = true;
                    break;
                case "i": o.IgnoreCase = true; break;
                case "no-ignore-case": o.IgnoreCase = false; break;
                case "w": o.WordRegexp = true; break;
                case "x": o.LineRegexp = true; break;
                case "z": o.NullData = true; break;
                case "s": o.NoMessages = true; break;
                case "v": o.InvertMatch = true; break;
                case "V": o.ShowVersion = true; break;
                case "help": o.ShowHelp = true; break;
                case "m": o.MaxCount = ParseNumber(value!, "invalid max count", allowNegative: true); break;
                case "b": o.ByteOffset = true; break;
                case "n": o.LineNumber = true; break;
                case "line-buffered": o.LineBuffered = true; break;
                case "H": o.WithFilename = true; break;
                case "h": o.WithFilename = false; break;
                case "label": o.Label = value; break;
                case "o": o.OnlyMatching = true; break;
                case "q": o.Quiet = true; break;
                case "binary-files":
                    o.Binary = value switch
                    {
                        "binary" => BinaryFiles.Binary,
                        "text" => BinaryFiles.Text,
                        "without-match" => BinaryFiles.WithoutMatch,
                        _ => throw new FatalException("unknown binary-files type"),
                    };
                    break;
                case "a": o.Binary = BinaryFiles.Text; break;
                case "I": o.Binary = BinaryFiles.WithoutMatch; break;
                case "d":
                    o.Directories = value switch
                    {
                        "read" => DirectoryAction.Read,
                        "skip" => DirectoryAction.Skip,
                        "recurse" => DirectoryAction.Recurse,
                        _ => throw InvalidArgument(value!, spelling),
                    };
                    break;
                case "D":
                    o.SkipDevices = value switch
                    {
                        "read" => false,
                        "skip" => true,
                        _ => throw InvalidArgument(value!, spelling),
                    };
                    break;
                case "r":
                    o.Directories = DirectoryAction.Recurse;
                    o.FollowSymlinks = false;
                    break;
                case "R":
                    o.Directories = DirectoryAction.Recurse;
                    o.FollowSymlinks = true;
                    break;
                case "include": o.FileFilters.Add((new Glob(value!), true)); break;
                case "exclude": o.FileFilters.Add((new Glob(value!), false)); break;
                case "exclude-from":
                    foreach (string line in ReadLines(value!, stdin))
                        if (line.Length > 0) o.FileFilters.Add((new Glob(line), false));
                    break;
                case "exclude-dir": o.ExcludeDirs.Add(new Glob(value!)); break;
                case "L": o.List = ListFiles.WithoutMatch; break;
                case "l": o.List = ListFiles.WithMatches; break;
                case "c": o.Count = true; break;
                case "T": o.InitialTab = true; break;
                case "Z": o.NullAfterName = true; break;
                case "B": before = ParseContext(value!); break;
                case "A": after = ParseContext(value!); break;
                case "C": context = ParseContext(value!); break;
                case OptionParser.NumericId: context = ParseContext(value!); break;
                case "color":
                    o.Color = value switch
                    {
                        null or "auto" or "tty" or "if-tty" => ColorWhen.Auto,
                        "always" or "yes" or "force" => ColorWhen.Always,
                        "never" or "no" or "none" => ColorWhen.Never,
                        _ => throw InvalidArgument(value, spelling),
                    };
                    break;
                case "U": o.KeepCarriageReturn = true; break;
                case "group-separator": o.GroupSeparator = value; break;
                case "no-group-separator": o.GroupSeparator = null; break;
                default: throw new InvalidOperationException($"unhandled option {spelling}");
            }
        }

        // -A and -B override -C regardless of order, as in GNU grep.
        o.Before = before >= 0 ? before : Math.Max(context, 0);
        o.After = after >= 0 ? after : Math.Max(context, 0);

        if (o.ShowHelp || o.ShowVersion) return o;

        var operands = parsed.Operands;
        if (!patternsGiven)
        {
            if (operands.Count == 0) throw new UsageException("");
            o.Patterns.Add(operands[0]);
            operands.RemoveAt(0);
        }
        o.Files.AddRange(Wildcards.ExpandAll(operands));
        return o;
    }

    private static FatalException InvalidArgument(string value, string spelling) =>
        new($"invalid argument '{value}' for '{spelling}'");

    private static int ParseContext(string value)
    {
        long n = ParseNumber(value, $"{value}: invalid context length argument", allowNegative: false);
        return (int)Math.Min(n, int.MaxValue);
    }

    private static long ParseNumber(string value, string error, bool allowNegative)
    {
        if (!long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, null, out long n))
        {
            // Digits that merely overflow mean "effectively unlimited".
            if (value.Length > 0 && value.All(char.IsAsciiDigit)) return long.MaxValue;
            throw new FatalException(error);
        }
        if (n < 0 && !allowNegative) throw new FatalException(error);
        return n;
    }

    /// <summary>Reads -f / --exclude-from files, one entry per line ("-" is standard input).</summary>
    private static List<string> ReadLines(string path, Stream stdin)
    {
        try
        {
            using var stream = path == "-" ? null : File.OpenRead(path);
            var reader = new LineReader(stream ?? stdin);
            var lines = new List<string>();
            while (reader.ReadLine(out string line, out _)) lines.Add(line);
            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FatalException($"{path}: {GrepCommand.Describe(e)}");
        }
    }
}
