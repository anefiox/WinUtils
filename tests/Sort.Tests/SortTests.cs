using System.Text;
using WinUtils.Common.IO;
using WinUtils.Sort;

namespace Sort.Tests;

[CollectionDefinition("sort", DisableParallelization = true)]
public sealed class SortCollection;

[Collection("sort")]
public sealed class SortTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public SortTests()
    {
        _root = Directory.CreateTempSubdirectory("sort-tests-").FullName;
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
            code = SortCommand.Run(args, new MemoryStream(Encoding.UTF8.GetBytes(stdin)), stdout, stderr);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Sort(string stdin, params string[] args) => Run(stdin, args).Out;

    [Fact]
    public void SortsByCodePoint() => Assert.Equal("B\na\nb\nÀ\n", Sort("b\nÀ\na\nB\n"));

    [Fact]
    public void KeepsCrlfLineEndings() => Assert.Equal("a\r\nb\r\nc\r\n", Sort("c\r\nb\r\na"));

    [Fact]
    public void Utf16InputIsSortedAndPrintedAsUtf8()
    {
        File.WriteAllText("u.txt", "zoë\r\nañil\r\n", Encoding.Unicode);
        Assert.Equal("añil\r\nzoë\r\n", Run("", "u.txt").Out);
    }

    [Fact]
    public void NumericAndKeys()
    {
        Assert.Equal("-3\n2.5\n9\n10\n", Sort("10\n9\n-3\n2.5\n", "-n"));
        Assert.Equal("a 1\nc 2\nb 3\n", Sort("b 3\na 1\nc 2\n", "-k2n"));
        Assert.Equal("x,1\ny,1\nx,2\n", Sort("x,2\ny,1\nx,1\n", "-t,", "-k2,2n", "-k1,1"));
    }

    [Fact]
    public void StableKeepsInputOrderForEqualKeys()
    {
        Assert.Equal("b 1\na 1\nc 2\n", Sort("b 1\nc 2\na 1\n", "-s", "-k2,2"));
        Assert.Equal("a 1\nb 1\nc 2\n", Sort("b 1\nc 2\na 1\n", "-k2,2")); // last resort orders ties
    }

    [Fact]
    public void UniqueKeepsTheFirstOfEachKey()
    {
        Assert.Equal("b 1\nc 2\n", Sort("b 1\nc 2\na 1\n", "-u", "-k2,2"));
        Assert.Equal("apple\nbanana\n", Sort("banana\nApple\napple\nBANANA\n", "-fu").ToLowerInvariant());
    }

    [Fact]
    public void LargeInputSortsOnDiskWithTheSameResult()
    {
        var random = new Random(42);
        var lines = Enumerable.Range(0, 20_000).Select(_ => $"{random.Next(100_000)} {(char)('a' + random.Next(26))}").ToList();
        string input = string.Join('\n', lines) + "\n";
        string inMemory = Sort(input, "-k2,2", "-k1,1n");
        string onDisk = Sort(input, "-k2,2", "-k1,1n", "-S", "16K", "-T", _root); // forces many temp runs
        Assert.Equal(inMemory, onDisk);
        Assert.Equal(Sort(input, "-u"), Sort(input, "-u", "-S", "16K"));
        Assert.Empty(Directory.GetFiles(_root, "sort*")); // temp runs are cleaned up
    }

    [Fact]
    public void OutputMayOverwriteAnInput()
    {
        File.WriteAllText("data.txt", "c\na\nb\n");
        Run("", "-o", "data.txt", "data.txt");
        Assert.Equal("a\nb\nc\n", File.ReadAllText("data.txt"));

        File.WriteAllText("s1.txt", "a\nc\n");
        File.WriteAllText("s2.txt", "b\nd\n");
        Run("", "-m", "-o", "s1.txt", "s1.txt", "s2.txt");
        Assert.Equal("a\nb\nc\nd\n", File.ReadAllText("s1.txt"));
    }

    [Fact]
    public void RandomSortGroupsEqualKeys()
    {
        string input = string.Concat(Enumerable.Range(0, 50).Select(i => $"{i % 5}\n"));
        var output = Sort(input, "-R").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(50, output.Length);
        // Each key appears as one contiguous run.
        int runs = 1 + Enumerable.Range(1, output.Length - 1).Count(i => output[i] != output[i - 1]);
        Assert.Equal(5, runs);
    }

    [Fact]
    public void CheckReportsTheFirstDisorder()
    {
        var (code, _, err) = Run("a\nc\nb\n", "-c");
        Assert.Equal(1, code);
        Assert.Equal("sort: -:3: disorder: b\n", err);
        Assert.Equal(0, Run("a\nb\n", "-c").Code);
        Assert.Equal("", Run("b\na\n", "-C").Err);
    }

    [Fact]
    public void VersionAndHumanOrders()
    {
        Assert.Equal("v1.2~rc1\nv1.2\nv1.9\nv1.10\n", Sort("v1.10\nv1.2\nv1.9\nv1.2~rc1\n", "-V"));
        Assert.Equal("500\n2K\n3M\n1G\n", Sort("1G\n500\n3M\n2K\n", "-h"));
    }

    [Fact]
    public void BadOptions()
    {
        Assert.Equal("sort: field number is zero: invalid field specification '0'\n", Run("", "-k0").Err);
        Assert.Equal("sort: multi-character tab 'ab'\n", Run("", "-t", "ab").Err);
        Assert.Equal(2, Run("", "missing.txt").Code);
    }
}
