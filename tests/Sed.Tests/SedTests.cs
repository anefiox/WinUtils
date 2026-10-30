using System.Text;
using WinUtils.Common.IO;
using WinUtils.Sed;

namespace Sed.Tests;

/// <summary>Tests change the current directory, so they can't run in parallel.</summary>
[CollectionDefinition("sed", DisableParallelization = true)]
public sealed class SedCollection;

[Collection("sed")]
public sealed class SedTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public SedTests()
    {
        _root = Directory.CreateTempSubdirectory("sed-tests-").FullName;
        Environment.CurrentDirectory = _root;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static (int Code, string Out, string Err) Run(string stdin, params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = SedCommand.Run(args, new MemoryStream(Encoding.UTF8.GetBytes(stdin)), stdout, stderr);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Sed(string stdin, params string[] args) => Run(stdin, args).Out;

    [Fact]
    public void Substitutes()
    {
        Assert.Equal("hellO world\n", Sed("hello world\n", "s/o/O/"));
        Assert.Equal("hellO wOrld\n", Sed("hello world\n", "s/o/O/g"));
        Assert.Equal("[hello] world\n", Sed("hello world\n", "-E", @"s/(\w+)/[\1]/"));
    }

    [Fact]
    public void CaseConversionInReplacement()
    {
        Assert.Equal("Hello World\n", Sed("hello world\n", @"s/\b\w/\u&/g"));
        Assert.Equal("HELLO world\n", Sed("hello world\n", @"s/\w\+/\U&/"));
        Assert.Equal("hELLO\n", Sed("hello\n", @"s/.*/\U\l&/"));
    }

    [Fact]
    public void CrlfLinesKeepTheirEndings()
    {
        // '$' must match before the CR, and the output must stay CRLF.
        Assert.Equal("a!\r\nb!\r\n", Sed("a\r\nb\r\n", "s/$/!/"));
        Assert.Equal("a\r\nX\r\nb\r\n", Sed("a\r\nb\r\n", "1a X"));
        Assert.Equal("a,b\r\n", Sed("a\r\nb\r\n", "N;s/\\n/,/"));
    }

    [Fact]
    public void BinaryModeKeepsCarriageReturns()
    {
        Assert.Equal("a\r!\n", Sed("a\r\n", "-b", "s/$/!/"));
    }

    [Fact]
    public void MissingFinalNewlineStaysMissing()
    {
        Assert.Equal("A\nB", Sed("a\nb", "s/.*/\\U&/"));
        Assert.Equal("a\na", Sed("a", "p"));
    }

    [Fact]
    public void InPlaceEditKeepsCrlfAndEncoding()
    {
        File.WriteAllText("crlf.txt", "one\r\ntwo\r\n");
        File.WriteAllText("utf16.txt", "café\r\nthé\r\n", Encoding.Unicode);
        File.WriteAllText("bom.txt", "x\n", new UTF8Encoding(true));

        Assert.Equal(0, Run("", "-i", "s/o/0/g", "crlf.txt", "utf16.txt", "bom.txt").Code);

        Assert.Equal("0ne\r\ntw0\r\n", File.ReadAllText("crlf.txt"));
        byte[] utf16 = File.ReadAllBytes("utf16.txt");
        Assert.Equal([0xFF, 0xFE], utf16[..2]);
        Assert.Equal("café\r\nthé\r\n", Encoding.Unicode.GetString(utf16[2..]));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\n'], File.ReadAllBytes("bom.txt"));
    }

    [Fact]
    public void InPlaceEditWithBackup()
    {
        File.WriteAllText("a.txt", "old\n");
        File.WriteAllText("b.txt", "old\n");
        Run("", "-i.bak", "s/old/new/", "a.txt");
        Run("", "--in-place=orig_*", "s/old/new/", "b.txt");

        Assert.Equal("new\n", File.ReadAllText("a.txt"));
        Assert.Equal("old\n", File.ReadAllText("a.txt.bak"));
        Assert.Equal("new\n", File.ReadAllText("b.txt"));
        Assert.Equal("old\n", File.ReadAllText("orig_b.txt"));
        Assert.Empty(Directory.GetFiles(".", "sed*")); // no temp files left behind
    }

    [Fact]
    public void InPlaceTreatsFilesSeparately()
    {
        File.WriteAllText("a.txt", "1\n2\n");
        File.WriteAllText("b.txt", "3\n4\n");
        Run("", "-i", "1d;$s/$/ last/", "a.txt", "b.txt");
        Assert.Equal("2 last\n", File.ReadAllText("a.txt"));
        Assert.Equal("4 last\n", File.ReadAllText("b.txt"));
    }

    [Fact]
    public void InPlaceWithoutFilesIsAnError()
    {
        var (code, _, err) = Run("", "-i", "p");
        Assert.Equal(1, code);
        Assert.Equal("sed: no input files\n", err);
    }

    [Fact]
    public void FilesAreOneStreamUnlessSeparate()
    {
        File.WriteAllText("a.txt", "1\n2\n");
        File.WriteAllText("b.txt", "3\n4\n");
        Assert.Equal("4\n", Sed("", "-n", "$p", "a.txt", "b.txt"));
        Assert.Equal("2\n4\n", Sed("", "-sn", "$p", "a.txt", "b.txt"));
        Assert.Equal("3\n", Sed("", "-n", "3p", "*.txt"));
    }

    [Fact]
    public void MissingInputFileExitsTwo()
    {
        File.WriteAllText("a.txt", "x\n");
        var (code, output, err) = Run("", "p", "nope.txt", "a.txt");
        Assert.Equal(2, code);
        Assert.Equal("x\nx\n", output);
        Assert.Equal("sed: can't read nope.txt: No such file or directory\n", err);
    }

    [Fact]
    public void WriteAndReadFiles()
    {
        File.WriteAllText("insert.txt", "INSERTED\n");
        Assert.Equal("a\nINSERTED\nb\n", Sed("a\nb\n", "1r insert.txt"));
        Assert.Equal("a\nb\n", Sed("a\nb\n", "1r missing.txt"));

        Sed("x1\ny\nx2\n", "-n", "/x/w out.txt");
        Assert.Equal("x1\nx2\n", File.ReadAllText("out.txt"));

        Sed("a b\n", "s/ /\\n/w split.txt");
        Assert.Equal("a\nb\n", File.ReadAllText("split.txt"));
    }

    [Fact]
    public void ReadLineByLineWithR()
    {
        File.WriteAllText("r.txt", "R1\nR2\n");
        Assert.Equal("a\nR1\nb\nR2\nc\n", Sed("a\nb\nc\n", "R r.txt"));
    }

    [Fact]
    public void ScriptFilesWithCrlfLineEndings()
    {
        File.WriteAllText("script.sed", "s/a/A/\r\n/b/d\r\n");
        Assert.Equal("A\nc\n", Sed("a\nb\nc\n", "-f", "script.sed"));
    }

    [Fact]
    public void QuitWithExitCode()
    {
        var (code, output, _) = Run("a\nb\n", "/a/q5");
        Assert.Equal(5, code);
        Assert.Equal("a\n", output);
    }

    [Fact]
    public void ErrorLocations()
    {
        Assert.Equal("sed: -e expression #1, char 5: unterminated `s' command\n", Run("", "s/a/b").Err);
        Assert.Equal("sed: -e expression #2, char 1: unknown command: `k'\n", Run("", "-e", "p", "-e", "k").Err);
        File.WriteAllText("bad.sed", "p\n\ns/x/");
        Assert.Equal("sed: file bad.sed line 3: unterminated `s' command\n", Run("", "-f", "bad.sed").Err);
    }

    [Fact]
    public void UsageWithoutScript()
    {
        var (code, _, err) = Run("");
        Assert.Equal(1, code);
        Assert.StartsWith("Usage: sed", err);
    }

    [Fact]
    public void Utf16InputIsReencodedForStdout()
    {
        File.WriteAllText("u.txt", "naïve\r\n", Encoding.Unicode);
        Assert.Equal("NAÏVE\r\n", Sed("", @"s/.*/\U&/", "u.txt"));
    }

    [Fact]
    public void NullData()
    {
        Assert.Equal("A\0B\0", Sed("a\0b\0", "-z", @"s/./\U&/"));
    }

    [Fact]
    public void QuietFromHashN()
    {
        Assert.Equal("b\n", Sed("a\nb\n", "#n\n2p"));
    }

    [Fact]
    public void LineWrapForL()
    {
        Assert.Equal("abcde\\\nfghij$\n", Sed("abcdefghij\n", "-n", "-l", "6", "l")); // verified against GNU sed
        Assert.Equal("a\\tb\\001$\n", Sed("a\tb\u0001\n", "-n", "l"));
    }
}
