using System.Text;
using System.Text.RegularExpressions;

namespace WinUtils.Common.Text;

/// <summary>fnmatch-style wildcard pattern: <c>*</c>, <c>?</c>, <c>[abc]</c>, <c>[!abc]</c>/<c>[^abc]</c> and <c>\</c> escapes.</summary>
public sealed class Glob
{
    private readonly Regex _regex;

    public Glob(string pattern, bool ignoreCase = true)
    {
        Pattern = pattern;
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        _regex = new Regex(ToRegex(pattern), options);
    }

    public string Pattern { get; }

    public bool IsMatch(string text) => _regex.IsMatch(text);

    public static bool HasWildcards(string text) => text.AsSpan().IndexOfAny('*', '?', '[') >= 0;

    public static string ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '\\' when i + 1 < glob.Length:
                    sb.Append(Regex.Escape(glob[++i].ToString()));
                    break;
                case '[':
                    int end = FindClassEnd(glob, i);
                    if (end < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }
                    sb.Append('[');
                    int j = i + 1;
                    if (glob[j] is '!' or '^')
                    {
                        sb.Append('^');
                        j++;
                    }
                    for (; j < end; j++)
                    {
                        char d = glob[j];
                        if (d == '\\' && j + 1 < end) d = glob[++j];
                        else if (d == '-')
                        {
                            sb.Append('-');
                            continue;
                        }
                        sb.Append(char.IsAsciiLetterOrDigit(d) || d == '_' || d > 127 ? d.ToString() : "\\" + d);
                    }
                    sb.Append(']');
                    i = end;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        return sb.Append('$').ToString();
    }

    private static int FindClassEnd(string glob, int open)
    {
        int j = open + 1;
        if (j < glob.Length && glob[j] is '!' or '^') j++;
        if (j < glob.Length && glob[j] == ']') j++; // a leading ']' is literal
        for (; j < glob.Length; j++)
        {
            if (glob[j] == '\\') j++;
            else if (glob[j] == ']') return j;
        }
        return -1;
    }
}
