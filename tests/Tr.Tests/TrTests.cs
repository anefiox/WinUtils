using System.Text;
using WinUtils.Common.IO;
using WinUtils.Tr;

namespace Tr.Tests;

public sealed class TrTests
{
    private static (int Code, string Out, string Err) Run(byte[] stdin, params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = TrCommand.Run(args, new MemoryStream(stdin), stdout, stderr);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Tr(string stdin, params string[] args) => Run(Encoding.UTF8.GetBytes(stdin), args).Out;

    [Theory]
    [InlineData(new[] { "a-z", "A-Z" }, "hello\n", "HELLO\n")]
    [InlineData(new[] { "-d", "aeiou" }, "education\n", "dctn\n")]
    [InlineData(new[] { "-s", " " }, "a   b  c\n", "a b c\n")]
    [InlineData(new[] { "-cd", "[:digit:]\n" }, "a1b2c3\n", "123\n")]
    [InlineData(new[] { "abc", "x" }, "aabbcc\n", "xxxxxx\n")]
    [InlineData(new[] { "-t", "abc", "xy" }, "abc\n", "xyc\n")]
    [InlineData(new[] { "a-c", "[x*]" }, "abcd\n", "xxxd\n")]
    [InlineData(new[] { "\\n", " " }, "a\nb\n", "a b ")]
    public void Basics(string[] args, string input, string expected) => Assert.Equal(expected, Tr(input, args));

    [Fact]
    public void DeletingCarriageReturnsConvertsCrlfToLf()
    {
        Assert.Equal("one\ntwo\n", Tr("one\r\ntwo\r\n", "-d", "\\r"));
    }

    [Fact]
    public void CaseClassesAreUnicodeAware()
    {
        Assert.Equal("CAFÉ ÆRØ ΣΟΦΙΑ\n", Tr("café ærø σοφια\n", "[:lower:]", "[:upper:]"));
        Assert.Equal("ärger\n", Tr("ÄRGER\n", "[:upper:]", "[:lower:]"));
    }

    [Fact]
    public void ClassesIncludeNonAsciiLetters()
    {
        Assert.Equal("café\n", Tr("café 123!\n", "-cd", "[:alpha:]\n"));
        Assert.Equal("naïve façade\n", Tr("naïve  façade\n", "-s", "[:space:]"));
    }

    [Fact]
    public void RangesAreByCodePoint()
    {
        Assert.Equal("αβγ\n", Tr("ΑΒΓ\n", "Α-Ω", "α-ω"));
    }

    [Fact]
    public void Utf16InputIsPrintedAsUtf8()
    {
        byte[] input = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("héllo\r\n")];
        Assert.Equal("HÉLLO\n", Run(input, "-d", "\\r").Out.ToUpperInvariant());
    }

    [Theory]
    [InlineData(new string[0], "tr: missing operand\n")]
    [InlineData(new[] { "a" }, "tr: missing operand after 'a'\nTwo strings must be given when translating.\n")]
    [InlineData(new[] { "-d", "a", "b" }, "tr: extra operand 'b'\nOnly one string may be given when deleting without squeezing repeats.\n")]
    [InlineData(new[] { "z-a", "x" }, "tr: range-endpoints of 'z-a' are in reverse collating sequence order\n")]
    [InlineData(new[] { "[:foo:]", "x" }, "tr: invalid character class 'foo'\n")]
    [InlineData(new[] { "abc", "" }, "tr: when not truncating set1, string2 must be non-empty\n")]
    [InlineData(new[] { "a", "[:digit:]" }, "tr: when translating, the only character classes that may appear in\nstring2 are 'upper' and 'lower'\n")]
    public void Errors(string[] args, string message)
    {
        var (code, _, err) = Run([], args);
        Assert.Equal(1, code);
        Assert.StartsWith(message, err);
    }
}
