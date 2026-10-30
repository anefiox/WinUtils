using System.Diagnostics;
using System.Text;
using WinUtils.Common.IO;
using WinUtils.Tail;

namespace Tail.Tests;

[CollectionDefinition("tail", DisableParallelization = true)]
public sealed class TailCollection;

[Collection("tail")]
public sealed class TailTests : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;
    private readonly string _root;

    public TailTests()
    {
        _root = Directory.CreateTempSubdirectory("tail-tests-").FullName;
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

    /// <summary>Collects output written from tail's thread while the test thread reads it.</summary>
    private sealed class Capture : Stream
    {
        private readonly MemoryStream _data = new();

        public string Text
        {
            get { lock (_data) return Encoding.UTF8.GetString(_data.ToArray()); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_data) _data.Write(buffer, offset, count);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static (int Code, string Out, string Err) Run(Stream stdin, params string[] args)
    {
        var outStream = new Capture();
        var errStream = new Capture();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = TailCommand.Run(args, stdin, stdout, stderr);
        }
        return (code, outStream.Text, errStream.Text);
    }

    private static string Tail(string stdin, params string[] args) =>
        Run(new PipeStream(Encoding.UTF8.GetBytes(stdin)), args).Out;

    private static readonly string Twenty = string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}\n"));

    [Theory]
    [InlineData(new string[0], "11\n12\n13\n14\n15\n16\n17\n18\n19\n20\n")]
    [InlineData(new[] { "-n", "2" }, "19\n20\n")]
    [InlineData(new[] { "-2" }, "19\n20\n")]
    [InlineData(new[] { "-n", "+19" }, "19\n20\n")]
    [InlineData(new[] { "-c", "6" }, "19\n20\n")]
    [InlineData(new[] { "-c", "+46" }, "19\n20\n")]
    public void Counts(string[] args, string expected)
    {
        // Pipes and files take different paths.
        Assert.Equal(expected, Tail(Twenty, args));
        File.WriteAllText("twenty.txt", Twenty);
        Assert.Equal(expected, Run(new MemoryStream(), [.. args, "twenty.txt"]).Out);
    }

    [Fact]
    public void LargePipeKeepsOnlyWhatItNeeds()
    {
        string big = string.Concat(Enumerable.Range(1, 100_000).Select(i => $"line {i}\n"));
        Assert.Equal("line 99999\nline 100000\n", Tail(big, "-n", "2"));
        Assert.Equal("line 3\n", Tail(big, "-n", "+3").Split('\n')[0] + "\n");
    }

    [Fact]
    public void LastLineWithoutNewlineCounts()
    {
        Assert.Equal("b\nc", Tail("a\nb\nc", "-n", "2"));
    }

    [Fact]
    public void Utf16FilesAreReadFromTheEnd()
    {
        File.WriteAllText("u.txt", "first\r\nਊsecond\r\nthird\r\n", Encoding.Unicode);
        Assert.Equal("ਊsecond\r\nthird\r\n", Run(new MemoryStream(), "-n", "2", "u.txt").Out);
        Assert.Equal("first\r\nਊsecond\r\nthird\r\n", Run(new MemoryStream(), "-n", "+1", "u.txt").Out);
    }

    [Fact]
    public void MultipleFilesGetHeaders()
    {
        File.WriteAllText("a.txt", "1\n2\n");
        File.WriteAllText("b.txt", "3\n");
        Assert.Equal("==> a.txt <==\n2\n\n==> b.txt <==\n3\n", Run(new MemoryStream(), "-n", "1", "a.txt", "b.txt").Out);
    }

    [Fact]
    public void ObsoleteNumberFormNeedsASingleFile()
    {
        var (code, _, err) = Run(new MemoryStream(), "-3", "a.txt", "b.txt");
        Assert.Equal(1, code);
        Assert.Equal("tail: option used in invalid context -- 3\n", err);
    }

    // ---- follow ----

    private static string WaitFor(Capture capture, string expected)
    {
        var watch = Stopwatch.StartNew();
        while (!capture.Text.Contains(expected) && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
        return capture.Text;
    }

    private sealed class Following : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task<int> _task;
        public readonly Capture Out = new(), Err = new();

        public Following(params string[] args)
        {
            _task = Task.Run(() =>
            {
                using var stdout = OutputSink.ForStream(Out);
                using var stderr = OutputSink.ForStream(Err);
                return TailCommand.Run([.. args, "-s", "0.02"], new MemoryStream(), stdout, stderr, _cancel.Token);
            });
        }

        public string WaitFor(string expected) => TailTests.WaitFor(Out, expected);
        public string WaitForError(string expected) => TailTests.WaitFor(Err, expected);

        public int Stop()
        {
            _cancel.Cancel();
            return _task.Wait(TimeSpan.FromSeconds(10)) ? _task.Result : throw new TimeoutException("tail -f didn't stop");
        }

        public void Dispose()
        {
            if (!_task.IsCompleted) Stop();
        }
    }

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void FollowPrintsAppendedData()
    {
        File.WriteAllText("log.txt", "old 1\nold 2\n");
        using var tail = new Following("-f", "-n", "1", "log.txt");
        tail.WaitFor("old 2\n");
        Append("log.txt", "new 1\n");
        Append("log.txt", "new 2\n");
        Assert.Equal("old 2\nnew 1\nnew 2\n", tail.WaitFor("new 2\n"));
        Assert.Equal(0, tail.Stop());
    }

    [Fact]
    public void FollowDoesNotBlockWritersOrDeletion()
    {
        File.WriteAllText("log.txt", "a\n");
        using var tail = new Following("-f", "log.txt");
        tail.WaitFor("a\n");
        // Writing, renaming and deleting must all work while tail holds the file open.
        Append("log.txt", "b\n");
        tail.WaitFor("b\n");
        File.Move("log.txt", "log.1");
        Append("log.1", "c\n");
        Assert.Equal("a\nb\nc\n", tail.WaitFor("c\n")); // -f follows the renamed file
        File.Delete("log.1");
        tail.Stop();
    }

    [Fact]
    public void FollowReportsTruncation()
    {
        File.WriteAllText("log.txt", "a long first line\n");
        using var tail = new Following("-f", "log.txt");
        tail.WaitFor("a long first line\n");
        File.WriteAllText("log.txt", "x\n");
        Assert.Contains("tail: log.txt: file truncated", tail.WaitForError("truncated"));
        Assert.EndsWith("x\n", tail.WaitFor("a long first line\nx\n"));
    }

    [Fact]
    public void FollowByNameSurvivesRotation()
    {
        File.WriteAllText("app.log", "before\n");
        using var tail = new Following("-F", "app.log");
        tail.WaitFor("before\n");

        File.Move("app.log", "app.log.1");
        File.WriteAllText("app.log", "after rotation\n");
        Assert.Equal("before\nafter rotation\n", tail.WaitFor("after rotation\n"));
        Assert.Contains("'app.log' has", tail.Err.Text);
    }

    [Fact]
    public void FollowByNameWaitsForAMissingFile()
    {
        using var tail = new Following("-F", "later.log");
        tail.WaitForError("cannot open 'later.log'");
        File.WriteAllText("later.log", "hello\n");
        Assert.Equal("hello\n", tail.WaitFor("hello\n"));
        Assert.Contains("'later.log' has appeared;  following new file", tail.WaitForError("appeared"));
    }

    [Fact]
    public void FollowSeveralFilesWithHeaders()
    {
        File.WriteAllText("a.log", "a1\n");
        File.WriteAllText("b.log", "b1\n");
        using var tail = new Following("-f", "a.log", "b.log");
        tail.WaitFor("b1\n");
        Append("a.log", "a2\n");
        Assert.Equal("==> a.log <==\na1\n\n==> b.log <==\nb1\n\n==> a.log <==\na2\n", tail.WaitFor("a2\n"));
    }

    [Fact]
    public void PidStopsFollowing()
    {
        File.WriteAllText("log.txt", "x\n");
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false })!;
        child.WaitForExit();
        var task = Task.Run(() => Run(new MemoryStream(), "-f", "--pid", child.Id.ToString(), "-s", "0.02", "log.txt"));
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "tail should stop once the process has exited");
        Assert.Equal("x\n", task.Result.Out);
    }
}
