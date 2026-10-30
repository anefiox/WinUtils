using System.Text;
using WinUtils.Common.IO;
using WinUtils.Uniq;

namespace Uniq.Tests;

[CollectionDefinition("uniq", DisableParallelization = true)]
public sealed class UniqCollection;

[Collection("uniq")]
public sealed class UniqTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public UniqTests()
    {
        _root = Directory.CreateTempSubdirectory("uniq-tests-").FullName;
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
            code = UniqCommand.Run(args, new MemoryStream(Encoding.UTF8.GetBytes(stdin)), stdout, stderr);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Uniq(string stdin, params string[] args) => Run(stdin, args).Out;

    private const string Dups = "a\na\nb\nc\nc\nc\n";

    [Theory]
    [InlineData(new string[0], "a\nb\nc\n")]
    [InlineData(new[] { "-c" }, "      2 a\n      1 b\n      3 c\n")]
    [InlineData(new[] { "-d" }, "a\nc\n")]
    [InlineData(new[] { "-u" }, "b\n")]
    [InlineData(new[] { "-D" }, "a\na\nc\nc\nc\n")]
    [InlineData(new[] { "--group" }, "a\na\n\nb\n\nc\nc\nc\n")]
    public void Modes(string[] args, string expected) => Assert.Equal(expected, Uniq(Dups, args));

    [Fact]
    public void CrlfAndLfLinesMatchAndKeepTheirEndings()
    {
        Assert.Equal("a\r\nb\n", Uniq("a\r\na\nb\n"));
    }

    [Fact]
    public void IgnoreCaseBeyondAscii()
    {
        Assert.Equal("Ärger\n", Uniq("Ärger\närger\nÄRGER\n", "-i"));
    }

    [Fact]
    public void SkipsFieldsAndCharacters()
    {
        Assert.Equal("1 x apple\n3 z pear\n", Uniq("1 x apple\n2 y apple\n3 z pear\n", "-f", "2"));
        Assert.Equal("ab1\n", Uniq("ab1\ncd1\n", "-s", "2"));
        Assert.Equal("abc\n", Uniq("abc\nabd\n", "-w", "2"));
    }

    [Fact]
    public void WritesToAnOutputFile()
    {
        File.WriteAllText("in.txt", Dups);
        Assert.Equal(0, Run("", "in.txt", "out.txt").Code);
        Assert.Equal("a\nb\nc\n", File.ReadAllText("out.txt"));
    }

    [Fact]
    public void MissingFinalNewlineIsAdded()
    {
        Assert.Equal("a\nb\n", Uniq("a\na\nb"));
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("uniq: missing.txt: No such file or directory\n", Run("", "missing.txt").Err);
        Assert.StartsWith("uniq: extra operand 'c'", Run("", "a", "b", "c").Err);
        Assert.StartsWith("uniq: --group is mutually exclusive with -c/-d/-D/-u", Run("", "--group", "-c").Err);
    }
}
