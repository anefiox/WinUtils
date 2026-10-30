using System.Text;
using WinUtils.Common.IO;
using WinUtils.Wc;

namespace Wc.Tests;

[CollectionDefinition("wc", DisableParallelization = true)]
public sealed class WcCollection;

[Collection("wc")]
public sealed class WcTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public WcTests()
    {
        _root = Directory.CreateTempSubdirectory("wc-tests-").FullName;
        Environment.CurrentDirectory = _root;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class PipeStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static (int Code, string Out, string Err) Run(byte[] stdin, params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = WcCommand.Run(args, new PipeStream(stdin), stdout, stderr);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Wc(string stdin, params string[] args) => Run(Encoding.UTF8.GetBytes(stdin), args).Out;

    [Fact]
    public void DefaultCountsFromAPipe()
    {
        Assert.Equal("      2       3      14\n", Wc("one two\nthree\n"));
    }

    [Theory]
    [InlineData("-m", "café\n", "5\n")]                 // characters, not bytes
    [InlineData("-c", "café\n", "6\n")]
    [InlineData("-w", "a b c\n", "2\n")]           // a non-breaking space joins words
    [InlineData("-w", "a　b c\n", "3\n")]      // other Unicode spaces separate them
    [InlineData("-L", "日本語\n", "6\n")]                // wide characters take two columns
    [InlineData("-L", "é\n", "1\n")]              // a combining accent takes none
    [InlineData("-m", "😀\n", "2\n")]                   // an emoji is one character...
    [InlineData("-L", "😀\n", "2\n")]                   // ...two columns wide
    [InlineData("-L", "a\tb\n", "9\n")]                 // tabs go to the next multiple of 8
    public void UnicodeAwareCounts(string option, string input, string expected) =>
        Assert.Equal(expected, Wc(input, option));

    [Fact]
    public void CrlfCountsAsOneLine()
    {
        Assert.Equal("      2       2      10\n", Wc("one\r\ntwo\r\n", "-lwc"));
        Assert.Equal("3\n", Wc("one\r\ntwo\r\n", "-L"));
    }

    [Fact]
    public void Utf16FilesAreCountedByCharacter()
    {
        File.WriteAllText("u.txt", "héllo wörld\r\nbye\r\n", Encoding.Unicode);
        // 2 lines, 3 words, 18 characters, 38 bytes (2 BOM + 18 * 2), longest line 11.
        Assert.Equal(" 2  3 18 38 11 u.txt\n", Run([], "-lwmcL", "u.txt").Out);
    }

    [Fact]
    public void Latin1FilesCountOneCharacterPerByte()
    {
        File.WriteAllBytes("l.txt", [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n']);
        Assert.Equal("5 l.txt\n", Run([], "-m", "l.txt").Out);
    }

    [Fact]
    public void CharacterSplitAcrossBlocksIsCountedOnce()
    {
        // Put a two-byte é exactly across the 256 KiB block boundary.
        var data = new byte[256 * 1024 + 10];
        data.AsSpan().Fill((byte)'x');
        data[256 * 1024 - 1] = 0xC3;
        data[256 * 1024] = 0xA9;
        data[^1] = (byte)'\n';
        File.WriteAllBytes("split.txt", data);
        Assert.Equal($"{data.Length - 1} split.txt\n", Run([], "-m", "split.txt").Out);
    }

    [Fact]
    public void FilesGetPaddedColumnsAndATotal()
    {
        File.WriteAllText("a.txt", "one two\nthree\n");
        File.WriteAllText("b.txt", "x\n");
        Assert.Equal(" 2  3 14 a.txt\n 1  1  2 b.txt\n 3  4 16 total\n", Run([], "a.txt", "b.txt").Out);
        Assert.Equal("2 a.txt\n", Run([], "-l", "a.txt").Out);
        Assert.Equal(" 2 a.txt\n 1 b.txt\n 3 total\n", Run([], "-l", "*.txt").Out);
    }

    [Theory]
    [InlineData("--total=always", new[] { "a.txt" }, "2 a.txt\n2 total\n")]
    [InlineData("--total=never", new[] { "a.txt", "a.txt" }, " 2 a.txt\n 2 a.txt\n")]
    [InlineData("--total=only", new[] { "a.txt", "a.txt" }, " 4\n")]
    public void TotalModes(string option, string[] files, string expected)
    {
        File.WriteAllText("a.txt", "one two\nthree\n");
        Assert.Equal(expected, Run([], ["-l", option, .. files]).Out);
    }

    [Fact]
    public void FileListFromNulSeparatedInput()
    {
        File.WriteAllText("a.txt", "1\n2\n");
        File.WriteAllText("b.txt", "3\n");
        File.WriteAllText("list", "a.txt\0b.txt\0");
        Assert.Equal("2 a.txt\n1 b.txt\n3 total\n", Run([], "-l", "--files0-from=list").Out);
        Assert.Equal("2 a.txt\n1 b.txt\n3 total\n", Run("a.txt\0b.txt\0"u8.ToArray(), "-l", "--files0-from=-").Out);
    }

    [Fact]
    public void ErrorsAndDirectories()
    {
        File.WriteAllText("a.txt", "x\n");
        Directory.CreateDirectory("sub");
        var (code, output, err) = Run([], "-l", "missing", "sub", "a.txt");
        Assert.Equal(1, code);
        Assert.Equal("      0 sub\n      1 a.txt\n      1 total\n", output);
        Assert.Equal("wc: missing: No such file or directory\nwc: sub: Is a directory\n", err);
    }

    [Fact]
    public void BytesOnlyUsesTheFileSize()
    {
        File.WriteAllBytes("big.bin", new byte[5_000_000]);
        Assert.Equal("5000000 big.bin\n", Run([], "-c", "big.bin").Out);
    }
}
