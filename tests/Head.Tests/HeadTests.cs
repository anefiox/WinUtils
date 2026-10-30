using System.Text;
using WinUtils.Common.IO;
using WinUtils.Head;

namespace Head.Tests;

[CollectionDefinition("head", DisableParallelization = true)]
public sealed class HeadCollection;

[Collection("head")]
public sealed class HeadTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public HeadTests()
    {
        _root = Directory.CreateTempSubdirectory("head-tests-").FullName;
        Environment.CurrentDirectory = _root;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>A non-seekable stream, like a pipe.</summary>
    private sealed class PipeStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static (int Code, byte[] Out, string Err) Run(byte[] stdin, bool pipe, params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = HeadCommand.Run(args, pipe ? new PipeStream(stdin) : new MemoryStream(stdin), stdout, stderr);
        }
        return (code, outStream.ToArray(), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Head(string stdin, params string[] args) =>
        Encoding.UTF8.GetString(Run(Encoding.UTF8.GetBytes(stdin), pipe: true, args).Out);

    private static readonly string Twenty = string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}\n"));

    [Fact]
    public void FirstTenLinesByDefault() => Assert.Equal(string.Concat(Enumerable.Range(1, 10).Select(i => $"{i}\n")), Head(Twenty));

    [Theory]
    [InlineData(new[] { "-n", "3" }, "1\n2\n3\n")]
    [InlineData(new[] { "-3" }, "1\n2\n3\n")]
    [InlineData(new[] { "-n", "-18" }, "1\n2\n")]
    [InlineData(new[] { "-c", "4" }, "1\n2\n")]
    [InlineData(new[] { "-c", "-45" }, "1\n2\n3\n")] // the input is 51 bytes
    [InlineData(new[] { "-n", "0" }, "")]
    public void Counts(string[] args, string expected)
    {
        // Pipes and seekable input take different paths for the "all but last" forms.
        Assert.Equal(expected, Head(Twenty, args));
        Assert.Equal(expected, Encoding.UTF8.GetString(Run(Encoding.UTF8.GetBytes(Twenty), pipe: false, args).Out));
    }

    [Fact]
    public void KeepsCrlfAndBytesExactly()
    {
        byte[] input = [(byte)'a', 0xE9, (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n'];
        Assert.Equal(input[..4], Run(input, pipe: true, "-n", "1").Out);
    }

    [Fact]
    public void Utf16LinesAreCountedByCodeUnitAndPrintedAsUtf8()
    {
        // U+0A0A is two 0x0A bytes that aren't a newline; only the aligned 0A 00 pairs are.
        File.WriteAllText("u.txt", "ਊfirst\r\nsecond\r\nthird\r\n", Encoding.Unicode);
        Assert.Equal("ਊfirst\r\nsecond\r\n", Encoding.UTF8.GetString(Run([], false, "-n", "2", "u.txt").Out));
        Assert.Equal("ਊfirst\r\n", Encoding.UTF8.GetString(Run([], false, "-n", "-2", "u.txt").Out));
    }

    [Fact]
    public void HeadersForSeveralFiles()
    {
        File.WriteAllText("a.txt", "1\n2\n");
        File.WriteAllText("b.txt", "3\n");
        Assert.Equal("==> a.txt <==\n1\n\n==> b.txt <==\n3\n", Encoding.UTF8.GetString(Run([], false, "-n", "1", "a.txt", "b.txt").Out));
        Assert.Equal("1\n3\n", Encoding.UTF8.GetString(Run([], false, "-qn", "1", "*.txt").Out));
        Assert.Equal("==> a.txt <==\n1\n", Encoding.UTF8.GetString(Run([], false, "-vn1", "a.txt").Out));
    }

    [Fact]
    public void MissingFileIsReported()
    {
        File.WriteAllText("a.txt", "1\n");
        var (code, output, err) = Run([], false, "nope.txt", "a.txt");
        Assert.Equal(1, code);
        Assert.Equal("==> a.txt <==\n1\n", Encoding.UTF8.GetString(output));
        Assert.Equal("head: cannot open 'nope.txt' for reading: No such file or directory\n", err);
    }

    [Theory]
    [InlineData("-n", "abc", "head: invalid number of lines: 'abc'\n")]
    [InlineData("-c", "5X", "head: invalid number of bytes: '5X'\n")]
    public void InvalidCounts(string option, string value, string message)
    {
        var (code, _, err) = Run([], true, option, value);
        Assert.Equal(1, code);
        Assert.StartsWith(message, err);
    }

    [Fact]
    public void SizeSuffixes()
    {
        string big = new('x', 3000);
        Assert.Equal(1024, Head(big, "-c", "1K").Length);
        Assert.Equal(1000, Head(big, "-c", "1kB").Length);
        Assert.Equal(1024, Head(big, "-c", "2b").Length);
    }
}
