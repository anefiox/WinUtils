using System.Globalization;

namespace WinUtils.Sort;

internal enum Ordering { Text, Numeric, General, Human, Month, Version, Random }

/// <summary>How one key (or the whole line, without -k) is compared.</summary>
internal sealed class KeyOptions
{
    public Ordering Ordering { get; set; }
    public bool Reverse { get; set; }
    public bool Dictionary { get; set; }        // -d: only blanks and alphanumerics
    public bool IgnoreNonprinting { get; set; } // -i: only printable characters
    public bool FoldCase { get; set; }          // -f
    public bool SkipStartBlanks { get; set; }   // b on the start position
    public bool SkipEndBlanks { get; set; }     // b on the end position

    public bool HasAny => Ordering != Ordering.Text || Reverse || Dictionary || IgnoreNonprinting || FoldCase || SkipStartBlanks || SkipEndBlanks;

    public KeyOptions Clone() => (KeyOptions)MemberwiseClone();

    /// <summary>Applies an option letter (bdfghiMnRrV); returns false if it isn't one.</summary>
    public bool Apply(char c, bool atEnd)
    {
        switch (c)
        {
            case 'b':
                if (atEnd) SkipEndBlanks = true;
                else SkipStartBlanks = SkipEndBlanks = true;
                break;
            case 'd': Dictionary = true; break;
            case 'f': FoldCase = true; break;
            case 'i': IgnoreNonprinting = true; break;
            case 'r': Reverse = true; break;
            case 'g': Ordering = Ordering.General; break;
            case 'h': Ordering = Ordering.Human; break;
            case 'M': Ordering = Ordering.Month; break;
            case 'n': Ordering = Ordering.Numeric; break;
            case 'R': Ordering = Ordering.Random; break;
            case 'V': Ordering = Ordering.Version; break;
            default: return false;
        }
        return true;
    }
}

/// <summary>A -k key: from field StartField, character StartChar, to field EndField, character EndChar.</summary>
internal sealed class KeySpec
{
    public int StartField { get; init; } = 1;
    public int StartChar { get; init; } = 1;
    /// <summary>0 means the end of the line.</summary>
    public int EndField { get; init; }
    /// <summary>0 means the end of the field.</summary>
    public int EndChar { get; init; }
    public required KeyOptions Options { get; set; }
    /// <summary>The key had no option letters, so it takes the global options.</summary>
    public bool Inherits { get; init; }
}

/// <summary>
/// GNU sort's comparison: each key in turn, then (unless -s or -u) the whole lines as a last
/// resort so the output is deterministic. Text comparison is by Unicode code point, which is
/// what GNU sort does in the C.UTF-8 locale.
/// </summary>
internal sealed class KeyComparer : IComparer<string>
{
    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private readonly List<KeySpec> _keys;
    private readonly char? _separator;
    private readonly bool _lastResort;
    private readonly bool _reverseLastResort;
    private readonly int _seed = Random.Shared.Next();

    public KeyComparer(List<KeySpec> keys, KeyOptions global, char? separator, bool lastResort)
    {
        foreach (var key in keys)
            if (key.Inherits) key.Options = global.Clone();
        // Without -k the whole line is the key, compared with the global options.
        _keys = keys.Count > 0 ? keys : [new KeySpec { Options = global }];
        _separator = separator;
        _lastResort = lastResort;
        _reverseLastResort = global.Reverse;
        // The plain whole-line comparison needs no key extraction at all.
        IsPlain = keys.Count == 0 && !global.HasAny;
    }

    /// <summary>No keys or options: lines compare as plain strings.</summary>
    public bool IsPlain { get; }

    /// <summary>Number of ints <see cref="ComputeBounds"/> writes per line: a start and length per key.</summary>
    public int BoundsPerLine => _keys.Count * 2;

    public int Compare(string? a, string? b)
    {
        if (IsPlain) return string.CompareOrdinal(a, b);
        Span<int> boundsA = stackalloc int[BoundsPerLine], boundsB = stackalloc int[BoundsPerLine];
        ComputeBounds(a!, boundsA);
        ComputeBounds(b!, boundsB);
        return Compare(a!, boundsA, b!, boundsB);
    }

    /// <summary>
    /// Finds where each key lies in a line. Sorting computes this once per line rather than
    /// once per comparison, which matters with millions of comparisons.
    /// </summary>
    public void ComputeBounds(string line, Span<int> bounds)
    {
        for (int k = 0; k < _keys.Count; k++)
        {
            var (start, length) = Extract(line, _keys[k]);
            bounds[2 * k] = start;
            bounds[2 * k + 1] = length;
        }
    }

    public int Compare(string a, ReadOnlySpan<int> boundsA, string b, ReadOnlySpan<int> boundsB)
    {
        for (int k = 0; k < _keys.Count; k++)
        {
            var o = _keys[k].Options;
            int c = CompareKey(a.AsSpan(boundsA[2 * k], boundsA[2 * k + 1]), b.AsSpan(boundsB[2 * k], boundsB[2 * k + 1]), o);
            if (c != 0) return o.Reverse ? -c : c;
        }
        if (!_lastResort) return 0;
        int last = string.CompareOrdinal(a, b);
        return _reverseLastResort ? -last : last;
    }

    private (int Start, int Length) Extract(string line, KeySpec key)
    {
        if (_keys.Count == 1 && key.StartField == 1 && key.StartChar == 1 && key.EndField == 0)
        {
            int first = key.Options.SkipStartBlanks ? SkipBlanks(line, 0) : 0;
            return (first, line.Length - first);
        }

        int start = FieldStart(line, key.StartField);
        if (key.Options.SkipStartBlanks) start = SkipBlanks(line, start);
        start = Math.Min(start + key.StartChar - 1, line.Length);

        int end;
        if (key.EndField == 0) end = line.Length;
        else if (key.EndChar == 0) end = FieldEnd(line, key.EndField);
        else
        {
            end = FieldStart(line, key.EndField);
            if (key.Options.SkipEndBlanks) end = SkipBlanks(line, end);
            end = Math.Min(end + key.EndChar, line.Length);
        }
        return end <= start ? (start, 0) : (start, end - start);
    }

    /// <summary>
    /// Start of a 1-based field. With -t, fields are separated by the character. Otherwise a
    /// field is a run of blanks followed by non-blanks, so its leading blanks belong to it.
    /// </summary>
    private int FieldStart(string line, int field)
    {
        int pos = 0;
        for (int f = 1; f < field && pos < line.Length; f++)
        {
            if (_separator is char t)
            {
                int next = line.IndexOf(t, pos);
                pos = next < 0 ? line.Length : next + 1;
            }
            else
            {
                pos = SkipBlanks(line, pos);
                while (pos < line.Length && !IsBlank(line[pos])) pos++;
            }
        }
        return pos;
    }

    private int FieldEnd(string line, int field)
    {
        int pos = FieldStart(line, field);
        if (_separator is char t)
        {
            int next = line.IndexOf(t, pos);
            return next < 0 ? line.Length : next;
        }
        pos = SkipBlanks(line, pos);
        while (pos < line.Length && !IsBlank(line[pos])) pos++;
        return pos;
    }

    private static int SkipBlanks(string line, int pos)
    {
        while (pos < line.Length && IsBlank(line[pos])) pos++;
        return pos;
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';

    private int CompareKey(ReadOnlySpan<char> a, ReadOnlySpan<char> b, KeyOptions o) => o.Ordering switch
    {
        Ordering.Numeric => CompareNumbers(a, b),
        Ordering.General => CompareGeneral(a, b),
        Ordering.Human => CompareHuman(a, b),
        Ordering.Month => Month(a).CompareTo(Month(b)),
        Ordering.Version => CompareVersions(a, b),
        Ordering.Random => CompareRandom(a, b, o),
        _ => CompareText(a, b, o),
    };

    private static int CompareText(ReadOnlySpan<char> a, ReadOnlySpan<char> b, KeyOptions o)
    {
        if (!o.Dictionary && !o.IgnoreNonprinting)
        {
            // Folding to upper case then comparing code points is exactly OrdinalIgnoreCase, which is vectorized.
            return o.FoldCase ? a.CompareTo(b, StringComparison.OrdinalIgnoreCase) : a.SequenceCompareTo(b);
        }
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && Ignored(a[i], o)) i++;
            while (j < b.Length && Ignored(b[j], o)) j++;
            bool endA = i == a.Length, endB = j == b.Length;
            if (endA || endB) return endA == endB ? 0 : endA ? -1 : 1;
            char x = a[i++], y = b[j++];
            if (o.FoldCase)
            {
                x = char.ToUpperInvariant(x);
                y = char.ToUpperInvariant(y);
            }
            if (x != y) return x.CompareTo(y);
        }
    }

    private static bool Ignored(char c, KeyOptions o) =>
        (o.Dictionary && !(IsBlank(c) || char.IsLetterOrDigit(c))) || (o.IgnoreNonprinting && char.IsControl(c));

    /// <summary>
    /// -n: an optional '-', digits and a '.' fraction, after leading blanks, compared as decimal
    /// strings so any length of number works. Anything else counts as zero.
    /// </summary>
    internal static int CompareNumbers(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        bool negA = ParseNumber(a, out var intA, out var fracA);
        bool negB = ParseNumber(b, out var intB, out var fracB);
        bool zeroA = intA.IsEmpty && fracA.IsEmpty, zeroB = intB.IsEmpty && fracB.IsEmpty;
        negA &= !zeroA;
        negB &= !zeroB;
        if (negA != negB) return negA ? -1 : 1;
        int c = intA.Length.CompareTo(intB.Length);
        if (c == 0) c = intA.SequenceCompareTo(intB);
        if (c == 0) c = fracA.SequenceCompareTo(fracB);
        return negA ? -c : c;
    }

    /// <summary>Returns whether the number is negative; the digits come back without insignificant zeros.</summary>
    private static bool ParseNumber(ReadOnlySpan<char> s, out ReadOnlySpan<char> integer, out ReadOnlySpan<char> fraction)
    {
        s = s.TrimStart(" \t");
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        int i = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        integer = s[..i].TrimStart('0');
        fraction = [];
        if (i < s.Length && s[i] == '.')
        {
            int j = i + 1;
            while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
            fraction = s[(i + 1)..j].TrimEnd('0');
        }
        return negative;
    }

    /// <summary>-h: sign, then suffix (none &lt; K &lt; M &lt; G ...), then the number.</summary>
    private static int CompareHuman(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int sa = HumanSign(a), sb = HumanSign(b);
        if (sa != sb) return sa.CompareTo(sb);
        int c = HumanSuffix(a).CompareTo(HumanSuffix(b));
        if (sa < 0) c = -c;
        return c != 0 ? c : CompareNumbers(a, b);
    }

    private static int HumanSign(ReadOnlySpan<char> s)
    {
        bool negative = ParseNumber(s, out var integer, out var fraction);
        return integer.IsEmpty && fraction.IsEmpty ? 0 : negative ? -1 : 1;
    }

    private static int HumanSuffix(ReadOnlySpan<char> s)
    {
        s = s.TrimStart(" \t");
        int i = s.StartsWith('-') ? 1 : 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
        return i < s.Length ? "KMGTPEZYRQ".IndexOf(char.ToUpperInvariant(s[i]) == 'K' ? 'K' : s[i]) + 1 : 0;
    }

    /// <summary>-g: floating point, with non-numbers first, then NaN, then -inf ... +inf.</summary>
    private static int CompareGeneral(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        double? x = ParseDouble(a), y = ParseDouble(b);
        if (x is null || y is null) return (x is not null).CompareTo(y is not null);
        bool nanX = double.IsNaN(x.Value), nanY = double.IsNaN(y.Value);
        if (nanX || nanY) return (!nanX).CompareTo(!nanY);
        return x.Value.CompareTo(y.Value);
    }

    /// <summary>Like strtod: the longest number at the start, after blanks.</summary>
    private static double? ParseDouble(ReadOnlySpan<char> s)
    {
        s = s.TrimStart(" \t");
        // Only characters that can appear in a number (including "inf", "nan", "infinity").
        int limit = 0;
        while (limit < s.Length && "+-.0123456789eEinfatyINFATY".Contains(s[limit])) limit++;
        for (int length = limit; length > 0; length--)
        {
            if (double.TryParse(s[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        }
        return null;
    }

    private static int Month(ReadOnlySpan<char> s)
    {
        s = s.TrimStart(" \t");
        if (s.Length < 3) return 0;
        Span<char> upper = stackalloc char[3];
        s[..3].ToUpperInvariant(upper);
        for (int i = 0; i < Months.Length; i++)
            if (upper.SequenceEqual(Months[i])) return i + 1;
        return 0;
    }

    /// <summary>-V: the Debian version comparison GNU uses (digits compare as numbers, '~' sorts first).</summary>
    internal static int CompareVersions(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        static int Order(char c) => char.IsAsciiDigit(c) ? 0 : char.IsAsciiLetter(c) ? c : c == '~' ? -1 : c + 256;

        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                int x = i < a.Length ? Order(a[i]) : 0, y = j < b.Length ? Order(b[j]) : 0;
                if (x != y) return x - y;
                i++;
                j++;
            }
            while (i < a.Length && a[i] == '0') i++;
            while (j < b.Length && b[j] == '0') j++;
            int firstDifference = 0;
            while (i < a.Length && j < b.Length && char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                if (firstDifference == 0) firstDifference = a[i] - b[j];
                i++;
                j++;
            }
            if (i < a.Length && char.IsAsciiDigit(a[i])) return 1;
            if (j < b.Length && char.IsAsciiDigit(b[j])) return -1;
            if (firstDifference != 0) return firstDifference;
        }
        return a.SequenceCompareTo(b);
    }

    /// <summary>-R: a random but consistent order, so equal keys still end up together.</summary>
    private int CompareRandom(ReadOnlySpan<char> a, ReadOnlySpan<char> b, KeyOptions o)
    {
        int c = Hash(a, o).CompareTo(Hash(b, o));
        return c != 0 ? c : CompareText(a, b, o);
    }

    private int Hash(ReadOnlySpan<char> s, KeyOptions o)
    {
        var hash = new HashCode();
        hash.Add(_seed);
        foreach (char c in s)
            if (!Ignored(c, o)) hash.Add(o.FoldCase ? char.ToUpperInvariant(c) : c);
        return hash.ToHashCode();
    }
}
