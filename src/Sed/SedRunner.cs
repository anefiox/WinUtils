using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinUtils.Common.IO;

namespace WinUtils.Sed;

/// <summary>Runtime error in a script, e.g. an empty regex with no previous regex.</summary>
internal sealed class SedRuntimeException(string message) : Exception(message);

/// <summary>Executes a parsed script: the read-execute-print cycle with pattern and hold spaces.</summary>
internal sealed class SedRunner(List<Command> commands, SedOptions options)
{
    private enum CycleEnd { Normal, Delete, Restart, Quit, QuitSilently }

    private readonly List<(string? Text, string? File)> _appends = [];
    private readonly char _separator = options.NullData ? '\0' : '\n';
    private Input _input = null!;
    private string _ps = "";      // pattern space
    private string _hs = "";      // hold space
    // Whether each buffer "had a newline" in the input. A final line without one is printed
    // without one; like GNU sed, the flag travels with the text through g, h, x, G and H.
    private bool _psChomped;
    private bool _hsChomped = true;
    private string _lineEnd = "\n";   // terminator of the latest input line that had one (CRLF stays CRLF)
    private string _newline = "\n";   // ends text from a, i, c, = and l
    private Regex? _lastRegex;
    private bool _replaced;       // for t and T

    /// <summary>Where p, autoprint, =, i, l etc. write: stdout, or the temp file of an in-place edit.</summary>
    public SedWriter Output { get; set; } = null!;

    public bool Quit { get; private set; }

    public int ExitCode { get; private set; }

    public void Run(Input input)
    {
        _input = input;
        // Ranges don't span files when they're processed separately; 0,/re/ starts "before" line 1.
        foreach (var cmd in commands) cmd.InRange = cmd.A1?.Kind == AddressKind.Zero;

        bool reuse = false;
        while (!Quit)
        {
            if (!reuse)
            {
                if (!input.Next(out _ps, out string terminator)) break;
                NewLineRead(terminator);
                _replaced = false;
            }
            reuse = false;

            var end = Execute();
            if (end is CycleEnd.Normal or CycleEnd.Quit && !options.Quiet) PrintPatternSpace();
            FlushAppends();
            if (end is CycleEnd.Quit or CycleEnd.QuitSilently) Quit = true;
            reuse = end == CycleEnd.Restart;
            if (options.Unbuffered || Output.Sink.IsConsole) Output.Sink.Flush();
        }
    }

    private string PatternSpaceEnd => _psChomped ? _lineEnd : "";

    private void NewLineRead(string terminator)
    {
        _psChomped = terminator.Length > 0;
        if (_psChomped)
        {
            _lineEnd = terminator;
            _newline = terminator == "\0" ? "\n" : terminator;
        }
        // Output to the console or a pipe is UTF-8 for UTF-16 input; everything else passes through.
        if (!options.InPlace) Output.Sink.ContentEncoding = _input.IsUtf16 ? OutputSink.Utf8 : _input.Encoding;
    }

    private CycleEnd Execute()
    {
        int pc = 0;
        while (pc < commands.Count)
        {
            var cmd = commands[pc];
            if (Matches(cmd) == cmd.Negate)
            {
                pc = cmd.Name == '{' ? cmd.Jump : pc + 1;
                continue;
            }
            pc++;
            switch (cmd.Name)
            {
                case '{' or '}' or ':':
                    break;
                case '=':
                    Output.Line(_input.LineNumber.ToString(CultureInfo.InvariantCulture), "\n", _newline);
                    break;
                case 'a':
                    _appends.Add((cmd.Text, null));
                    break;
                case 'i':
                    Output.Line(cmd.Text, _newline, _newline);
                    break;
                case 'c':
                    // With a range, the text replaces the whole range, so it's printed once at the end.
                    if (cmd.A2 is null || !cmd.InRange || cmd.Negate) Output.Line(cmd.Text, _newline, _newline);
                    return CycleEnd.Delete;
                case 'd':
                    return CycleEnd.Delete;
                case 'D':
                    int newline = _ps.IndexOf(_separator);
                    if (newline < 0) return CycleEnd.Delete;
                    _ps = _ps[(newline + 1)..];
                    return CycleEnd.Restart;
                case 'F':
                    Output.Line(_input.FileName, _newline, _newline);
                    break;
                case 'g':
                    _ps = _hs;
                    _psChomped = _hsChomped;
                    break;
                case 'G':
                    _ps = _ps + _separator + _hs;
                    _psChomped |= _hsChomped;
                    break;
                case 'h':
                    _hs = _ps;
                    _hsChomped = _psChomped;
                    break;
                case 'H':
                    _hs = _hs + _separator + _ps;
                    _hsChomped |= _psChomped;
                    break;
                case 'x':
                    (_ps, _hs) = (_hs, _ps);
                    (_psChomped, _hsChomped) = (_hsChomped, _psChomped);
                    break;
                case 'l':
                    Output.Line(Unambiguous(_ps, cmd.Number >= 0 ? cmd.Number : options.LineLength), _newline, _newline);
                    break;
                case 'n':
                    if (_input.IsLast) return CycleEnd.Quit; // GNU prints the pattern space and stops
                    if (!options.Quiet) PrintPatternSpace();
                    FlushAppends();
                    _input.Next(out _ps, out string terminator);
                    NewLineRead(terminator);
                    break;
                case 'N':
                    if (_input.IsLast) return CycleEnd.Quit;
                    FlushAppends();
                    _input.Next(out string next, out string nextTerminator);
                    NewLineRead(nextTerminator);
                    _ps = _ps + _separator + next;
                    break;
                case 'p':
                    PrintPatternSpace();
                    break;
                case 'P':
                    int first = _ps.IndexOf(_separator);
                    if (first < 0) PrintPatternSpace();
                    else Output.Line(_ps[..first], _newline, _newline);
                    break;
                case 'q':
                    ExitCode = cmd.Number;
                    return CycleEnd.Quit;
                case 'Q':
                    ExitCode = cmd.Number;
                    return CycleEnd.QuitSilently;
                case 'r':
                    _appends.Add((null, cmd.Text));
                    break;
                case 'R':
                    if (cmd.Source!.Next() is string line) _appends.Add((line, null));
                    break;
                case 's':
                    Substitute(cmd.Substitution!);
                    break;
                case 'b':
                    if (cmd.Jump < 0) return CycleEnd.Normal;
                    pc = cmd.Jump;
                    break;
                case 't' or 'T':
                    bool jump = _replaced == (cmd.Name == 't');
                    _replaced = false;
                    if (!jump) break;
                    if (cmd.Jump < 0) return CycleEnd.Normal;
                    pc = cmd.Jump;
                    break;
                case 'w':
                    cmd.Writer!.Line(_ps, PatternSpaceEnd, _newline);
                    break;
                case 'W':
                    int end = _ps.IndexOf(_separator);
                    cmd.Writer!.Line(end < 0 ? _ps : _ps[..end], _newline, _newline);
                    break;
                case 'y':
                    _ps = string.Create(_ps.Length, (_ps, cmd.Transliteration!), static (span, state) =>
                    {
                        for (int k = 0; k < span.Length; k++)
                            span[k] = state.Item2.TryGetValue(state._ps[k], out char to) ? to : state._ps[k];
                    });
                    break;
                case 'z':
                    _ps = "";
                    break;
            }
        }
        return CycleEnd.Normal;
    }

    private void PrintPatternSpace() => Output.Line(_ps, PatternSpaceEnd, _newline);

    private void FlushAppends()
    {
        foreach (var (text, file) in _appends)
        {
            if (text is not null)
            {
                Output.Line(text, _newline, _newline);
                continue;
            }
            // r copies the file; a missing or unreadable file is silently ignored, as in GNU sed.
            try
            {
                using var stream = File.OpenRead(file!);
                var reader = new LineReader(stream);
                while (reader.ReadLine(out string line, out string terminator)) Output.Line(line, terminator, _newline);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _appends.Clear();
    }

    private bool Matches(Command cmd)
    {
        if (cmd.A1 is null) return true;
        if (cmd.A2 is null) return Matches(cmd.A1);

        long line = _input.LineNumber;
        if (!cmd.InRange)
        {
            if (!Matches(cmd.A1)) return false;
            var a2 = cmd.A2;
            switch (a2.Kind)
            {
                case AddressKind.Line when a2.N > line:
                    cmd.InRange = true;
                    cmd.RangeEnd = a2.N;
                    break;
                case AddressKind.Plus when a2.N > 0:
                    cmd.InRange = true;
                    cmd.RangeEnd = line + a2.N;
                    break;
                case AddressKind.Multiple when a2.N > 0 && line % a2.N != 0:
                    cmd.InRange = true;
                    cmd.RangeEnd = (line / a2.N + 1) * a2.N;
                    break;
                case AddressKind.Last when !_input.IsLast:
                case AddressKind.Regex: // the end regex is checked from the next line on
                    cmd.InRange = true;
                    break;
            }
            return true;
        }

        switch (cmd.A2.Kind)
        {
            case AddressKind.Last:
                if (_input.IsLast) cmd.InRange = false;
                return true;
            case AddressKind.Regex:
                if (Matches(cmd.A2)) cmd.InRange = false;
                return true;
            default:
                // A numeric end. If lines were skipped (inside a block), a range past its end
                // closes without matching.
                if (line < cmd.RangeEnd) return true;
                cmd.InRange = false;
                return line == cmd.RangeEnd;
        }
    }

    private bool Matches(Address a)
    {
        long line = _input.LineNumber;
        return a.Kind switch
        {
            AddressKind.Line => line == a.N,
            AddressKind.Last => _input.IsLast,
            AddressKind.Step => a.Step <= 0 ? line == a.N : line >= a.N && (line - a.N) % a.Step == 0,
            AddressKind.Regex => Use(a.Regex).IsMatch(_ps),
            _ => false,
        };
    }

    /// <summary>An empty regex (//) means the last regex used, whichever command used it.</summary>
    private Regex Use(Regex? regex)
    {
        _lastRegex = regex ?? _lastRegex ?? throw new SedRuntimeException("no previous regular expression");
        return _lastRegex;
    }

    private void Substitute(Substitution sub)
    {
        var regex = Use(sub.Regex);
        var m = regex.Match(_ps);
        if (!m.Success) return;

        var sb = new StringBuilder();
        int pos = 0, count = 0, previousEnd = -1;
        bool replaced = false;
        while (m.Success)
        {
            // POSIX: an empty match right after the previous match doesn't count ("baaac" with
            // s/a*/x/g gives "xbxcx").
            if (m.Length == 0 && m.Index == previousEnd)
            {
                m = m.NextMatch();
                continue;
            }
            count++;
            if (count >= sub.Occurrence)
            {
                sb.Append(_ps, pos, m.Index - pos);
                AppendReplacement(sb, m, sub.Replacement);
                pos = m.Index + m.Length;
                replaced = true;
                if (!sub.Global) break;
            }
            previousEnd = m.Index + m.Length;
            m = m.NextMatch();
        }
        if (!replaced) return;

        sb.Append(_ps, pos, _ps.Length - pos);
        _ps = sb.ToString();
        _replaced = true;
        if (sub.Print) PrintPatternSpace();
        sub.Write?.Line(_ps, PatternSpaceEnd, _newline);
    }

    private static void AppendReplacement(StringBuilder sb, Match m, List<ReplacementPart> parts)
    {
        char mode = 'E';      // U or L until \E
        char next = '\0';     // u or l for the next character only
        foreach (var part in parts)
        {
            string text;
            switch (part)
            {
                case CasePart { Op: var op }:
                    if (op is 'u' or 'l') next = op;
                    else
                    {
                        mode = op;
                        if (op == 'E') next = '\0';
                    }
                    continue;
                case GroupPart { Number: var n }:
                    text = m.Groups[n].Value;
                    break;
                default:
                    text = ((LiteralPart)part).Text;
                    break;
            }
            foreach (char c in text)
            {
                char x = mode switch { 'U' => char.ToUpperInvariant(c), 'L' => char.ToLowerInvariant(c), _ => c };
                if (next != '\0')
                {
                    x = next == 'u' ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
                    next = '\0';
                }
                sb.Append(x);
            }
        }
    }

    /// <summary>The l command: escapes, octal for other control characters, wrapping, and a final '$'.</summary>
    private string Unambiguous(string text, int width)
    {
        var sb = new StringBuilder();
        int column = 0;
        foreach (char c in text)
        {
            string piece = c switch
            {
                '\\' => @"\\",
                '\a' => @"\a",
                '\b' => @"\b",
                '\f' => @"\f",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '\v' => @"\v",
                _ when char.IsControl(c) => "\\" + Convert.ToString(c, 8).PadLeft(3, '0'),
                _ => c.ToString(),
            };
            if (width > 1 && column + piece.Length > width - 1)
            {
                sb.Append('\\').Append(_newline);
                column = 0;
            }
            sb.Append(piece);
            column += piece.Length;
        }
        return sb.Append('$').ToString();
    }
}
