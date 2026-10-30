using System.Text.RegularExpressions;

namespace WinUtils.Grep;

internal abstract class Matcher
{
    public abstract bool IsMatch(string line);

    /// <summary>Finds the next match starting at or after <paramref name="start"/>.</summary>
    public abstract bool TryMatch(string line, int start, out int index, out int length);

    /// <summary>
    /// Optional regex for scanning a whole buffer of lines at once. It finds candidates only: any
    /// line that <see cref="IsMatch"/> accepts also has a match here, starting in that line or an
    /// earlier one, but each candidate line must still be confirmed with <see cref="IsMatch"/>.
    /// </summary>
    public Regex? BufferRegex { get; private init; }

    public static Matcher Create(GrepOptions o)
    {
        // A pattern containing newlines is several patterns, as in GNU grep.
        var patterns = o.Patterns.SelectMany(p => p.Split('\n')).ToList();
        if (patterns.Count == 0) return new NeverMatcher();

        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (o.IgnoreCase) options |= RegexOptions.IgnoreCase;
        if (o.NullData) options |= RegexOptions.Singleline; // records may contain newlines

        bool backReferences = o.Syntax == PatternSyntax.Perl && patterns.Count > 1;
        var perLine = patterns.Select(p => Build(o, p, lineAnchors: false, ref backReferences)).ToList();

        try
        {
            // Joining patterns into one alternation is fastest, but it would renumber the groups
            // that back-references point at, so patterns with back-references stay separate.
            if (perLine.Count > 1 && backReferences)
                return new AnyOfMatcher(perLine.Select(t => new Regex(t, options)).ToArray());

            // A user's -P pattern can't have its anchors rewritten, so it only gets the per-line regex.
            Regex? buffer = null;
            if (o.Syntax != PatternSyntax.Perl && !o.NullData)
                buffer = new Regex(Join(patterns.Select(p => Build(o, p, lineAnchors: true, ref backReferences))), options);
            return new RegexMatcher(new Regex(Join(perLine), options)) { BufferRegex = buffer };
        }
        catch (ArgumentException e)
        {
            throw new PatternException(e.Message);
        }
    }

    private static string Join(IEnumerable<string> regexes) => string.Join("|", regexes.Select(t => $"(?:{t})"));

    private static string Build(GrepOptions o, string pattern, bool lineAnchors, ref bool backReferences)
    {
        string regex;
        switch (o.Syntax)
        {
            case PatternSyntax.Fixed:
                regex = Regex.Escape(pattern);
                break;
            case PatternSyntax.Perl:
                regex = pattern;
                break;
            default:
                regex = PosixRegex.Translate(pattern, o.Syntax == PatternSyntax.Extended, o.IgnoreCase, out bool refs, lineAnchors);
                backReferences |= refs;
                break;
        }
        if (o.LineRegexp)
            return lineAnchors ? $"{PosixRegex.LineStart}(?:{regex}){PosixRegex.LineEnd}" : $@"^(?:{regex})\z";
        // A newline is a non-word character, so this works unchanged across a buffer.
        if (o.WordRegexp) return $@"(?<!\w)(?:{regex})(?!\w)";
        return regex;
    }

    private sealed class NeverMatcher : Matcher
    {
        public override bool IsMatch(string line) => false;

        public override bool TryMatch(string line, int start, out int index, out int length)
        {
            index = length = 0;
            return false;
        }
    }

    private sealed class RegexMatcher(Regex regex) : Matcher
    {
        public override bool IsMatch(string line) => regex.IsMatch(line);

        public override bool TryMatch(string line, int start, out int index, out int length)
        {
            var m = regex.Match(line, start);
            index = m.Index;
            length = m.Length;
            return m.Success;
        }
    }

    /// <summary>Several independent regexes; reports the leftmost match, preferring the longest.</summary>
    private sealed class AnyOfMatcher(Regex[] regexes) : Matcher
    {
        public override bool IsMatch(string line)
        {
            foreach (var r in regexes)
                if (r.IsMatch(line)) return true;
            return false;
        }

        public override bool TryMatch(string line, int start, out int index, out int length)
        {
            index = -1;
            length = 0;
            foreach (var r in regexes)
            {
                var m = r.Match(line, start);
                if (!m.Success) continue;
                if (index < 0 || m.Index < index || (m.Index == index && m.Length > length))
                {
                    index = m.Index;
                    length = m.Length;
                }
            }
            return index >= 0;
        }
    }
}
