using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using static WinUtils.Common.CommandLine.ArgumentKind;

namespace WinUtils.Xargs;

internal sealed class XargsOptions
{
    public char? Delimiter { get; set; }
    public string? Eof { get; set; }
    public string? Replace { get; set; }
    public int MaxLines { get; set; }
    public int MaxArgs { get; set; }
    public int MaxProcs { get; set; } = 1;
    public bool Prompt { get; set; }
    public bool NoRunIfEmpty { get; set; }
    public int? MaxChars { get; set; }
    public bool Verbose { get; set; }
    public bool Exit { get; set; }
    public string? ArgFile { get; set; }
    public string? SlotVariable { get; set; }
    public bool ShowLimits { get; set; }
}

public static class XargsCommand
{
    private const string TryHelp = "Try 'xargs --help' for more information.\n";

    private static readonly OptionParser Parser = new(
    [
        new("0", '0', "null"),
        new("a", 'a', "arg-file", Required),
        new("d", 'd', "delimiter", Required),
        new("E", 'E', null, Required),
        new("E", 'e', "eof", Optional),
        new("I", 'I', null, Required),
        new("I", 'i', "replace", Optional),
        new("L", 'L', "max-lines", Required),
        new("L", 'l', null, Optional),
        new("n", 'n', "max-args", Required),
        new("P", 'P', "max-procs", Required),
        new("p", 'p', "interactive"),
        new("r", 'r', "no-run-if-empty"),
        new("s", 's', "max-chars", Required),
        new("t", 't', "verbose"),
        new("x", 'x', "exit"),
        new("o", 'o', "open-tty"),
        new("process-slot-var", null, "process-slot-var", Required),
        new("show-limits", null, "show-limits"),
        new("help", null, "help"),
        new("version", null, "version"),
    ], stopAtFirstOperand: true);

    /// <summary>
    /// Runs xargs and returns its exit status: 0; 123 if a command exited with 1-125; 124 if
    /// one exited with 255; 125 if one crashed; 126 if one couldn't run; 127 if one wasn't found;
    /// 1 for any other error.
    /// </summary>
    /// <param name="readAnswer">Reads a -p answer; the real program reads it from the console.</param>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr, Func<string?>? readAnswer = null)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"xargs: {message}\n");
            stderr.Flush();
        }

        var o = new XargsOptions();
        ParseResult parsed;
        try
        {
            parsed = Parser.Parse(args);
            foreach (var (id, value, spelling) in parsed.Options)
            {
                switch (id)
                {
                    case "0": o.Delimiter = '\0'; break;
                    case "a": o.ArgFile = value; break;
                    case "d": o.Delimiter = ParseDelimiter(value!); break;
                    case "E": o.Eof = string.IsNullOrEmpty(value) ? null : value; break;
                    case "I":
                        o.Replace = string.IsNullOrEmpty(value) ? "{}" : value;
                        (o.MaxLines, o.MaxArgs) = (0, 0);
                        break;
                    case "L":
                        o.MaxLines = value is null ? 1 : Positive(value, spelling);
                        (o.MaxArgs, o.Replace) = (0, null);
                        break;
                    case "n":
                        o.MaxArgs = Positive(value!, spelling);
                        (o.MaxLines, o.Replace) = (0, null);
                        break;
                    case "P":
                        o.MaxProcs = int.TryParse(value, out int procs) && procs >= 0
                            ? procs
                            : throw new OptionException($"invalid number \"{value}\" for -P option");
                        if (o.MaxProcs == 0) o.MaxProcs = 64; // "as many as possible"
                        break;
                    case "p": o.Prompt = o.Verbose = true; break;
                    case "r": o.NoRunIfEmpty = true; break;
                    case "s": o.MaxChars = Positive(value!, spelling); break;
                    case "t": o.Verbose = true; break;
                    case "x": o.Exit = true; break;
                    case "o": throw new OptionException("--open-tty is not supported on Windows");
                    case "process-slot-var": o.SlotVariable = value; break;
                    case "show-limits": o.ShowLimits = true; break;
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

        Stream input = stdin;
        if (o.ArgFile is not null)
        {
            try
            {
                input = o.ArgFile == "-" ? stdin : File.OpenRead(o.ArgFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error($"Cannot open input file '{o.ArgFile}': No such file or directory");
                return 1;
            }
        }

        try
        {
            var command = parsed.Operands.Count > 0 ? parsed.Operands : ["echo"];
            var runner = new Runner(command, o, stdout, stderr, Error, readAnswer);
            var reader = new ItemReader(input, o.Delimiter, wholeLines: o.Replace is not null, o.Eof);
            return runner.Run(reader.Read());
        }
        catch (XargsException e)
        {
            Error(e.Message);
            return 1;
        }
        finally
        {
            if (input != stdin) input.Dispose();
        }
    }

    private static int Positive(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            throw new OptionException($"invalid number \"{value}\" for {option} option");
        return n > 0 ? n : throw new OptionException($"value {n} for {option} option should be >= 1");
    }

    /// <summary>-d takes a character or an escape: \n, \t, \0, \\, \xHH, \NNN (octal).</summary>
    private static char ParseDelimiter(string text)
    {
        if (text.Length == 1) return text[0];
        if (text.Length >= 2 && text[0] == '\\')
        {
            string rest = text[1..];
            char? c = rest switch
            {
                "n" => '\n',
                "t" => '\t',
                "r" => '\r',
                "f" => '\f',
                "v" => '\v',
                "a" => '\a',
                "b" => '\b',
                "\\" => '\\',
                _ when rest[0] == 'x' && int.TryParse(rest[1..], NumberStyles.HexNumber, null, out int hex) && hex < 256 => (char)hex,
                _ when rest.All(ch => ch is >= '0' and <= '7') && Convert.ToInt32(rest, 8) is var octal and < 256 => (char)octal,
                _ => null,
            };
            if (c is char delimiter) return delimiter;
        }
        throw new OptionException($"invalid input delimiter specification {text}: the delimiter must be either a single character or an escape sequence starting with \\.");
    }
}

/// <summary>Builds command lines from the items and runs them, up to -P at a time.</summary>
internal sealed class Runner(List<string> command, XargsOptions o, OutputSink stdout, OutputSink stderr,
    Action<string> error, Func<string?>? readAnswer)
{
    // GNU's default command is /bin/echo. cmd.exe's echo prints quotes and handles arguments
    // differently, so unless a real echo.exe is on PATH, echo is done here.
    private readonly bool _builtinEcho = command[0] == "echo" && CommandLauncher.FindExecutable("echo") is null;
    private readonly List<(Process Process, int Slot)> _running = [];
    private int _status;
    private bool _abort;

    public int Run(IEnumerable<Item> items)
    {
        int systemLimit = _builtinEcho || !CommandLauncher.UsesShell(command[0])
            ? CommandLauncher.MaxProcessCommandLine
            : CommandLauncher.MaxShellCommandLine;
        int limit = Math.Min(o.MaxChars ?? systemLimit, systemLimit);
        int baseLength = CommandLauncher.CommandLineLength(command);
        if (o.ShowLimits) ShowLimits(systemLimit, limit);
        // cmd.exe would only say "not recognized" and exit 1, which looks like an ordinary failure.
        if (!_builtinEcho && !CommandLauncher.Exists(command[0]))
        {
            error($"{command[0]}: No such file or directory");
            return 127;
        }
        if (baseLength > limit)
        {
            error("argument line too long");
            return 1;
        }

        if (o.Replace is string replace)
        {
            foreach (var item in items)
            {
                if (_abort) break;
                Execute(command.Select(arg => arg.Replace(replace, item.Text)).ToList());
            }
        }
        else
        {
            var batch = new List<string>();
            int length = baseLength, lines = 0;
            bool ran = false;
            void Flush()
            {
                Execute([.. command, .. batch]);
                batch.Clear();
                length = baseLength;
                lines = 0;
                ran = true;
            }

            foreach (var item in items)
            {
                if (_abort) break;
                int itemLength = CommandLauncher.ArgumentLength(item.Text);
                if (baseLength + itemLength > limit)
                {
                    error("argument line too long");
                    _status = 1;
                    break;
                }
                if (batch.Count > 0 && length + itemLength > limit)
                {
                    // -x: a batch cut short by the size limit is an error rather than a smaller batch.
                    if (o.Exit && (o.MaxArgs > 0 || o.MaxLines > 0))
                    {
                        error("argument list too long");
                        _status = 1;
                        break;
                    }
                    Flush();
                }
                batch.Add(item.Text);
                length += itemLength;
                if (o.MaxArgs > 0 && batch.Count >= o.MaxArgs) Flush();
                else if (o.MaxLines > 0 && item.EndsLine && ++lines >= o.MaxLines) Flush();
            }
            // With no input at all, GNU runs the command once anyway, unless -r.
            if (!_abort && _status != 1 && (batch.Count > 0 || (!ran && !o.NoRunIfEmpty))) Flush();
        }

        while (_running.Count > 0) WaitForOne();
        stdout.Flush();
        return _status;
    }

    private void Execute(List<string> argv)
    {
        if (_abort) return;
        if (o.Verbose)
        {
            stdout.Flush();
            stderr.Write(string.Join(' ', argv) + (o.Prompt ? " ?..." : "\n"));
            stderr.Flush();
            if (o.Prompt && readAnswer?.Invoke()?.TrimStart() is not ['y' or 'Y', ..]) return;
        }

        if (_builtinEcho)
        {
            stdout.Write(string.Join(' ', argv.Skip(1)) + "\n");
            return;
        }

        while (_running.Count >= o.MaxProcs) WaitForOne();
        stdout.Flush(); // our output stays ahead of the command's

        var psi = CommandLauncher.StartInfo(argv);
        // Like GNU xargs, commands get an empty standard input: ours is the list of items.
        psi.RedirectStandardInput = true;
        int slot = Enumerable.Range(0, o.MaxProcs).First(s => _running.All(r => r.Slot != s));
        if (o.SlotVariable is not null) psi.Environment[o.SlotVariable] = slot.ToString(CultureInfo.InvariantCulture);
        try
        {
            var process = Process.Start(psi)!;
            process.StandardInput.Close();
            _running.Add((process, slot));
        }
        catch (Win32Exception e)
        {
            bool notFound = e.NativeErrorCode is 2 or 3;
            error($"{argv[0]}: {(notFound ? "No such file or directory" : e.Message)}");
            _status = notFound ? 127 : 126;
            _abort = true;
            return;
        }
        if (o.MaxProcs == 1) WaitForOne();
    }

    private void WaitForOne()
    {
        int index = _running.Count == 1
            ? 0
            : Task.WaitAny(_running.Select(r => r.Process.WaitForExitAsync()).ToArray());
        var (process, _) = _running[index];
        _running.RemoveAt(index);
        process.WaitForExit();
        int code = process.ExitCode;
        string name = command[0];
        process.Dispose();

        switch (code)
        {
            case 0:
                break;
            case 255:
                error($"{name}: exited with status 255; aborting");
                _status = 124;
                _abort = true;
                break;
            case < 0:
                // An NTSTATUS crash code (e.g. 0xC0000005), the Windows counterpart of a fatal signal.
                error($"{name}: terminated by signal 0x{code:X8}");
                _status = 125;
                _abort = true;
                break;
            default:
                if (_status == 0) _status = 123;
                break;
        }
    }

    private void ShowLimits(int systemLimit, int limit)
    {
        long environment = Environment.GetEnvironmentVariables().Count;
        stderr.Write(
            $"Your environment variables take up {environment} entries\n" +
            $"Upper limit on argument length (this system): {systemLimit}\n" +
            $"Maximum length of command we could actually use: {systemLimit}\n" +
            $"Size of command buffer we are actually using: {limit}\n" +
            $"Maximum parallelism (--max-procs must be no greater): {int.MaxValue}\n\n");
        stderr.Flush();
    }
}
