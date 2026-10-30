using System.Text;
using WinUtils.Common.IO;
using WinUtils.FindW;

namespace FindW.Tests;

/// <summary>Tests change the current directory, so they can't run in parallel.</summary>
[CollectionDefinition("findw", DisableParallelization = true)]
public sealed class FindCollection;

[Collection("findw")]
public sealed class FindTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public FindTests()
    {
        _root = Directory.CreateTempSubdirectory("findw-tests-").FullName;
        Environment.CurrentDirectory = _root;
        File("a.txt", "hello");
        File("b.cs", "");
        File(@"src\Main.cs", "class Main {}");
        File(@"src\util\Helper.cs", "x");
        File(@"src\util\notes.TXT", "notes");
        File(@"node_modules\pkg\index.js", "js");
        Directory.CreateDirectory("empty");
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // .NET's recursive delete fails on junctions; rmdir removes them without following.
            System.Diagnostics.Process.Start("cmd.exe", ["/c", "rmdir", "/s", "/q", _root])!.WaitForExit();
        }
    }

    private static void File(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        System.IO.File.WriteAllText(path, content);
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = FindCommand.Run(args, stdout, stderr);
        }
        return (code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    private static string[] Lines(params string[] args) =>
        Run(args).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void ListsEverythingByDefault()
    {
        Assert.Equal(
            [".", @".\a.txt", @".\b.cs", @".\empty", @".\node_modules", @".\node_modules\pkg", @".\node_modules\pkg\index.js",
             @".\src", @".\src\Main.cs", @".\src\util", @".\src\util\Helper.cs", @".\src\util\notes.TXT"],
            Lines());
    }

    [Fact]
    public void NameMatchesBaseNamesCaseInsensitively()
    {
        Assert.Equal([@"src\util\notes.TXT"], Lines("src", "-name", "*.txt"));
        Assert.Equal([@".\b.cs", @".\src\Main.cs", @".\src\util\Helper.cs"], Lines(".", "-name", "*.cs"));
    }

    [Fact]
    public void PathAcceptsEitherSlash()
    {
        Assert.Equal([@".\src\util\Helper.cs"], Lines("-path", "*/util/*.cs"));
        Assert.Equal([@".\src\util\Helper.cs"], Lines("-path", @"*\util\*.cs"));
    }

    [Fact]
    public void TypeFilters()
    {
        Assert.Equal([".", @".\empty", @".\node_modules", @".\node_modules\pkg", @".\src", @".\src\util"], Lines("-type", "d"));
        Assert.Equal(6, Lines("-type", "f").Length);
        Assert.Equal(12, Lines("-type", "f,d").Length);
    }

    [Fact]
    public void OperatorsAndParentheses()
    {
        Assert.Equal([@".\a.txt", @".\b.cs"], Lines("-maxdepth", "1", "-type", "f", "(", "-name", "*.txt", "-o", "-name", "*.cs", ")"));
        Assert.Equal([@".\a.txt"], Lines("-maxdepth", "1", "-type", "f", "!", "-name", "*.cs"));
        Assert.Equal([@".\a.txt"], Lines("-maxdepth", "1", "-type", "f", "-not", "-name", "b*"));
    }

    [Fact]
    public void PruneSkipsADirectory()
    {
        var lines = Lines("-name", "node_modules", "-prune", "-o", "-name", "*.js", "-print");
        Assert.Empty(lines);
        Assert.Equal([@".\node_modules\pkg\index.js"], Lines("-name", "*.js"));
    }

    [Fact]
    public void DepthLimits()
    {
        Assert.Equal([".", @".\a.txt", @".\b.cs", @".\empty", @".\node_modules", @".\src"], Lines("-maxdepth", "1"));
        Assert.Equal([@".\src\util\Helper.cs", @".\src\util\notes.TXT"], Lines("-mindepth", "3", "-path", "*src*"));
    }

    [Fact]
    public void DepthFirstListsContentsBeforeTheirDirectory()
    {
        var lines = Lines("src", "-depth");
        Assert.Equal("src", lines[^1]);
        Assert.True(Array.IndexOf(lines, @"src\util\Helper.cs") < Array.IndexOf(lines, @"src\util"));
    }

    [Fact]
    public void Size()
    {
        Assert.Equal([@".\a.txt"], Lines("-maxdepth", "1", "-size", "5c"));
        Assert.Equal([@".\a.txt"], Lines("-maxdepth", "1", "-size", "+4c"));
        Assert.Equal([@".\b.cs"], Lines("-maxdepth", "1", "-type", "f", "-size", "-1c"));
        // Rounded up to whole units: a 5-byte file is 1k, so it isn't "less than 1k".
        Assert.Equal([@".\b.cs"], Lines("-maxdepth", "1", "-type", "f", "-size", "-1k"));
    }

    [Fact]
    public void Empty()
    {
        Assert.Equal([@".\b.cs", @".\empty"], Lines("-empty"));
    }

    [Fact]
    public void ModificationTimes()
    {
        System.IO.File.SetLastWriteTime("a.txt", DateTime.Now.AddDays(-10));
        System.IO.File.SetLastWriteTime("b.cs", DateTime.Now.AddMinutes(-30));
        Assert.Equal([@".\a.txt"], Lines("-maxdepth", "1", "-type", "f", "-mtime", "+7"));
        Assert.Equal([@".\b.cs"], Lines("-maxdepth", "1", "-type", "f", "-mmin", "-60", "-mmin", "+10"));
        Assert.Equal([@".\b.cs"], Lines("-maxdepth", "1", "-type", "f", "-newer", "a.txt"));
        Assert.Equal([@".\b.cs"], Lines("-maxdepth", "1", "-type", "f", "-newermt", DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm")));
    }

    [Fact]
    public void Regex()
    {
        Assert.Equal([@".\src\Main.cs"], Lines("-regex", @".*src\\[A-Z][a-z]+\.cs"));
        Assert.Equal([@".\src\Main.cs", @".\src\util\Helper.cs"], Lines("-regex", @".*src.*\.cs"));
        Assert.Equal([@".\src\Main.cs"], Lines("-regextype", "dotnet", "-regex", @".*\\M\w+\.cs"));
    }

    [Fact]
    public void Print0AndPrintf()
    {
        Assert.Equal(".\\a.txt\0", Run("-maxdepth", "1", "-name", "a.txt", "-print0").Out);
        Assert.Equal("a.txt|.|5|1|f|a.txt\n", Run("-maxdepth", "1", "-name", "a.txt", "-printf", @"%f|%h|%s|%d|%y|%P\n").Out);
        Assert.Equal(@"util\Helper.cs" + "\n", Run("src", "-name", "Helper.cs", "-printf", @"%P\n").Out);
    }

    [Fact]
    public void PrintfTimeFields()
    {
        System.IO.File.SetLastWriteTime("a.txt", new DateTime(2024, 3, 9, 14, 5, 7));
        Assert.Equal("2024-03-09 14:05:07\n", Run("-maxdepth", "1", "-name", "a.txt", "-printf", @"%TF %TT\n").Out);
    }

    [Fact]
    public void QuitStopsAfterTheFirstMatch()
    {
        Assert.Equal([@".\b.cs"], Lines("-name", "*.cs", "-print", "-quit"));
    }

    [Fact]
    public void ExplicitActionSuppressesImplicitPrint()
    {
        Assert.Equal("", Run("-name", "*.cs", "-false", "-print").Out);
    }

    [Fact]
    public void CommaEvaluatesBoth()
    {
        Assert.Equal(["A", "B"], Lines("-maxdepth", "0", "-printf", @"A\n", ",", "-printf", @"B\n"));
    }

    [Fact]
    public void Delete()
    {
        Assert.Equal(0, Run("node_modules", "-delete").Code);
        Assert.False(Directory.Exists("node_modules"));
        System.IO.File.SetAttributes("a.txt", FileAttributes.ReadOnly);
        Run("-name", "a.txt", "-delete");
        Assert.False(System.IO.File.Exists("a.txt"));
    }

    [Fact]
    public void ExecRunsCommandsAndActsAsATest()
    {
        Run("-name", "*.txt", "-exec", "cmd", "/c", "copy {} {}.bak >nul", ";");
        Assert.True(System.IO.File.Exists("a.txt.bak"));
        Assert.True(System.IO.File.Exists(@"src\util\notes.TXT.bak"));

        Assert.Equal([@".\a.txt"], Lines("-maxdepth", "1", "-name", "a.txt", "-exec", "cmd", "/c", "exit 0", ";", "-print"));
        Assert.Empty(Lines("-maxdepth", "1", "-name", "a.txt", "-exec", "cmd", "/c", "exit 1", ";", "-print"));
    }

    [Fact]
    public void ExecBatchesWithPlus()
    {
        // "copy /b a+b+c" can't be built this way, so count the batch through a .cmd file instead.
        System.IO.File.WriteAllText("count.cmd", "@echo %*> args.out\r\n");
        Run("-maxdepth", "1", "-name", "*.*", "-type", "f", "!", "-name", "count.cmd", "-exec", @".\count.cmd", "{}", "+");
        Assert.Equal(@".\a.txt .\b.cs", System.IO.File.ReadAllText("args.out").Trim());
    }

    [Fact]
    public void BuiltinsRunThroughCmd()
    {
        Run("-maxdepth", "1", "-name", "b.cs", "-exec", "del", "{}", ";");
        Assert.False(System.IO.File.Exists("b.cs"));
    }

    [Fact]
    public void ExecdirRunsInTheFilesDirectory()
    {
        Run("src", "-name", "Helper.cs", "-execdir", "cmd", "/c", "echo {}> where.out", ";");
        Assert.Equal(@".\Helper.cs", System.IO.File.ReadAllText(@"src\util\where.out").Trim());
    }

    [Fact]
    public void MissingStartingPointIsAnError()
    {
        var (code, output, err) = Run("nope", "a.txt");
        Assert.Equal(1, code);
        Assert.Equal("a.txt\n", output);
        Assert.Equal("findw: 'nope': No such file or directory\n", err);
    }

    [Theory]
    [InlineData(new[] { "-bogus" }, "findw: unknown predicate '-bogus'\n")]
    [InlineData(new[] { "-name" }, "findw: missing argument to '-name'\n")]
    [InlineData(new[] { "-exec", "echo" }, "findw: missing argument to '-exec'\n")]
    [InlineData(new[] { "(", "-true" }, "findw: invalid expression; I was expecting to find a ')' somewhere but did not see one.\n")]
    [InlineData(new[] { "-true", ")" }, "findw: invalid expression; you have too many ')'\n")]
    [InlineData(new[] { "-type", "x" }, "findw: Unknown argument to -type: x\n")]
    [InlineData(new[] { "-mtime", "abc" }, "findw: invalid argument 'abc' to '-mtime'\n")]
    [InlineData(new[] { "-true", "src" }, "findw: paths must precede expression: 'src'\n")]
    public void ExpressionErrors(string[] args, string message)
    {
        var (code, _, err) = Run(args);
        Assert.Equal(1, code);
        Assert.Equal(message, err);
    }

    [Fact]
    public void WildcardStartingPoints()
    {
        Assert.Equal(["a.txt"], Lines("*.txt"));
    }

    [Fact]
    public void JunctionsAreNotFollowedUnlessAsked()
    {
        var link = Directory.CreateSymbolicLinkSafe("link", Path.Combine(_root, "src"));
        if (link is null) return; // creating links needs Developer Mode or admin rights

        Assert.Equal([@".\link"], Lines("-name", "link"));
        Assert.Equal("l\n", Run("-name", "link", "-printf", @"%y\n").Out);
        Assert.Contains(@".\link\Main.cs", Lines("-L", "-name", "Main.cs"));
        Assert.DoesNotContain(@".\link\Main.cs", Lines("-name", "Main.cs"));
    }

    [Fact]
    public void LinkLoopsAreDetected()
    {
        var link = Directory.CreateSymbolicLinkSafe(@"src\loop", Path.Combine(_root, "src"));
        if (link is null) return;
        var (code, _, err) = Run("-L", "src");
        Assert.Equal(1, code);
        Assert.Contains("File system loop detected", err);
    }
}

internal static class DirectoryExtensions
{
    extension(Directory)
    {
        /// <summary>Creates a junction, which unlike a symlink needs no special rights.</summary>
        public static FileSystemInfo? CreateSymbolicLinkSafe(string path, string target)
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", path, target },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            mklink.WaitForExit();
            return mklink.ExitCode == 0 ? new DirectoryInfo(path) : null;
        }
    }
}
