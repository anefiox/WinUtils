using System.Text;
using System.Text.RegularExpressions;
using WinUtils.Common.Text;

namespace WinUtils.Sed;

/// <summary>A script error; <see cref="Position"/> is the offset in the combined script, or -1.</summary>
internal sealed class ScriptException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;

    /// <summary>GNU sed exits 1 for most script errors, but 4 for a few found after parsing.</summary>
    public int ExitCode { get; init; } = 1;
}

/// <summary>Parses a sed script into a flat list of commands; blocks and branches become jumps.</summary>
internal sealed class ScriptParser(string s, bool extended, Func<string, SedWriter> openWriter)
{
    private readonly List<Command> _commands = [];
    private readonly Stack<(int Index, int Position)> _blocks = new();
    private int _i;
    private bool _anyRegex;

    public List<Command> Parse()
    {
        while (true)
        {
            while (_i < s.Length && (char.IsWhiteSpace(s[_i]) || s[_i] == ';')) _i++;
            if (_i >= s.Length) break;
            if (s[_i] == '#')
            {
                SkipToEndOfLine();
                continue;
            }

            var cmd = new Command { A1 = ParseAddress(first: true) };
            if (cmd.A1 is not null)
            {
                SkipBlanks();
                if (Peek(','))
                {
                    _i++;
                    SkipBlanks();
                    cmd.A2 = ParseAddress(first: false) ?? throw Error("unexpected `,'");
                }
                if (cmd.A1.Kind == AddressKind.Zero && cmd.A2?.Kind != AddressKind.Regex)
                    throw Error("invalid usage of line address 0");
            }
            SkipBlanks();
            if (Peek('!'))
            {
                cmd.Negate = true;
                _i++;
                SkipBlanks();
                if (Peek('!')) throw Error("multiple `!'s");
            }
            if (_i >= s.Length) throw Error("missing command");
            cmd.Name = s[_i++];
            ParseCommand(cmd);
            _commands.Add(cmd);
        }

        if (_blocks.Count > 0) throw new ScriptException("unmatched `{'", _blocks.Peek().Position);
        ResolveLabels();
        return _commands;
    }

    private void ParseCommand(Command cmd)
    {
        switch (cmd.Name)
        {
            case '{':
                _blocks.Push((_commands.Count, _i));
                break;
            case '}':
                if (cmd.A1 is not null) throw Error("} doesn't want any addresses");
                if (_blocks.Count == 0) throw Error("unexpected `}'");
                _commands[_blocks.Pop().Index].Jump = _commands.Count + 1;
                EndOfCommand();
                break;
            case '=' or 'd' or 'D' or 'g' or 'G' or 'h' or 'H' or 'n' or 'N' or 'p' or 'P' or 'x' or 'z' or 'F':
                EndOfCommand();
                break;
            case 'l':
                SkipBlanks();
                cmd.Number = (int)Math.Min(ReadNumber() ?? -1, int.MaxValue);
                EndOfCommand();
                break;
            case 'q' or 'Q':
                if (cmd.A2 is not null) throw Error("command only uses one address");
                SkipBlanks();
                cmd.Number = (int)Math.Min(ReadNumber() ?? 0, 255); // exit codes are a byte
                EndOfCommand();
                break;
            case ':':
                if (cmd.A1 is not null) throw Error(": doesn't want any addresses");
                SkipBlanks();
                cmd.Label = ReadLabel();
                if (cmd.Label.Length == 0) throw Error("\":\" lacks a label");
                break;
            case 'b' or 't' or 'T':
                SkipBlanks();
                cmd.Label = ReadLabel();
                break;
            case 'a' or 'i' or 'c':
                cmd.Text = ReadText();
                break;
            case 'r' or 'R' or 'w' or 'W':
                cmd.Text = ReadFileName();
                if (cmd.Name is 'w' or 'W') cmd.Writer = openWriter(cmd.Text);
                if (cmd.Name == 'R') cmd.Source = new LineSource(cmd.Text);
                break;
            case 's':
                cmd.Substitution = ParseSubstitution();
                break;
            case 'y':
                cmd.Transliteration = ParseTransliteration();
                EndOfCommand();
                break;
            case 'v':
                ReadLabel(); // version requirement: accepted and ignored
                break;
            case 'e':
                throw Error("the `e' command is not supported");
            default:
                throw Error($"unknown command: `{cmd.Name}'");
        }
    }

    private Address? ParseAddress(bool first)
    {
        if (_i >= s.Length) return null;
        char c = s[_i];
        if (char.IsAsciiDigit(c))
        {
            long n = ReadNumber()!.Value;
            if (Peek('~'))
            {
                _i++;
                return new Address { Kind = AddressKind.Step, N = n, Step = ReadNumber() ?? 0 };
            }
            return new Address { Kind = first && n == 0 ? AddressKind.Zero : AddressKind.Line, N = n };
        }
        if (c == '$')
        {
            _i++;
            return new Address { Kind = AddressKind.Last };
        }
        if (!first && c is '+' or '~')
        {
            _i++;
            long n = ReadNumber() ?? throw Error("expected a number after `" + c + "'");
            return new Address { Kind = c == '+' ? AddressKind.Plus : AddressKind.Multiple, N = n };
        }
        if (c is '/' or '\\')
        {
            _i++;
            char delimiter = '/';
            if (c == '\\')
            {
                if (_i >= s.Length) throw Error("unexpected end of script");
                delimiter = s[_i++];
            }
            string raw = ReadDelimited(delimiter, regex: true, "unterminated address regex");
            bool ignoreCase = false, multiline = false;
            while (Peek('I') || Peek('M'))
            {
                if (s[_i++] == 'I') ignoreCase = true;
                else multiline = true;
            }
            return new Address { Kind = AddressKind.Regex, Regex = Compile(raw, ignoreCase, multiline) };
        }
        return null;
    }

    private Substitution ParseSubstitution()
    {
        const string Unterminated = "unterminated `s' command";
        if (_i >= s.Length || s[_i] is '\n' or '\\') throw Error(Unterminated);
        char delimiter = s[_i++];
        string pattern = ReadDelimited(delimiter, regex: true, Unterminated);
        string replacement = ReadDelimited(delimiter, regex: false, Unterminated);

        bool global = false, print = false, ignoreCase = false, multiline = false;
        int occurrence = 0;
        SedWriter? write = null;
        while (_i < s.Length)
        {
            char c = s[_i];
            if (c == 'g') global = true;
            else if (c == 'p') print = true;
            else if (c is 'i' or 'I') ignoreCase = true;
            else if (c is 'm' or 'M') multiline = true;
            else if (char.IsAsciiDigit(c))
            {
                if (occurrence != 0) throw Error("multiple number options to `s' command");
                occurrence = (int)Math.Min(ReadNumber()!.Value, int.MaxValue);
                if (occurrence == 0) throw Error("number option to `s' command may not be zero");
                continue;
            }
            else if (c == 'w')
            {
                _i++;
                write = openWriter(ReadFileName());
                break;
            }
            else if (c == 'e') throw Error("the `e' flag is not supported");
            else if (c is ';' or '\n' or '}' or '#' or ' ' or '\t' or '\r') break;
            else throw Error("unknown option to `s'");
            _i++;
        }
        if (write is null) EndOfCommand();

        var regex = Compile(pattern, ignoreCase, multiline);
        var parts = ParseReplacement(replacement);
        if (regex is not null)
        {
            int groups = regex.GetGroupNumbers().Max();
            foreach (var part in parts)
                if (part is GroupPart { Number: var n } && n > groups)
                    throw Error($"invalid reference \\{n} on `s' command's RHS");
        }
        return new Substitution
        {
            Regex = regex,
            Replacement = parts,
            Global = global,
            Occurrence = Math.Max(occurrence, 1),
            Print = print,
            Write = write,
        };
    }

    private static List<ReplacementPart> ParseReplacement(string text)
    {
        var parts = new List<ReplacementPart>();
        var literal = new StringBuilder();
        void Add(ReplacementPart part)
        {
            if (literal.Length > 0) parts.Add(new LiteralPart(literal.ToString()));
            literal.Clear();
            parts.Add(part);
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '&')
            {
                Add(new GroupPart(0));
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                char d = text[++i];
                switch (d)
                {
                    case >= '0' and <= '9': Add(new GroupPart(d - '0')); break;
                    case 'L' or 'U' or 'E' or 'l' or 'u': Add(new CasePart(d)); break;
                    default: literal.Append(CEscape(d) ?? d); break;
                }
            }
            else
            {
                literal.Append(c);
            }
        }
        if (literal.Length > 0) parts.Add(new LiteralPart(literal.ToString()));
        return parts;
    }

    private Dictionary<char, char> ParseTransliteration()
    {
        const string Unterminated = "unterminated `y' command";
        if (_i >= s.Length || s[_i] is '\n' or '\\') throw Error(Unterminated);
        char delimiter = s[_i++];
        string from = ReadPlain(delimiter, Unterminated), to = ReadPlain(delimiter, Unterminated);
        if (from.Length != to.Length) throw Error("strings for `y' command are different lengths");
        var map = new Dictionary<char, char>();
        for (int k = 0; k < from.Length; k++) map[from[k]] = to[k];
        return map;
    }

    /// <summary>Reads y's strings, where only \\, \n and \delimiter are escapes.</summary>
    private string ReadPlain(char delimiter, string unterminated)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= s.Length) throw Error(unterminated);
            char c = s[_i++];
            if (c == delimiter) return sb.ToString();
            if (c == '\\' && _i < s.Length)
            {
                char d = s[_i++];
                sb.Append(d == delimiter ? d : CEscape(d) ?? d);
            }
            else sb.Append(c);
        }
    }

    /// <summary>
    /// Reads up to an unescaped delimiter. "\delimiter" becomes a literal delimiter. In a regex,
    /// C escapes such as \n and \t become the characters themselves, and a delimiter inside a
    /// bracket expression doesn't end the regex (s/[/]/x/ is valid).
    /// </summary>
    private string ReadDelimited(char delimiter, bool regex, string unterminated)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_i >= s.Length) throw Error(unterminated);
            char c = s[_i++];
            if (c == delimiter) return sb.ToString();
            if (c == '\\' && _i < s.Length)
            {
                char d = s[_i++];
                if (d == delimiter) sb.Append(regex ? LiteralInRegex(d) : d == '&' ? @"\&" : d.ToString());
                else if (regex && d == '\n') sb.Append('\n');
                else if (regex && CEscape(d) is char e) sb.Append(e);
                else sb.Append('\\').Append(d);
                continue;
            }
            if (regex && c == '[')
            {
                CopyBracket(sb, unterminated);
                continue;
            }
            sb.Append(c);
        }
    }

    private void CopyBracket(StringBuilder sb, string unterminated)
    {
        sb.Append('[');
        if (Peek('^')) sb.Append(s[_i++]);
        if (Peek(']')) sb.Append(s[_i++]); // a leading ']' is literal
        while (true)
        {
            if (_i >= s.Length) throw Error(unterminated);
            char c = s[_i++];
            if (c == '[' && _i < s.Length && s[_i] is ':' or '.' or '=')
            {
                char kind = s[_i];
                int close = s.IndexOf(kind + "]", _i + 1, StringComparison.Ordinal);
                if (close < 0) throw Error(unterminated);
                sb.Append(s, _i - 1, close + 2 - (_i - 1));
                _i = close + 2;
                continue;
            }
            if (c == '\\' && Peek('n'))
            {
                sb.Append('\n');
                _i++;
                continue;
            }
            sb.Append(c);
            if (c == ']') return;
        }
    }

    /// <summary>A delimiter escaped in a regex is a literal; escape it if it's special in this syntax.</summary>
    private string LiteralInRegex(char c)
    {
        string special = extended ? @".*[]^$\(){}|+?" : @".*[]^$\";
        return special.Contains(c) ? "\\" + c : c.ToString();
    }

    private static char? CEscape(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        'f' => '\f',
        'v' => '\v',
        'a' => '\a',
        _ => null,
    };

    private Regex? Compile(string pattern, bool ignoreCase, bool multiline)
    {
        if (pattern.Length == 0)
        {
            // An empty regex reuses the last one used at run time, so one must come earlier.
            if (!_anyRegex) throw Error("no previous regular expression");
            return null;
        }
        _anyRegex = true;
        // Without M, ^ and $ match only at the ends of the pattern space, which can hold several
        // lines after N or G. With M they also match around each embedded newline.
        string start = multiline ? PosixRegex.LineStart : "^";
        string end = multiline ? @"(?=\n|\z)" : @"\z";
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline; // '.' matches newline, as in GNU sed
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        try
        {
            return new Regex(PosixRegex.Translate(pattern, extended, ignoreCase, start, end, @"\A", @"\z", out _), options);
        }
        catch (Exception e) when (e is PatternException or ArgumentException)
        {
            throw Error(e.Message);
        }
    }

    /// <summary>
    /// Text for a, i and c. GNU allows a one-line form ("a text") as well as the POSIX form
    /// ("a\" then the text on the next line). A backslash before a newline continues the text.
    /// </summary>
    private string ReadText()
    {
        SkipBlanks();
        if (Peek('\\'))
        {
            _i++;
            if (Peek('\r')) _i++;
            if (Peek('\n')) _i++;
        }
        else if (_i >= s.Length || Peek('\n'))
        {
            throw Error("expected \\ after `a', `c' or `i'");
        }

        var sb = new StringBuilder();
        while (_i < s.Length)
        {
            char c = s[_i++];
            if (c == '\n') break;
            if (c == '\\' && _i < s.Length)
            {
                char d = s[_i++];
                sb.Append(d == 't' ? '\t' : d); // "\" + newline keeps the newline and continues
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private string ReadFileName()
    {
        SkipBlanks();
        int start = _i;
        while (_i < s.Length && s[_i] != '\n') _i++;
        string name = s[start.._i].TrimEnd('\r');
        if (_i < s.Length) _i++;
        if (name.Length == 0) throw Error("missing filename in r/R/w/W commands");
        return name;
    }

    /// <summary>Labels end at a newline or semicolon.</summary>
    private string ReadLabel()
    {
        int start = _i;
        while (_i < s.Length && s[_i] is not ('\n' or ';')) _i++;
        string label = s[start.._i].Trim();
        if (_i < s.Length) _i++;
        return label;
    }

    private long? ReadNumber()
    {
        int start = _i;
        while (_i < s.Length && char.IsAsciiDigit(s[_i])) _i++;
        if (_i == start) return null;
        return long.TryParse(s.AsSpan(start, _i - start), out long n) ? n : long.MaxValue;
    }

    private void EndOfCommand()
    {
        SkipBlanks();
        if (_i >= s.Length) return;
        char c = s[_i];
        if (c is ';' or '\n') _i++;
        else if (c is not ('}' or '#')) throw Error("extra characters after command");
    }

    private void ResolveLabels()
    {
        var labels = new Dictionary<string, int>();
        for (int k = 0; k < _commands.Count; k++)
        {
            if (_commands[k].Name == ':') labels.TryAdd(_commands[k].Label, k); // GNU allows duplicates; the first wins
        }
        foreach (var cmd in _commands)
        {
            if (cmd.Name is not ('b' or 't' or 'T') || cmd.Label.Length == 0) continue;
            cmd.Jump = labels.TryGetValue(cmd.Label, out int target)
                ? target
                : throw new ScriptException($"can't find label for jump to `{cmd.Label}'", -1) { ExitCode = 4 };
        }
    }

    private bool Peek(char c) => _i < s.Length && s[_i] == c;

    private void SkipBlanks()
    {
        while (_i < s.Length && s[_i] is ' ' or '\t' or '\r') _i++;
    }

    private void SkipToEndOfLine()
    {
        while (_i < s.Length && s[_i] != '\n') _i++;
    }

    private ScriptException Error(string message) => new(message, _i);
}
