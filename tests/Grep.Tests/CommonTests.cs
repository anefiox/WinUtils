using System.Text;
using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;
using WinUtils.Common.Text;

namespace Grep.Tests;

public sealed class CommonTests
{
    private static readonly OptionParser Parser = new(
    [
        new("a", 'a', "all"),
        new("e", 'e', "regexp", ArgumentKind.Required),
        new("color", null, "color", ArgumentKind.Optional),
        new("color", null, "colour", ArgumentKind.Optional),
    ], allowNumericOptions: true);

    [Fact]
    public void ParsesBundledAndAttachedShortOptions()
    {
        var r = Parser.Parse(["-aeFOO", "file"]);
        Assert.Equal(["a", "e"], r.Options.Select(o => o.Id));
        Assert.Equal("FOO", r.Options[1].Value);
        Assert.Equal(["file"], r.Operands);
    }

    [Fact]
    public void ParsesLongOptionsWithAndWithoutEquals()
    {
        var r = Parser.Parse(["--regexp=x", "--regexp", "y", "--color", "--colo=always"]);
        Assert.Equal(["x", "y", null, "always"], r.Options.Select(o => o.Value));
    }

    [Fact]
    public void ParsesNumericOptions()
    {
        var r = Parser.Parse(["-15a"]);
        Assert.Equal(("#", "15"), (r.Options[0].Id, r.Options[0].Value));
        Assert.Equal("a", r.Options[1].Id);
    }

    [Fact]
    public void RejectsBadOptions()
    {
        Assert.Throws<OptionException>(() => Parser.Parse(["-q"]));
        Assert.Throws<OptionException>(() => Parser.Parse(["-e"]));
        Assert.Throws<OptionException>(() => Parser.Parse(["--all=1"]));
    }

    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "PROGRAM.CS", true)]
    [InlineData("*.cs", "Program.csproj", false)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("[ab]*", "beta", true)]
    [InlineData("[!ab]*", "beta", false)]
    [InlineData("a.b", "axb", false)]
    public void GlobMatching(string pattern, string name, bool expected) =>
        Assert.Equal(expected, new Glob(pattern).IsMatch(name));

    [Fact]
    public void LineReaderHandlesMixedEndingsAndUnterminatedLastLine()
    {
        var reader = new LineReader(new MemoryStream("a\r\nb\nc"u8.ToArray()));
        var lines = new List<(string, string)>();
        while (reader.ReadLine(out string text, out string term)) lines.Add((text, term));
        Assert.Equal([("a", "\r\n"), ("b", "\n"), ("c", "")], lines);
    }

    [Fact]
    public void LineReaderHandlesLinesLongerThanItsBuffer()
    {
        string longLine = new('x', 300_000);
        var reader = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes(longLine + "\nend\n")));
        Assert.True(reader.ReadLine(out string first, out _));
        Assert.Equal(longLine, first);
        Assert.True(reader.ReadLine(out string second, out _));
        Assert.Equal("end", second);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, "utf-8", 3)]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "utf-16", 2)]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, "utf-16BE", 2)]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9 }, "utf-8", 0)]
    [InlineData(new byte[] { 0x63, 0x61, 0x66, 0xE9 }, "iso-8859-1", 0)]
    public void DetectsEncodings(byte[] head, string encoding, int preamble)
    {
        var (detected, length) = LineReader.Detect(head);
        Assert.Equal(encoding, detected.WebName);
        Assert.Equal(preamble, length);
    }

    [Fact]
    public void UtfDetectionToleratesACharacterSplitAtTheBlockEnd()
    {
        Assert.Equal("utf-8", LineReader.Detect([0x63, 0xC3], complete: false).Encoding.WebName);
        Assert.Equal("iso-8859-1", LineReader.Detect([0x63, 0xC3], complete: true).Encoding.WebName);
    }
}
