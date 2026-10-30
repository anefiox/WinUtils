using System.Text.RegularExpressions;
using WinUtils.Grep;

namespace Grep.Tests;

public sealed class PosixRegexTests
{
    private static bool Bre(string pattern, string input) =>
        Regex.IsMatch(input, PosixRegex.Translate(pattern, extended: false, ignoreCase: false, out _));

    private static bool Ere(string pattern, string input) =>
        Regex.IsMatch(input, PosixRegex.Translate(pattern, extended: true, ignoreCase: false, out _));

    [Theory]
    [InlineData(@"a\(b\)*c", "abbc", true)]
    [InlineData(@"a(b)c", "a(b)c", true)]       // bare parens are literal in a BRE
    [InlineData(@"a(b)c", "abc", false)]
    [InlineData(@"ab\{2\}c", "abbc", true)]
    [InlineData(@"ab\{2\}c", "abc", false)]
    [InlineData(@"a{2}", "a{2}", true)]          // bare braces are literal in a BRE
    [InlineData(@"cat\|dog", "hotdog", true)]    // GNU extension
    [InlineData(@"a\+", "aa", true)]
    [InlineData(@"a+", "a+", true)]
    [InlineData(@"a+", "aa", false)]
    [InlineData(@"*a", "*a", true)]              // leading star is literal
    [InlineData(@"\(*a\)", "*a", true)]
    [InlineData(@"a^b", "a^b", true)]            // ^ mid-pattern is literal
    [InlineData(@"a$b", "a$b", true)]
    [InlineData(@"^ab$", "ab", true)]
    [InlineData(@"\(a\)\1", "aa", true)]
    [InlineData(@"\(a\)\1", "ab", false)]
    [InlineData(@"\<the\>", "in the end", true)]
    [InlineData(@"\<the\>", "other", false)]
    [InlineData(@"a.c", "abc", true)]
    [InlineData(@"a\.c", "abc", false)]
    public void BasicSyntax(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Bre(pattern, input));

    [Theory]
    [InlineData(@"a(b|c)+d", "abcbd", true)]
    [InlineData(@"a\(b", "a(b", true)]
    [InlineData(@"colou?r", "color", true)]
    [InlineData(@"x{2,3}", "xx", true)]
    [InlineData(@"x{,2}y", "y", true)]           // {,n} means {0,n}
    [InlineData(@"a{", "a{", true)]              // an invalid interval is literal
    [InlineData(@"a{x}", "a{x}", true)]
    [InlineData(@"a**", "aaa", true)]            // stacked repetition is legal in POSIX
    [InlineData(@"a+?b", "b", true)]             // (a+)? -- not a lazy quantifier
    [InlineData(@"(a)\1", "aa", true)]
    [InlineData(@"a)", "a)", true)]              // unmatched ')' is literal in an ERE
    public void ExtendedSyntax(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Ere(pattern, input));

    [Theory]
    [InlineData(@"[[:digit:]]+", "abc123", true)]
    [InlineData(@"^[[:alpha:]]+$", "héllo", true)]
    [InlineData(@"[[:space:]]", "a b", true)]
    [InlineData(@"[[:upper:]]", "abc", false)]
    [InlineData(@"[^[:alnum:]]", "abc", false)]
    [InlineData(@"[]a]", "]", true)]             // leading ']' is literal
    [InlineData(@"[^]a]", "b", true)]
    [InlineData(@"[a\]", @"\", true)]            // backslash is literal in brackets
    [InlineData(@"[a-c]", "b", true)]
    [InlineData(@"[a-]", "-", true)]
    [InlineData(@"[.]", "x", false)]
    [InlineData(@"[_]", "_", true)]
    public void BracketExpressions(string pattern, string input, bool expected) =>
        Assert.Equal(expected, Ere(pattern, input));

    [Theory]
    [InlineData(@"a\(", false, @"Unmatched ( or \(")]
    [InlineData(@"a\)", false, @"Unmatched ) or \)")]
    [InlineData(@"[abc", true, "Unmatched [, [^, [:, [., or [=")]
    [InlineData(@"[[:foo:]]", true, "Invalid character class name")]
    [InlineData(@"[:space:]", true, "character class syntax is [[:space:]], not [:space:]")]
    [InlineData(@"[z-a]", true, "Invalid range end")]
    [InlineData(@"a\", true, "Trailing backslash")]
    [InlineData(@"a\{1", false, @"Unmatched \{")]
    [InlineData(@"a{3,1}", true, @"Invalid content of \{\}")]
    public void ErrorsMatchGnuMessages(string pattern, bool extended, string message)
    {
        var e = Assert.Throws<PatternException>(() => PosixRegex.Translate(pattern, extended, false, out _));
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void ReportsBackReferences()
    {
        PosixRegex.Translate(@"\(a\)\1", false, false, out bool refs);
        Assert.True(refs);
        PosixRegex.Translate(@"\(a\)", false, false, out refs);
        Assert.False(refs);
    }
}
