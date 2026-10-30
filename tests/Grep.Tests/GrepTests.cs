using System.Text;

namespace Grep.Tests;

[Collection("grep")]
public sealed class GrepTests : IDisposable
{
    private readonly GrepRunner _g = new();

    public void Dispose() => _g.Dispose();

    private const string Fruit = "apple\nbanana\ncherry\napricot\n";

    [Fact]
    public void SelectsMatchingLinesFromStdin()
    {
        var r = _g.Pipe(Fruit, "ap");
        Assert.Equal("apple\napricot\n", r.Stdout);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void ExitsOneWhenNothingMatches()
    {
        var r = _g.Pipe(Fruit, "zzz");
        Assert.Equal("", r.Stdout);
        Assert.Equal(1, r.ExitCode);
    }

    [Fact]
    public void ExitsTwoOnMissingFile()
    {
        var r = _g.Run("x", "missing.txt");
        Assert.Equal(2, r.ExitCode);
        Assert.Equal("grep: missing.txt: No such file or directory\n", r.Stderr);
    }

    [Fact]
    public void NoMessagesSuppressesErrorsButNotStatus()
    {
        var r = _g.Run("-s", "x", "missing.txt");
        Assert.Equal("", r.Stderr);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void MissingPatternIsAUsageError()
    {
        var r = _g.Run();
        Assert.Equal(2, r.ExitCode);
        Assert.StartsWith("Usage: grep", r.Stderr);
    }

    [Fact]
    public void UnknownOptionIsAUsageError()
    {
        var r = _g.Run("--frobnicate", "x");
        Assert.Equal(2, r.ExitCode);
        Assert.StartsWith("grep: unrecognized option '--frobnicate'", r.Stderr);
    }

    [Theory]
    [InlineData("-i", "APPLE", "apple\n")]
    [InlineData("-v", "a", "cherry\n")]
    [InlineData("-c", "an", "1\n")]
    [InlineData("-x", "apple", "apple\n")]
    [InlineData("-m1", "ap", "apple\n")]
    public void BasicFlags(string flag, string pattern, string expected)
    {
        Assert.Equal(expected, _g.Pipe(Fruit, flag, pattern).Stdout);
    }

    [Fact]
    public void LineNumbersAndByteOffsets()
    {
        Assert.Equal("2:banana\n", _g.Pipe(Fruit, "-n", "ban").Stdout);
        Assert.Equal("6:banana\n", _g.Pipe(Fruit, "-b", "ban").Stdout);
        Assert.Equal("2:6:banana\n", _g.Pipe(Fruit, "-nb", "ban").Stdout);
    }

    [Fact]
    public void WordRegexpRequiresWordBoundaries()
    {
        var r = _g.Pipe("cat\nconcat\ncat-like\ncats\n", "-w", "cat");
        Assert.Equal("cat\ncat-like\n", r.Stdout);
    }

    [Fact]
    public void WordRegexpFindsALaterWholeWordMatch()
    {
        // The first "foo" is inside "foobar"; the second one qualifies.
        Assert.Equal("foobar foo\n", _g.Pipe("foobar foo\n", "-w", "foo").Stdout);
    }

    [Fact]
    public void OnlyMatchingPrintsEachMatch()
    {
        var r = _g.Pipe("a1b22c333\n", "-oE", "[0-9]+");
        Assert.Equal("1\n22\n333\n", r.Stdout);
    }

    [Fact]
    public void OnlyMatchingWithByteOffsets()
    {
        Assert.Equal("1:1\n3:22\n", _g.Pipe("a1b22\n", "-obE", "[0-9]+").Stdout);
    }

    [Fact]
    public void MultiplePatterns()
    {
        Assert.Equal("banana\ncherry\n", _g.Pipe(Fruit, "-e", "ban", "-e", "che").Stdout);
        Assert.Equal("banana\ncherry\n", _g.Pipe(Fruit, "ban\nche").Stdout);
    }

    [Fact]
    public void PatternsFromFile()
    {
        _g.File("pats.txt", "ban\r\nche\r\n");
        Assert.Equal("banana\ncherry\n", _g.Pipe(Fruit, "-f", "pats.txt").Stdout);
    }

    [Fact]
    public void EmptyPatternFileMatchesNothing()
    {
        _g.File("empty.txt", "");
        Assert.Equal(1, _g.Pipe(Fruit, "-f", "empty.txt").ExitCode);
    }

    [Fact]
    public void EmptyPatternMatchesEverything()
    {
        Assert.Equal(Fruit, _g.Pipe(Fruit, "").Stdout);
    }

    [Fact]
    public void FixedStringsAreLiteral()
    {
        Assert.Equal("a.b*c\n", _g.Pipe("a.b*c\naxbbc\n", "-F", "a.b*c").Stdout);
    }

    [Fact]
    public void MultipleFilesArePrefixedWithNames()
    {
        _g.File("one.txt", "hello\n").File("two.txt", "hello world\nbye\n");
        var r = _g.Run("hello", "one.txt", "two.txt");
        Assert.Equal("one.txt:hello\ntwo.txt:hello world\n", r.Stdout);
        Assert.Equal("hello\nhello world\n", _g.Run("-h", "hello", "one.txt", "two.txt").Stdout);
        Assert.Equal("one.txt:hello\n", _g.Run("-H", "hello", "one.txt").Stdout);
    }

    [Fact]
    public void CountPerFile()
    {
        _g.File("one.txt", "a\na\n").File("two.txt", "b\n");
        Assert.Equal("one.txt:2\ntwo.txt:0\n", _g.Run("-c", "a", "one.txt", "two.txt").Stdout);
    }

    [Fact]
    public void ListFiles()
    {
        _g.File("one.txt", "a\n").File("two.txt", "b\n");
        Assert.Equal("one.txt\n", _g.Run("-l", "a", "one.txt", "two.txt").Stdout);
        Assert.Equal("two.txt\n", _g.Run("-L", "a", "one.txt", "two.txt").Stdout);
        Assert.Equal("one.txt\0", _g.Run("-lZ", "a", "one.txt", "two.txt").Stdout);
    }

    [Fact]
    public void QuietPrintsNothingAndSucceeds()
    {
        var r = _g.Pipe(Fruit, "-q", "ban");
        Assert.Equal("", r.Stdout);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void QuietMatchOverridesErrors()
    {
        _g.File("a.txt", "hit\n");
        Assert.Equal(0, _g.Run("-q", "hit", "missing.txt", "a.txt").ExitCode);
    }

    [Fact]
    public void WildcardsInFileOperandsAreExpanded()
    {
        _g.File("a.log", "x\n").File("b.log", "x\n").File("c.txt", "x\n");
        Assert.Equal("a.log:x\nb.log:x\n", _g.Run("x", "*.log").Stdout);
    }

    [Fact]
    public void DirectoryWithoutRecursionIsAnError()
    {
        Directory.CreateDirectory(Path.Combine(_g.Root, "dir"));
        var r = _g.Run("x", "dir");
        Assert.Equal("grep: dir: Is a directory\n", r.Stderr);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void RecursiveSearchWithoutOperandOmitsDotPrefix()
    {
        _g.File(@"src\a.cs", "needle\n").File(@"src\sub\b.cs", "needle\n").File("top.txt", "hay\n");
        var r = _g.Run("-r", "needle");
        Assert.Equal("src\\a.cs:needle\nsrc\\sub\\b.cs:needle\n", r.Stdout);
    }

    [Fact]
    public void RecursiveSearchOfNamedDirectoryKeepsItsName()
    {
        _g.File(@"src\a.cs", "needle\n");
        Assert.Equal("src\\a.cs:needle\n", _g.Run("-r", "needle", "src").Stdout);
    }

    [Fact]
    public void RecursiveSearchOfSingleFileOmitsName()
    {
        _g.File("a.txt", "needle\n");
        Assert.Equal("needle\n", _g.Run("-r", "needle", "a.txt").Stdout);
    }

    [Fact]
    public void IncludeExcludeAndExcludeDir()
    {
        _g.File("a.cs", "x\n").File("b.txt", "x\n").File(@"node_modules\c.cs", "x\n").File(@"lib\d.cs", "x\n");
        Assert.Equal("a.cs\nlib\\d.cs\nnode_modules\\c.cs\n", _g.Run("-rl", "--include=*.cs", "x").Stdout);
        Assert.Equal("a.cs\nlib\\d.cs\n", _g.Run("-rl", "--include=*.cs", "--exclude-dir=node_modules", "x").Stdout);
        Assert.Equal("b.txt\n", _g.Run("-rl", "--exclude=*.cs", "x").Stdout);
    }

    [Fact]
    public void ContextLinesAndGroupSeparators()
    {
        string input = "1\n2\nmatch\n4\n5\n6\n7\nmatch\n9\n";
        Assert.Equal("2\nmatch\n4\n--\n7\nmatch\n9\n", _g.Pipe(input, "-C1", "match").Stdout);
        Assert.Equal("2-2\n3:match\n4-4\n--\n7-7\n8:match\n9-9\n", _g.Pipe(input, "-n1", "match").Stdout);
        Assert.Equal("match\n4\nmatch\n9\n", _g.Pipe(input, "-A1", "--no-group-separator", "match").Stdout);
        Assert.Equal("2\nmatch\n::\n7\nmatch\n", _g.Pipe(input, "-B1", "--group-separator=::", "match").Stdout);
    }

    [Fact]
    public void AdjacentContextGroupsMerge()
    {
        Assert.Equal("a\nb\nc\n", _g.Pipe("a\nb\nc\n", "-C1", "[ac]").Stdout);
    }

    [Fact]
    public void MaxCountStillPrintsTrailingContext()
    {
        Assert.Equal("m1\nx\n", _g.Pipe("m1\nx\nm2\ny\n", "-m1", "-A1", "m").Stdout);
    }

    [Fact]
    public void CrlfLinesMatchEndAnchorsAndKeepTheirEndings()
    {
        var r = _g.Pipe("foo\r\nbar\r\n", "o$");
        Assert.Equal("foo\r\n", r.Stdout);
    }

    [Fact]
    public void BinaryOptionKeepsCarriageReturnInTheLine()
    {
        Assert.Equal(1, _g.Pipe("foo\r\n", "-U", "o$").ExitCode);
    }

    [Fact]
    public void FinalLineWithoutNewlineGetsOne()
    {
        Assert.Equal("last\n", _g.Pipe("first\nlast", "last").Stdout);
    }

    [Fact]
    public void Utf16FilesAreDecodedAndOutputAsUtf8()
    {
        _g.File("ps.txt", "héllo\r\nwörld\r\n", Encoding.Unicode); // what Windows PowerShell's '>' writes
        var r = _g.Run("wörld", "ps.txt");
        Assert.Equal("wörld\r\n", r.Stdout);
    }

    [Fact]
    public void NonUtf8BytesPassThroughUnchanged()
    {
        byte[] latin1 = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n', (byte)'x', (byte)'\n'];
        _g.Bytes("cp1252.txt", latin1);
        Assert.Equal(latin1[..5], _g.RunBytes("caf", "cp1252.txt"));
    }

    [Fact]
    public void BinaryFilesReportAMatchInsteadOfPrintingIt()
    {
        _g.Bytes("bin.dat", [(byte)'a', 0, (byte)'b', (byte)'\n']);
        Assert.Equal("grep: bin.dat: binary file matches\n", _g.Run("a", "bin.dat").Stdout);
        Assert.Equal(1, _g.Run("-I", "a", "bin.dat").ExitCode);
        Assert.Equal("a\0b\n", _g.Run("-a", "a", "bin.dat").Stdout);
    }

    [Fact]
    public void NullDataSplitsRecordsOnNul()
    {
        Assert.Equal("two\0", _g.Pipe("one\0two\0", "-z", "tw").Stdout);
    }

    [Fact]
    public void ColorHighlightsMatches()
    {
        var r = _g.Pipe("say hello\n", "--color=always", "hello");
        Assert.Equal("say \e[01;31m\e[Khello\e[m\e[K\n", r.Stdout);
    }

    [Fact]
    public void OptionsMayFollowOperands()
    {
        Assert.Equal("BANANA\n", _g.Pipe("BANANA\n", "banana", "-i").Stdout);
    }

    [Fact]
    public void DoubleDashEndsOptions()
    {
        Assert.Equal("-v\n", _g.Pipe("-v\nx\n", "--", "-v").Stdout);
    }

    [Fact]
    public void LongOptionPrefixesAreAccepted()
    {
        Assert.Equal("1\n", _g.Pipe(Fruit, "--cou", "ban").Stdout);
        Assert.Equal(2, _g.Pipe(Fruit, "--i", "ban").ExitCode); // ambiguous
    }

    [Fact]
    public void InvalidRegexIsReported()
    {
        var r = _g.Pipe(Fruit, "-E", "a(");
        Assert.Equal(2, r.ExitCode);
        Assert.Equal("grep: Unmatched ( or \\(\n", r.Stderr);
    }

    [Fact]
    public void PerlSyntaxUsesDotNetRegex()
    {
        Assert.Equal("a123\n", _g.Pipe("a123\nabc\n", "-P", @"\d{3}").Stdout);
        Assert.Equal("foo.bar\n", _g.Pipe("foo.bar\nfoobar\n", "-P", @"(?<=foo)\.").Stdout);
    }
}
