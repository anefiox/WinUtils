using System.Text;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Sed;

internal sealed class SedOptions
{
    public bool Quiet { get; set; }
    public bool Extended { get; set; }
    public bool InPlace { get; set; }
    public string? BackupSuffix { get; set; }
    public bool Separate { get; set; }
    public bool NullData { get; set; }
    public bool Unbuffered { get; set; }
    public bool KeepCarriageReturn { get; set; }
    public int LineLength { get; set; } = 70;
}

public static class SedCommand
{
    private const string Usage = "Usage: sed [OPTION]... {script-only-if-no-other-script} [input-file]...\n";

    private static readonly OptionParser Parser = new(
    [
        new("n", 'n', "quiet"),
        new("n", null, "silent"),
        new("e", 'e', "expression", Required),
        new("f", 'f', "file", Required),
        new("E", 'E', "regexp-extended"),
        new("E", 'r'),
        new("i", 'i', "in-place", Optional),
        new("s", 's', "separate"),
        new("z", 'z', "null-data"),
        new("u", 'u', "unbuffered"),
        new("l", 'l', "line-length", Required),
        new("b", 'b', "binary"),
        new("ignored", null, "posix"),
        new("ignored", null, "debug"),
        new("ignored", null, "follow-symlinks"),
        new("ignored", null, "sandbox"),
        new("help", null, "help"),
        new("version", null, "version"),
    ]);

    /// <summary>Runs sed and returns its exit status: 0 success, 1 bad script, 2 missing input, 4 I/O error.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Fail(string message)
        {
            stdout.Flush();
            stderr.Write($"sed: {message}\n");
            stderr.Flush();
        }

        var o = new SedOptions();
        var script = new StringBuilder();
        var sources = new List<(int Start, string? File, int Expression)>();
        int expressions = 0;
        void AddScript(string text, string? file)
        {
            if (script.Length > 0) script.Append('\n');
            sources.Add((script.Length, file, file is null ? ++expressions : 0));
            script.Append(text);
        }

        ParseResult parsed;
        try
        {
            parsed = Parser.Parse(args);
            foreach (var (id, value, _) in parsed.Options)
            {
                switch (id)
                {
                    case "n": o.Quiet = true; break;
                    case "e": AddScript(value!, null); break;
                    case "f": AddScript(ReadScriptFile(value!, stdin), value); break;
                    case "E": o.Extended = true; break;
                    case "i":
                        o.InPlace = o.Separate = true;
                        o.BackupSuffix = string.IsNullOrEmpty(value) ? null : value;
                        break;
                    case "s": o.Separate = true; break;
                    case "z": o.NullData = true; break;
                    case "u": o.Unbuffered = true; break;
                    case "b": o.KeepCarriageReturn = true; break;
                    case "l":
                        o.LineLength = int.TryParse(value, out int n) && n >= 0 ? n : throw new OptionException($"invalid line length: {value}");
                        break;
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
            Fail(e.Message);
            stderr.Write(Usage);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail($"couldn't open file {e.Message}");
            return 1;
        }

        var operands = parsed.Operands;
        if (script.Length == 0 && sources.Count == 0)
        {
            if (operands.Count == 0)
            {
                stderr.Write(Usage);
                stderr.Flush();
                return 1;
            }
            AddScript(operands[0], null);
            operands.RemoveAt(0);
        }
        var files = Wildcards.ExpandAll(operands);

        string text = script.ToString();
        if (text.StartsWith("#n") && (text.Length == 2 || text[2] == '\n')) o.Quiet = true;

        var stdoutWriter = new SedWriter(stdout);
        var writers = new Dictionary<string, SedWriter>(StringComparer.OrdinalIgnoreCase);
        var files_ = new List<FileStream>();
        SedWriter OpenWriter(string name)
        {
            if (name == "/dev/stdout") return stdoutWriter;
            if (name == "/dev/stderr") return new SedWriter(stderr);
            if (!writers.TryGetValue(name, out var writer))
            {
                var stream = new FileStream(name, FileMode.Create, FileAccess.Write, FileShare.Read);
                files_.Add(stream);
                writers[name] = writer = new SedWriter(OutputSink.ForStream(stream));
            }
            return writer;
        }

        List<Command> commands;
        try
        {
            commands = new ScriptParser(text, o.Extended, OpenWriter).Parse();
        }
        catch (ScriptException e)
        {
            Fail(Locate(e, text, sources) + e.Message);
            return e.ExitCode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail($"couldn't open file {e.Message}");
            return 4;
        }

        int status = 0;
        void InputError(string name, Exception e)
        {
            Fail($"can't read {name}: {Describe(e)}");
            status = 2;
        }

        var runner = new SedRunner(commands, o);
        try
        {
            if (o.InPlace)
            {
                if (files.Count == 0)
                {
                    Fail("no input files");
                    return 1;
                }
                foreach (string file in files)
                {
                    if (runner.Quit) break;
                    if (!EditInPlace(file, runner, o, stdin, InputError, Fail)) status = 4;
                }
            }
            else
            {
                runner.Output = stdoutWriter;
                if (files.Count == 0) files.Add("-");
                foreach (var group in o.Separate ? files.Select(f => new[] { f }) : [files.ToArray()])
                {
                    if (runner.Quit) break;
                    using var input = new Input(group, stdin, o.NullData ? '\0' : '\n', !o.KeepCarriageReturn, InputError);
                    runner.Run(input);
                }
            }
        }
        catch (SedRuntimeException e)
        {
            Fail(e.Message);
            return 4;
        }
        finally
        {
            stdout.Flush();
            foreach (var writer in writers.Values) writer.Sink.Flush();
            foreach (var stream in files_) stream.Dispose();
        }
        return runner.Quit && runner.ExitCode != 0 ? runner.ExitCode : status;
    }

    /// <summary>
    /// -i: writes the result to a temp file beside the original, then swaps it in. The original's
    /// encoding, byte-order mark and line endings are kept.
    /// </summary>
    private static bool EditInPlace(string file, SedRunner runner, SedOptions o, Stream stdin,
        Action<string, Exception> inputError, Action<string> fail)
    {
        if (Directory.Exists(file))
        {
            fail($"couldn't edit {file}: not a regular file");
            return false;
        }
        if (!File.Exists(file))
        {
            inputError(file, new FileNotFoundException());
            return true;
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string temp = Path.Combine(directory, $"sed{Path.GetRandomFileName()}");
        try
        {
            using (var input = new Input([file], stdin, o.NullData ? '\0' : '\n', !o.KeepCarriageReturn, inputError))
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(input.Preamble);
                var sink = OutputSink.ForStream(stream);
                sink.ContentEncoding = input.Encoding;
                runner.Output = new SedWriter(sink);
                runner.Run(input);
                sink.Flush();
            }
            if (o.BackupSuffix is { } suffix)
            {
                string backup = suffix.Contains('*')
                    ? Path.Combine(directory, suffix.Replace("*", Path.GetFileName(file)))
                    : file + suffix;
                File.Copy(file, backup, overwrite: true);
            }
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            fail($"couldn't edit {file}: {Describe(e)}");
            try { File.Delete(temp); }
            catch (IOException) { }
            return false;
        }
    }

    /// <summary>GNU-style error location: "-e expression #2, char 5: " or "file x.sed line 3: ".</summary>
    private static string Locate(ScriptException e, string script, List<(int Start, string? File, int Expression)> sources)
    {
        if (e.Position < 0) return "";
        var source = sources.Last(s => s.Start <= e.Position);
        if (source.File is null) return $"-e expression #{source.Expression}, char {e.Position - source.Start}: ";
        int line = 1 + script.AsSpan(source.Start, e.Position - source.Start).Count('\n');
        return $"file {source.File} line {line}: ";
    }

    private static string ReadScriptFile(string path, Stream stdin)
    {
        using var stream = path == "-" ? null : File.OpenRead(path);
        var reader = new LineReader(stream ?? stdin);
        var lines = new List<string>();
        while (reader.ReadLine(out string line, out _)) lines.Add(line);
        return string.Join('\n', lines);
    }

    private static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        _ => e.Message,
    };
}
