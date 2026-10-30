using System.Globalization;
using System.Text;

namespace WinUtils.Tr;

public sealed class TrException(string message) : Exception(message);

internal abstract record SetItem;
internal sealed record CharItem(char C) : SetItem;
internal sealed record RangeItem(char From, char To) : SetItem;
internal sealed record ClassItem(string Name) : SetItem;
/// <summary>[c*n], or [c*] (Count 0) to fill SET2 to the length of SET1.</summary>
internal sealed record RepeatItem(char C, int Count) : SetItem;

/// <summary>Parses tr's SET syntax: escapes, ranges, [:class:], [=c=], [c*n] and [c*].</summary>
internal static class CharSet
{
    private static readonly string[] Classes =
        ["alnum", "alpha", "blank", "cntrl", "digit", "graph", "lower", "print", "punct", "space", "upper", "xdigit"];

    public static List<SetItem> Parse(string text, Action<string> warn)
    {
        var tokens = Unescape(text, warn);
        var items = new List<SetItem>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var (c, escaped) = tokens[i];
            if (c == '[' && !escaped && i + 1 < tokens.Count)
            {
                char kind = tokens[i + 1].C;
                if (kind is ':' or '=' && FindClose(tokens, i + 2, kind) is int close)
                {
                    string name = new(tokens.Skip(i + 2).Take(close - i - 2).Select(t => t.C).ToArray());
                    if (kind == ':')
                    {
                        if (!Classes.Contains(name)) throw new TrException($"invalid character class '{name}'");
                        items.Add(new ClassItem(name));
                    }
                    else
                    {
                        if (name.Length != 1) throw new TrException($"{name}: equivalence class operand must be a single character");
                        items.Add(new CharItem(name[0]));
                    }
                    i = close + 1;
                    continue;
                }
                // [c*n] or [c*]
                if (i + 2 < tokens.Count && tokens[i + 2] is ('*', false))
                {
                    int end = i + 3;
                    while (end < tokens.Count && char.IsAsciiDigit(tokens[end].C)) end++;
                    if (end < tokens.Count && tokens[end] is (']', false))
                    {
                        string digits = new(tokens.Skip(i + 3).Take(end - i - 3).Select(t => t.C).ToArray());
                        int count = digits.Length == 0 ? 0
                            : digits[0] == '0' ? Convert.ToInt32(digits, 8) // a leading 0 means octal
                            : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n
                            : throw new TrException($"invalid repeat count '{digits}' in [c*n] construct");
                        items.Add(new RepeatItem(tokens[i + 1].C, count));
                        i = end;
                        continue;
                    }
                }
            }
            if (i + 2 < tokens.Count && tokens[i + 1] is ('-', false))
            {
                char to = tokens[i + 2].C;
                if (to < c)
                    throw new TrException($"range-endpoints of '{Display(c)}-{Display(to)}' are in reverse collating sequence order");
                items.Add(new RangeItem(c, to));
                i += 2;
                continue;
            }
            items.Add(new CharItem(c));
        }
        return items;
    }

    private static int? FindClose(List<(char C, bool Escaped)> tokens, int from, char kind)
    {
        for (int i = from; i + 1 < tokens.Count; i++)
            if (tokens[i].C == kind && tokens[i + 1] is (']', false) && i > from) return i;
        return null;
    }

    /// <summary>Resolves backslash escapes, remembering which characters were escaped (and so literal).</summary>
    private static List<(char C, bool Escaped)> Unescape(string text, Action<string> warn)
    {
        var tokens = new List<(char, bool)>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\')
            {
                tokens.Add((c, false));
                continue;
            }
            if (i + 1 == text.Length)
            {
                warn("warning: an unescaped backslash at end of string is not portable");
                tokens.Add(('\\', true));
                continue;
            }
            char d = text[++i];
            if (d is >= '0' and <= '7')
            {
                int value = 0, digits = 0;
                while (digits < 3 && i < text.Length && text[i] is >= '0' and <= '7' && value * 8 + (text[i] - '0') <= 255)
                {
                    value = value * 8 + (text[i++] - '0');
                    digits++;
                }
                i--;
                tokens.Add(((char)value, true));
                continue;
            }
            tokens.Add((d switch
            {
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                _ => d,
            }, true));
        }
        return tokens;
    }

    public static bool IsInClass(string name, char c) => name switch
    {
        "alnum" => char.IsLetterOrDigit(c),
        "alpha" => char.IsLetter(c),
        "blank" => c is ' ' or '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator,
        "cntrl" => char.IsControl(c),
        "digit" => char.IsAsciiDigit(c),
        "graph" => !char.IsControl(c) && !char.IsWhiteSpace(c) && !char.IsSurrogate(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.OtherNotAssigned,
        "lower" => char.IsLower(c),
        "print" => !char.IsControl(c) && !char.IsSurrogate(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.OtherNotAssigned,
        "punct" => char.IsPunctuation(c) || char.IsSymbol(c),
        "space" => char.IsWhiteSpace(c),
        "upper" => char.IsUpper(c),
        "xdigit" => char.IsAsciiHexDigit(c),
        _ => false,
    };

    /// <summary>Every BMP character in a class, in code-point order (surrogates excluded).</summary>
    public static IEnumerable<char> ClassMembers(string name)
    {
        for (int c = 0; c <= 0xFFFF; c++)
            if (c is < 0xD800 or > 0xDFFF && IsInClass(name, (char)c)) yield return (char)c;
    }

    private static string Display(char c) => c < ' ' ? $"\\{Convert.ToString(c, 8).PadLeft(3, '0')}" : c.ToString();
}
