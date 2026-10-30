using System.Text;
using WinUtils.Common.IO;
using WinUtils.Cut;

namespace Cut.Tests;

public sealed class CutTests
{
    private static (int Code, byte[] Out, string Err) RunBytes(byte[] stdin, params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = CutCommand.Run(args, new MemoryStream(stdin), stdout, stderr);
        }
        return (code, outStream.ToArray(), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Cut(string stdin, params string[] args) =>
        Encoding.UTF8.GetString(RunBytes(Encoding.UTF8.GetBytes(stdin), args).Out);

    [Theory]
    [InlineData("-f2", "a\tb\tc\n", "b\n")]
    [InlineData("-f1,3", "a\tb\tc\n", "a\tc\n")]
    [InlineData("-f2-", "a\tb\tc\n", "b\tc\n")]
    [InlineData("-sf2", "no tabs\na\tb\n", "b\n")]
    public void Fields(string option, string input, string expected) => Assert.Equal(expected, Cut(input, option));

    [Fact]
    public void DelimitersAndComplement()
    {
        Assert.Equal("31\n", Cut("ann,31,oslo\n", "-d,", "-f2"));
        Assert.Equal("ann|oslo\n", Cut("ann,31,oslo\n", "-d,", "-f1,3", "--output-delimiter=|"));
        Assert.Equal("ann,oslo\n", Cut("ann,31,oslo\n", "-d,", "--complement", "-f2"));
    }

    [Fact]
    public void CharactersAreCharactersNotBytes()
    {
        Assert.Equal("caf\n", Cut("café crème\n", "-c1-3"));
        Assert.Equal("café\n", Cut("café crème\n", "-c1-4"));   // GNU would split the é
        Assert.Equal("日本\n", Cut("日本語\n", "-c1-2"));
    }

    [Fact]
    public void BytesAreRawBytes()
    {
        // "é" is two bytes in UTF-8; -b1-4 takes "caf" plus the first byte of é, unchanged.
        var (_, output, _) = RunBytes(Encoding.UTF8.GetBytes("café\n"), "-b1-4");
        Assert.Equal(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xC3, (byte)'\n' }, output);
    }

    [Fact]
    public void CrlfEndingsAreKeptAndNotPartOfTheLine()
    {
        Assert.Equal("c\r\n", Cut("a,b,c\r\n", "-d,", "-f3"));
        Assert.Equal("bc\r\n", Cut("abc\r\n", "-c2-"));
    }

    [Fact]
    public void Utf16Input()
    {
        byte[] input = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("naïve,x\r\n")];
        Assert.Equal("naïve\r\n", Encoding.UTF8.GetString(RunBytes(input, "-d,", "-f1").Out));
    }

    [Theory]
    [InlineData(new[] { "-c0" }, "cut: byte/character positions are numbered from 1\n")]
    [InlineData(new[] { "-f0" }, "cut: fields are numbered from 1\n")]
    [InlineData(new[] { "-c3-1" }, "cut: invalid decreasing range\n")]
    [InlineData(new string[0], "cut: you must specify a list of bytes, characters, or fields\n")]
    [InlineData(new[] { "-c1", "-f1" }, "cut: only one type of list may be specified\n")]
    [InlineData(new[] { "-d", "ab", "-f1" }, "cut: the delimiter must be a single character\n")]
    public void Errors(string[] args, string message)
    {
        var (code, _, err) = RunBytes([], args);
        Assert.Equal(1, code);
        Assert.StartsWith(message, err);
    }
}
