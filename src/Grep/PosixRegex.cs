using System.Text;
using System.Text.RegularExpressions;

namespace WinUtils.Grep;

public sealed class PatternException(string message) : Exception(message);

/// <summary>
/// Translates GNU basic (BRE) and extended (ERE) regular expressions to .NET regex syntax.
/// <para>
/// In a BRE, \( \) \{ \} \| \+ \? are operators and the bare characters are literals; an ERE
/// is the other way round. Both support bracket expressions with POSIX classes ([[:alpha:]]),
/// back-references, and the GNU anchors \&lt; \&gt; \b \B. A '*' at the start of an expression
/// is literal, and '^'/'$' in a BRE are anchors only at the edges of an expression.
/// </para>
/// </summary>
internal static class PosixRegex
{
    /// <summary>Matches where a line starts, inside a buffer that holds many lines.</summary>
    public const string LineStart = @"(?<![^\n])";

    /// <summary>Matches where a line ends (before CRLF or LF, or at the end of the buffer).</summary>
    public const string LineEnd = @"(?=\r?(?:\n|\z))";

    /// <param name="lineAnchors">Translate ^ and $ for searching a buffer of many lines instead of one line.</param>
    public static string Translate(string pattern, bool extended, bool ignoreCase, out bool hasBackReferences, bool lineAnchors = false)
    {
        var translator = new Translator(pattern, extended, ignoreCase, lineAnchors);
        string result = translator.Run();
        hasBackReferences = translator.HasBackReferences;
        return result;
    }

    private sealed class Translator(string p, bool extended, bool ignoreCase, bool lineAnchors)
    {
        private const string UnmatchedBracket = "Unmatched [, [^, [:, [., or [=";

        private readonly string _start = lineAnchors ? LineStart : "^";
        private readonly string _end = lineAnchors ? LineEnd : "$";

        private readonly StringBuilder _sb = new();
        private readonly Stack<int> _groups = new();
        private int _i;
        private int _atomStart = -1;   // where the last quantifiable atom begins in _sb; -1 if none
        private bool _afterQuantifier;
        private bool _atStart = true;  // at the start of the pattern, a group, or an alternative

        public bool HasBackReferences { get; private set; }

        public string Run()
        {
            while (_i < p.Length)
            {
                char c = p[_i++];
                bool special;
                if (c == '\\')
                {
                    if (_i >= p.Length) throw new PatternException("Trailing backslash");
                    c = p[_i++];
                    if (!IsGroupingOrRepetition(c))
                    {
                        Escape(c);
                        continue;
                    }
                    special = !extended;
                }
                else
                {
                    special = c is '*' or '.' or '[' or '^' or '$' || (extended && IsGroupingOrRepetition(c));
                }

                if (!special)
                {
                    Literal(c);
                    continue;
                }

                switch (c)
                {
                    case '(':
                        _groups.Push(_sb.Length);
                        _sb.Append('(');
                        ExpressionStart();
                        break;
                    case ')':
                        if (_groups.Count == 0)
                        {
                            if (!extended) throw new PatternException(@"Unmatched ) or \)");
                            Literal(')');
                            break;
                        }
                        _sb.Append(')');
                        Atom(_groups.Pop());
                        break;
                    case '|':
                        _sb.Append('|');
                        ExpressionStart();
                        break;
                    case '*' or '+' or '?':
                        Quantifier(c.ToString());
                        break;
                    case '{':
                        Interval();
                        break;
                    case '}':
                        Literal('}');
                        break;
                    case '.':
                        Atom(_sb.Length, ".");
                        break;
                    case '[':
                        Bracket();
                        break;
                    case '^':
                        if (extended || _atStart)
                        {
                            // _atStart stays set: in "^*" the star is literal.
                            _sb.Append(_start);
                            _atomStart = -1;
                            _afterQuantifier = false;
                        }
                        else Literal('^');
                        break;
                    case '$':
                        if (extended || AtEndOfBranch()) Anchor(_end);
                        else Literal('$');
                        break;
                }
            }
            if (_groups.Count > 0) throw new PatternException(@"Unmatched ( or \(");
            return _sb.ToString();
        }

        private static bool IsGroupingOrRepetition(char c) => c is '(' or ')' or '{' or '}' or '|' or '+' or '?';

        /// <summary>Handles a backslash followed by <paramref name="c"/> (other than the grouping/repetition chars).</summary>
        private void Escape(char c)
        {
            switch (c)
            {
                case >= '1' and <= '9':
                    HasBackReferences = true;
                    // Wrapped so a following literal digit isn't read as part of the group number.
                    Atom(_sb.Length, $@"(?:\{c})");
                    break;
                case '<': Anchor(@"\b(?=\w)"); break;
                case '>': Anchor(@"\b(?<=\w)"); break;
                case 'b': Anchor(@"\b"); break;
                case 'B': Anchor(@"\B"); break;
                case '`': Anchor(lineAnchors ? _start : @"\A"); break;
                case '\'': Anchor(lineAnchors ? _end : @"\z"); break;
                // \w \W \s \S are GNU extensions; \d \D are our extension (GNU treats them as 'd'/'D').
                case 'w' or 'W' or 's' or 'S' or 'd' or 'D':
                    Atom(_sb.Length, "\\" + c);
                    break;
                default:
                    Literal(c);
                    break;
            }
        }

        private void ExpressionStart()
        {
            _atomStart = -1;
            _afterQuantifier = false;
            _atStart = true;
        }

        private void Anchor(string text)
        {
            _sb.Append(text);
            _atomStart = -1;
            _afterQuantifier = false;
            _atStart = false;
        }

        private void Atom(int start, string? text = null)
        {
            if (text is not null) _sb.Append(text);
            _atomStart = start;
            _afterQuantifier = false;
            _atStart = false;
        }

        private void Literal(char c) => Atom(_sb.Length, Regex.Escape(c.ToString()));

        private void Quantifier(string quantifier)
        {
            if (_atomStart < 0)
            {
                // Nothing to repeat ("*a", "a|*b", "\(*a\)"): POSIX says the operator is literal.
                Literal(quantifier[0]);
                return;
            }
            // POSIX allows stacked repetition ("a**", "a+?"); .NET rejects it or reads "+?" as lazy.
            if (_afterQuantifier) _sb.Insert(_atomStart, "(?:").Append(')');
            _sb.Append(quantifier);
            _afterQuantifier = true;
            _atStart = false;
        }

        private void Interval()
        {
            int start = _i;
            int k = _i;
            while (k < p.Length && char.IsAsciiDigit(p[k])) k++;
            string min = p[start..k];
            bool comma = k < p.Length && p[k] == ',';
            string max = "";
            if (comma)
            {
                int maxStart = ++k;
                while (k < p.Length && char.IsAsciiDigit(p[k])) k++;
                max = p[maxStart..k];
            }
            bool closed = extended
                ? k < p.Length && p[k] == '}'
                : k + 1 < p.Length && p[k] == '\\' && p[k + 1] == '}';

            if (!closed || (min.Length == 0 && !comma) || _atomStart < 0)
            {
                // GNU treats an ERE '{' that doesn't start a valid interval as a literal.
                if (extended || _atomStart < 0)
                {
                    Literal('{');
                    return;
                }
                throw new PatternException(@"Unmatched \{");
            }
            _i = k + (extended ? 1 : 2);

            int lo = min.Length == 0 ? 0 : ParseCount(min);
            if (!comma)
            {
                Quantifier($"{{{lo}}}");
                return;
            }
            if (max.Length == 0)
            {
                Quantifier($"{{{lo},}}");
                return;
            }
            int hi = ParseCount(max);
            if (hi < lo) throw new PatternException(@"Invalid content of \{\}");
            Quantifier($"{{{lo},{hi}}}");
        }

        private static int ParseCount(string digits) =>
            int.TryParse(digits, out int n) && n <= short.MaxValue
                ? n
                : throw new PatternException("Regular expression too big");

        private bool AtEndOfBranch() =>
            _i == p.Length || (p[_i] == '\\' && _i + 1 < p.Length && p[_i + 1] is ')' or '|');

        private void Bracket()
        {
            int j = _i;
            var cls = new StringBuilder("[");
            if (j < p.Length && p[j] == '^')
            {
                cls.Append('^');
                j++;
            }
            int bodyStart = j;
            bool first = true;
            while (true)
            {
                if (j >= p.Length) throw new PatternException(UnmatchedBracket);
                char ch = p[j];
                if (ch == ']' && !first)
                {
                    j++;
                    break;
                }
                first = false;

                if (ch == '[' && j + 1 < p.Length && p[j + 1] is ':' or '=' or '.')
                {
                    char kind = p[j + 1];
                    int close = p.IndexOf(kind + "]", j + 2, StringComparison.Ordinal);
                    if (close < 0) throw new PatternException(UnmatchedBracket);
                    string name = p[(j + 2)..close];
                    j = close + 2;
                    if (kind == ':') cls.Append(NamedClass(name));
                    else foreach (char n in name) cls.Append(EscapeInClass(n)); // [=a=] and [.a.]
                    continue;
                }

                if (j + 2 < p.Length && p[j + 1] == '-' && p[j + 2] != ']')
                {
                    char hi = p[j + 2];
                    if (hi < ch) throw new PatternException("Invalid range end");
                    cls.Append(EscapeInClass(ch)).Append('-').Append(EscapeInClass(hi));
                    j += 3;
                    continue;
                }

                cls.Append(EscapeInClass(ch));
                j++;
            }

            string body = p[bodyStart..(j - 1)];
            if (body.Length >= 2 && body[0] == ':' && body[^1] == ':')
                throw new PatternException("character class syntax is [[:space:]], not [:space:]");

            _i = j;
            Atom(_sb.Length, cls.Append(']').ToString());
        }

        private string NamedClass(string name) => name switch
        {
            "alpha" => @"\p{L}",
            "digit" => "0-9",
            "alnum" => @"\p{L}\p{Nd}",
            // .NET's IgnoreCase doesn't fold Unicode categories, so widen them by hand.
            "upper" => ignoreCase ? @"\p{Lu}\p{Ll}\p{Lt}" : @"\p{Lu}",
            "lower" => ignoreCase ? @"\p{Lu}\p{Ll}\p{Lt}" : @"\p{Ll}",
            "space" => @"\s",
            "blank" => @" \t",
            "punct" => @"\p{P}\p{S}",
            "print" => @"\P{C}",
            "graph" => @"\p{L}\p{M}\p{N}\p{P}\p{S}",
            "cntrl" => @"\p{Cc}",
            "xdigit" => "0-9A-Fa-f",
            _ => throw new PatternException("Invalid character class name"),
        };

        // Inside a .NET class, '\' before a non-word character makes it literal; before a word
        // character it would start an escape, so letters, digits and '_' are left alone.
        private static string EscapeInClass(char c) =>
            char.IsAsciiLetterOrDigit(c) || c == '_' || c > 127 ? c.ToString() : "\\" + c;
    }
}
