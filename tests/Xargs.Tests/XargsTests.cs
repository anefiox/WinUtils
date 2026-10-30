using System.Text;
using WinUtils.Common.IO;
using WinUtils.Xargs;

namespace Xargs.Tests;

[CollectionDefinition("xargs", DisableParallelization = true)]
public sealed class XargsCollection;

[Collection("xargs")]
public sealed class XargsTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public XargsTests()
    {
        _root = Directory.CreateTempSubdirectory("xargs-tests-").FullName;
        Environment.CurrentDirectory = _root;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static (int Code, string Out, string Err) Run(string stdin, string[] args, Func<string?>? answers = null)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = XargsCommand.Run(args, new MemoryStream(Encoding.UTF8.GetBytes(stdin)), stdout, stderr, answers);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string Xargs(string stdin, params string[] args) => Run(stdin, args).Out;

    [Fact]
    public void BuiltinEchoIsTheDefault()
    {
        Assert.Equal("a b c\n", Xargs("a\nb c\n"));
        Assert.Equal("a \"quoted\" b\n", Xargs("a '\"quoted\"' b\n", "echo")); // not cmd.exe's echo
    }

    [Fact]
    public void CrlfInputDoesNotLeakCarriageReturns()
    {
        Assert.Equal("[a] [b c]\n", Xargs("a\r\nb c\r\n", "-I", "%", "echo", "[%]").Replace("\n[", " ["));
        Assert.Equal("a|b|", Xargs("a\r\nb\r\n", "-d", "\\n", "-n", "1").Replace("\n", "|"));
        Assert.Equal("a b\n", Xargs("a\r\nb\r\n"));
    }

    [Fact]
    public void BackslashesInWindowsPathsAreLiteral()
    {
        Assert.Equal(@"C:\src\a.cs \\server\share\b.txt" + "\n", Xargs(@"C:\src\a.cs" + "\n" + @"\\server\share\b.txt" + "\n"));
        Assert.Equal(@"[src\FindW]" + "\n", Xargs(@"src\FindW" + "\n", "-I", "{}", "echo", "[{}]"));
        // Escaped blanks and quotes still work.
        Assert.Equal("a b\nit's\n", Xargs(@"a\ b it\'s" + "\n", "-n", "1"));
    }

    [Fact]
    public void OptionsAfterTheCommandBelongToTheCommand()
    {
        Assert.Equal("-n 1 x y\n", Xargs("x y\n", "echo", "-n", "1"));
    }

    [Fact]
    public void RunsProgramsAndCmdBuiltins()
    {
        File.WriteAllText("a.txt", "");
        File.WriteAllText("b.txt", "");
        // del is a cmd.exe built-in, so it has to go through cmd /c.
        Assert.Equal(0, Run("a.txt b.txt\n", ["del"]).Code);
        Assert.False(File.Exists("a.txt") || File.Exists("b.txt"));

        // -I with a real program: cmd.exe writes one file per item.
        Run("one\ntwo\n", ["-I", "{}", "cmd", "/c", "echo {}> {}.out"]);
        Assert.Equal("one", File.ReadAllText("one.out").Trim());
        Assert.Equal("two", File.ReadAllText("two.out").Trim());
    }

    [Fact]
    public void RunsBatchFiles()
    {
        File.WriteAllText("args.cmd", "@echo %*>> args.log\r\n");
        Run("a b c d e\n", ["-n", "2", @".\args.cmd"]);
        Assert.Equal("a b\r\nc d\r\ne\r\n", File.ReadAllText("args.log"));
    }

    [Theory]
    [InlineData("exit 0", 0)]
    [InlineData("exit 1", 123)]
    [InlineData("exit 7", 123)]
    [InlineData("exit 255", 124)]
    public void ExitStatuses(string command, int expected)
    {
        Assert.Equal(expected, Run("x\n", ["cmd", "/c", command]).Code);
    }

    [Fact]
    public void Exit255StopsFurtherCommands()
    {
        var (code, _, err) = Run("1\n2\n3\n", ["-n", "1", "cmd", "/c", "exit 255"]);
        Assert.Equal(124, code);
        Assert.Equal("xargs: cmd: exited with status 255; aborting\n", err);
    }

    [Fact]
    public void MissingCommandExits127()
    {
        var (code, _, _) = Run("x\n", [@"C:\no\such\program.exe"]);
        Assert.Equal(127, code);
        Assert.Equal(127, Run("x\n", ["no-such-command-anywhere"]).Code); // via cmd /c, which reports 9009
    }

    [Fact]
    public void ShellCommandsAreBatchedToFitCmdLimit()
    {
        // 2000 arguments of 10 characters would overflow cmd.exe's 8191-character line.
        File.WriteAllText("count.cmd", "@echo %*>> calls.log\r\n");
        string items = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"item{i:D6}"));
        Assert.Equal(0, Run(items, [@".\count.cmd"]).Code);
        var calls = File.ReadAllLines("calls.log");
        Assert.True(calls.Length >= 3, $"expected several batches, got {calls.Length}");
        Assert.All(calls, line => Assert.True(line.Length < 8191));
        Assert.Equal(2000, calls.Sum(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void ParallelRunsWithSlotNumbers()
    {
        string items = string.Join('\n', Enumerable.Range(1, 8));
        // "slot" keeps the digit away from '>': cmd.exe reads "0>" as a redirection of handle 0.
        Assert.Equal(0, Run(items, ["-P", "4", "-I", "{}", "--process-slot-var=SLOT", "cmd", "/c", "echo slot%SLOT%> {}.out"]).Code);
        var slots = Enumerable.Range(1, 8).Select(i => int.Parse(File.ReadAllText($"{i}.out").Trim()["slot".Length..])).ToList();
        Assert.All(slots, s => Assert.InRange(s, 0, 3));
    }

    [Fact]
    public void PromptRunsOnlyConfirmedCommands()
    {
        var answers = new Queue<string?>(["y", "n", "yes"]);
        var (_, output, err) = Run("a b c\n", ["-p", "-n", "1"], () => answers.Dequeue());
        Assert.Equal("a\nc\n", output);
        Assert.Equal("echo a ?...echo b ?...echo c ?...", err);
    }

    [Fact]
    public void VerbosePrintsCommandsToStderr()
    {
        var (_, output, err) = Run("a b\n", ["-t", "-n", "1"]);
        Assert.Equal("a\nb\n", output);
        Assert.Equal("echo a\necho b\n", err);
    }

    [Fact]
    public void ArgumentFileLeavesStdinAlone()
    {
        File.WriteAllText("list.txt", "x\ny\n");
        Assert.Equal("x y\n", Xargs("ignored\n", "-a", "list.txt"));
    }

    [Theory]
    [InlineData("-d", "\\x2c", "x,y", "x y\n")]
    [InlineData("-d", "\\t", "x\ty", "x y\n")]
    [InlineData("-d", "\\054", "x,y", "x y\n")]
    public void DelimiterEscapes(string option, string value, string input, string expected)
    {
        Assert.Equal(expected, Xargs(input, option, value));
    }

    [Fact]
    public void OpenTtyIsReportedAsUnsupported()
    {
        var (code, _, err) = Run("", ["-o", "vim"]);
        Assert.Equal(1, code);
        Assert.StartsWith("xargs: --open-tty is not supported on Windows", err);
    }
}
