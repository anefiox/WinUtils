using System.Text.RegularExpressions;

namespace WinUtils.Sed;

internal enum AddressKind
{
    Line,       // N
    Last,       // $
    Regex,      // /re/ or \cREc
    Step,       // first~step
    Zero,       // 0, only as the start of 0,/re/
    Plus,       // addr1,+N
    Multiple,   // addr1,~N
}

internal sealed class Address
{
    public AddressKind Kind { get; init; }
    public long N { get; init; }
    public long Step { get; init; }
    /// <summary>Null for an empty regex (//), which reuses the last regex used.</summary>
    public Regex? Regex { get; init; }
}

internal abstract record ReplacementPart;

internal sealed record LiteralPart(string Text) : ReplacementPart;

/// <summary>&amp; (group 0) or \1 to \9.</summary>
internal sealed record GroupPart(int Number) : ReplacementPart;

/// <summary>\U \L \E (until changed) or \u \l (next character only).</summary>
internal sealed record CasePart(char Op) : ReplacementPart;

internal sealed class Substitution
{
    public Regex? Regex { get; init; }
    public required List<ReplacementPart> Replacement { get; init; }
    public bool Global { get; init; }
    /// <summary>Replace starting at this occurrence (s///N).</summary>
    public int Occurrence { get; init; } = 1;
    public bool Print { get; init; }
    public SedWriter? Write { get; init; }
}

internal sealed class Command
{
    public char Name { get; set; }
    public Address? A1 { get; set; }
    public Address? A2 { get; set; }
    public bool Negate { get; set; }

    /// <summary>'{': index just past the matching '}'. b/t/T: index of the label, or -1 for end of script.</summary>
    public int Jump { get; set; } = -1;
    public string Label { get; set; } = "";
    /// <summary>Text for a/i/c; file name for r/R/w/W.</summary>
    public string Text { get; set; } = "";
    /// <summary>Exit code for q/Q; wrap width for l (-1 means the default).</summary>
    public int Number { get; set; } = -1;
    public Substitution? Substitution { get; set; }
    public Dictionary<char, char>? Transliteration { get; set; }
    public SedWriter? Writer { get; set; }
    public LineSource? Source { get; set; }

    // Range state while the command's address range is active.
    public bool InRange { get; set; }
    public long RangeEnd { get; set; }
}
