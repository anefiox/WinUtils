using System.Globalization;
using System.Text;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using WinUtils.Common.Text;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Sort;

public sealed class SortException(string message, int exitCode = 2) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public static class SortCommand
{
    private const string TryHelp = "Try 'sort --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("b", 'b', "ignore-leading-blanks"),
        new("d", 'd', "dictionary-order"),
        new("f", 'f', "ignore-case"),
        new("g", 'g', "general-numeric-sort"),
        new("h", 'h', "human-numeric-sort"),
        new("i", 'i', "ignore-nonprinting"),
        new("M", 'M', "month-sort"),
        new("n", 'n', "numeric-sort"),
        new("R", 'R', "random-sort"),
        new("r", 'r', "reverse"),
        new("V", 'V', "version-sort"),
        new("sort", null, "sort", Required),
        new("c", 'c'),
        new("c", null, "check", Optional),
        new("C", 'C'),
        new("k", 'k', "key", Required),
        new("m", 'm', "merge"),
        new("o", 'o', "output", Required),
        new("s", 's', "stable"),
        new("S", 'S', "buffer-size", Required),
        new("t", 't', "field-separator", Required),
        new("T", 'T', "temporary-directory", Required),
        new("u", 'u', "unique"),
        new("z", 'z', "zero-terminated"),
        new("files0-from", null, "files0-from", Required),
        new("ignored", null, "parallel", Required),
        new("ignored", null, "batch-size", Required),
        new("ignored", null, "compress-program", Required),
        new("ignored", null, "random-source", Required),
        new("ignored", null, "debug"),
        new("help", null, "help"),
        new("version", null, "version"),
    ]);

    private sealed class Settings
    {
        public required KeyComparer Comparer;      // with the last-resort comparison unless -s/-u
        public required KeyComparer KeysOnly;      // keys only, for -u
        public bool Unique, Stable, Check, Quiet, Merge;
        public string? Output;
        public char Terminator = '\n';
        public long Budget;                        // characters held in memory before spilling to disk
        public string TempDirectory = Path.GetTempPath();
    }

    /// <summary>Runs sort and returns its exit status: 0; 1 if -c found disorder; 2 on error.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"sort: {message}\n");
            stderr.Flush();
        }

        Settings s;
        List<string> files;
        try
        {
            (s, files) = ParseArguments(args, stdin, stdout);
        }
        catch (OptionException e)
        {
            Error(e.Message);
            stderr.Write(TryHelp);
            return 2;
        }
        catch (SortException e)
        {
            if (e.Message.Length > 0) Error(e.Message);
            return e.ExitCode;
        }
        if (files.Count == 0) return 0; // --help or --version

        var input = new Inputs(files, stdin, s.Terminator);
        try
        {
            if (s.Check)
            {
                if (files.Count > 1) throw new SortException($"extra operand '{files[1]}' not allowed with -c");
                return Check(input, files[0], s, stderr);
            }
            if (s.Merge) MergeFiles(input, files, s, stdout);
            else SortFiles(input, s, stdout);
            return 0;
        }
        catch (SortException e)
        {
            Error(e.Message);
            return e.ExitCode;
        }
    }

    private static (Settings, List<string>) ParseArguments(IReadOnlyList<string> args, Stream stdin, OutputSink stdout)
    {
        var parsed = Parser.Parse(args);
        var global = new KeyOptions();
        var keys = new List<KeySpec>();
        char? separator = null;
        bool unique = false, stable = false, check = false, quiet = false, merge = false, zero = false;
        string? output = null, files0From = null, tempDirectory = null;
        long? buffer = null;

        foreach (var (id, value, spelling) in parsed.Options)
        {
            switch (id)
            {
                case "b" or "d" or "f" or "g" or "h" or "i" or "M" or "n" or "R" or "r" or "V":
                    global.Apply(id[0], atEnd: false);
                    break;
                case "sort":
                    global.Apply(value switch
                    {
                        "general-numeric" => 'g',
                        "human-numeric" => 'h',
                        "month" => 'M',
                        "numeric" => 'n',
                        "random" => 'R',
                        "version" => 'V',
                        _ => throw new OptionException($"invalid argument '{value}' for '--sort'"),
                    }, atEnd: false);
                    break;
                case "c":
                    check = true;
                    quiet = value is "quiet" or "silent";
                    if (value is not (null or "quiet" or "silent" or "diagnose-first"))
                        throw new OptionException($"invalid argument '{value}' for '--check'");
                    break;
                case "C": (check, quiet) = (true, true); break;
                case "k": keys.Add(ParseKey(value!)); break;
                case "m": merge = true; break;
                case "o": output = value; break;
                case "s": stable = true; break;
                case "S": buffer = ParseBuffer(value!); break;
                case "t":
                    if (value == "\\0") separator = '\0';
                    else if (value!.Length != 1) throw new SortException(value.Length == 0 ? "empty tab" : $"multi-character tab '{value}'");
                    else separator = value[0];
                    break;
                case "T": tempDirectory = value; break;
                case "u": unique = true; break;
                case "z": zero = true; break;
                case "files0-from": files0From = value; break;
                case "help":
                    stdout.Write(HelpText.Text);
                    throw new SortException("", 0);
                case "version":
                    stdout.Write(HelpText.Version);
                    throw new SortException("", 0);
            }
        }

        List<string> files = files0From is not null
            ? ReadFileList(files0From, stdin)
            : Wildcards.ExpandAll(parsed.Operands);
        if (files.Count == 0) files.Add("-");

        var settings = new Settings
        {
            Comparer = new KeyComparer(keys, global, separator, lastResort: !stable && !unique),
            KeysOnly = new KeyComparer(keys, global, separator, lastResort: false),
            Unique = unique,
            Stable = stable,
            Check = check,
            Quiet = quiet,
            Merge = merge,
            Output = output,
            Terminator = zero ? '\0' : '\n',
            // By default hold up to 1/32 of available memory in lines (two bytes per character).
            Budget = (buffer ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 32) / 2,
            TempDirectory = tempDirectory ?? Path.GetTempPath(),
        };
        return (settings, files);
    }

    /// <summary>-k F[.C][OPTS][,F[.C][OPTS]]</summary>
    private static KeySpec ParseKey(string text)
    {
        int i = 0;
        int Number(string what)
        {
            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            if (i == start || !int.TryParse(text.AsSpan(start, i - start), out int n))
                throw new SortException($"invalid number {what}: invalid count at start of '{text[start..]}'");
            if (n == 0)
            {
                throw new SortException(what == "after '.'"
                    ? $"character offset is zero: invalid field specification '{text}'"
                    : $"field number is zero: invalid field specification '{text}'");
            }
            return n;
        }

        var options = new KeyOptions();
        bool any = false;
        int startField = Number("at field start"), startChar = 1, endField = 0, endChar = 0;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            startChar = Number("after '.'");
        }
        while (i < text.Length && text[i] != ',' && options.Apply(text[i], atEnd: false))
        {
            any = true;
            i++;
        }
        if (i < text.Length && text[i] == ',')
        {
            i++;
            endField = Number("after ','");
            if (i < text.Length && text[i] == '.')
            {
                i++;
                int start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                endChar = int.Parse(text.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            }
            while (i < text.Length && options.Apply(text[i], atEnd: true))
            {
                any = true;
                i++;
            }
        }
        if (i < text.Length) throw new SortException($"stray character in field spec: invalid field specification '{text}'");
        return new KeySpec
        {
            StartField = startField,
            StartChar = startChar,
            EndField = endField,
            EndChar = endChar,
            Options = options,
            Inherits = !any,
        };
    }

    private static long ParseBuffer(string text)
    {
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            return (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * percent / 100);
        // A plain number means KiB, as in GNU sort.
        string withUnit = text.Length > 0 && char.IsAsciiDigit(text[^1]) ? text + "K" : text;
        return Sizes.TryParse(withUnit, out long bytes) ? Math.Max(bytes, 1) : throw new SortException($"invalid -S argument '{text}'");
    }

    private static void SortFiles(Inputs input, Settings s, OutputSink stdout)
    {
        var chunk = new List<string>();
        long chunkChars = 0;
        var runs = new List<string>();
        try
        {
            foreach (string line in input.AllLines())
            {
                chunk.Add(line);
                chunkChars += line.Length + 24; // rough per-line overhead
                if (chunkChars > s.Budget)
                {
                    runs.Add(WriteRun(Sorted(chunk, s), s));
                    chunk.Clear();
                    chunkChars = 0;
                }
            }

            if (runs.Count == 0)
            {
                WriteOutput(Sorted(chunk, s), input, s, stdout);
                return;
            }
            // Too big for memory: merge the sorted runs written to disk.
            if (chunk.Count > 0) runs.Add(WriteRun(Sorted(chunk, s), s));
            chunk = [];
            var readers = runs.Select(path => ReadRun(path, s.Terminator)).ToList();
            WriteOutput(Merge(readers, s.Comparer), input, s, stdout);
        }
        finally
        {
            foreach (string run in runs)
            {
                try { File.Delete(run); }
                catch (IOException) { }
            }
        }
    }

    private static IEnumerable<string> Sorted(List<string> lines, Settings s)
    {
        string[] array = [.. lines];
        if (s.Comparer.IsPlain)
        {
            // Equal plain lines are identical, so stability can't show.
            ParallelSort.Sort(array, string.CompareOrdinal);
            return array;
        }

        // Find each line's keys once, then sort indexes. Breaking ties by index makes the sort
        // stable, which -s and -u need (the first of equal lines stays first).
        var comparer = s.Comparer;
        int width = comparer.BoundsPerLine;
        var bounds = new int[array.Length * width];
        Parallel.For(0, array.Length, i => comparer.ComputeBounds(array[i], bounds.AsSpan(i * width, width)));
        var order = new int[array.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        ParallelSort.Sort(order, (x, y) =>
        {
            int c = comparer.Compare(array[x], bounds.AsSpan(x * width, width), array[y], bounds.AsSpan(y * width, width));
            return c != 0 ? c : x.CompareTo(y);
        });
        return order.Select(i => array[i]);
    }

    private static string WriteRun(IEnumerable<string> lines, Settings s)
    {
        string path = Path.Combine(s.TempDirectory, $"sort{Path.GetRandomFileName()}");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
        foreach (string line in lines)
        {
            writer.Write(line);
            writer.Write(s.Terminator);
        }
        return path;
    }

    private static IEnumerable<string> ReadRun(string path, char terminator)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var reader = new LineReader(stream, terminator, stripCarriageReturn: false);
        while (reader.ReadLine(out string line, out _)) yield return line;
    }

    /// <summary>K-way merge; among equal lines the earlier source wins, keeping the merge stable.</summary>
    private static IEnumerable<string> Merge(List<IEnumerable<string>> sources, KeyComparer comparer)
    {
        var enumerators = sources.Select(s => s.GetEnumerator()).ToList();
        var queue = new PriorityQueue<int, (string Line, int Source)>(Comparer<(string Line, int Source)>.Create((x, y) =>
        {
            int c = comparer.Compare(x.Line, y.Line);
            return c != 0 ? c : x.Source.CompareTo(y.Source);
        }));
        try
        {
            for (int i = 0; i < enumerators.Count; i++)
                if (enumerators[i].MoveNext()) queue.Enqueue(i, (enumerators[i].Current, i));
            while (queue.TryDequeue(out int source, out var item))
            {
                yield return item.Line;
                if (enumerators[source].MoveNext()) queue.Enqueue(source, (enumerators[source].Current, source));
            }
        }
        finally
        {
            foreach (var e in enumerators) e.Dispose();
        }
    }

    private static void MergeFiles(Inputs input, List<string> files, Settings s, OutputSink stdout)
    {
        var sources = files.Select((f, i) => input.Lines(f)).ToList();
        WriteOutput(Merge(sources, s.Comparer), input, s, stdout);
    }

    /// <summary>Writes lines to stdout or -o FILE, dropping repeats for -u.</summary>
    private static void WriteOutput(IEnumerable<string> lines, Inputs input, Settings s, OutputSink stdout)
    {
        // -o may name an input file. Sorting has read everything by now, but a merge still
        // streams from its inputs, so write to a temp file and swap it in at the end.
        string? target = s.Output is null ? null : Path.GetFullPath(s.Output);
        string? temp = target is not null && s.Merge ? target + $".sort{Path.GetRandomFileName()}" : null;

        FileStream? file = null;
        OutputSink output = stdout;
        string? previous = null;
        bool first = true;
        try
        {
            foreach (string line in lines)
            {
                if (first)
                {
                    if (target is not null)
                    {
                        file = OpenOutput(temp ?? target);
                        output = OutputSink.ForStream(file);
                    }
                    output.ContentEncoding = input.OutputEncoding;
                    first = false;
                }
                if (s.Unique && previous is not null && s.KeysOnly.Compare(previous, line) == 0) continue;
                previous = line;
                output.WriteContent(line);
                output.WriteContent(input.Newline);
            }
            if (first && target is not null) file = OpenOutput(temp ?? target); // empty output still creates the file
            output.Flush();
        }
        finally
        {
            file?.Dispose();
        }
        if (temp is not null) File.Move(temp, target!, overwrite: true);
    }

    private static FileStream OpenOutput(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SortException($"open failed: {path}: {Inputs.Describe(e)}");
        }
    }

    private static int Check(Inputs input, string name, Settings s, OutputSink stderr)
    {
        string? previous = null;
        long number = 0;
        foreach (string line in input.Lines(name))
        {
            number++;
            if (previous is not null)
            {
                int c = s.Comparer.Compare(previous, line);
                if (c > 0 || (s.Unique && s.KeysOnly.Compare(previous, line) == 0))
                {
                    if (!s.Quiet)
                    {
                        stderr.Write($"sort: {name}:{number}: disorder: {line}\n");
                        stderr.Flush();
                    }
                    return 1;
                }
            }
            previous = line;
        }
        return 0;
    }

    private static List<string> ReadFileList(string path, Stream stdin)
    {
        try
        {
            using var file = path == "-" ? null : File.OpenRead(path);
            using var reader = new StreamReader(file ?? stdin, new UTF8Encoding(false));
            return reader.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SortException($"cannot open '{path}' for reading: {Inputs.Describe(e)}");
        }
    }
}

/// <summary>Reads input files, remembering the first file's encoding and line-ending style for output.</summary>
internal sealed class Inputs(List<string> files, Stream stdin, char terminator)
{
    private bool _styleKnown;

    /// <summary>Output line ending: the first one seen in the input, so CRLF input gives CRLF output.</summary>
    public string Newline { get; private set; } = terminator == '\0' ? "\0" : "\n";

    public Encoding OutputEncoding { get; private set; } = OutputSink.Utf8;

    public IEnumerable<string> AllLines() => files.SelectMany(Lines);

    public IEnumerable<string> Lines(string file)
    {
        Stream stream;
        try
        {
            stream = file == "-" ? stdin : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SortException(Directory.Exists(file) ? $"read failed: {file}: Is a directory" : $"cannot read: {file}: {Describe(e)}");
        }

        try
        {
            var reader = new LineReader(stream, terminator);
            if (!_styleKnown && OutputEncoding == OutputSink.Utf8 && !reader.IsUtf16) OutputEncoding = reader.Encoding;
            while (reader.ReadLine(out string line, out string lineEnd))
            {
                if (!_styleKnown && lineEnd.Length > 0)
                {
                    Newline = lineEnd;
                    _styleKnown = true;
                }
                yield return line;
            }
        }
        finally
        {
            if (stream != stdin) stream.Dispose();
        }
    }

    internal static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };
}
