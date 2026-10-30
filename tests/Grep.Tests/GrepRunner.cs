using System.Text;
using WinUtils.Common.IO;
using WinUtils.Grep;

namespace Grep.Tests;

public sealed record GrepResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs grep in-process against a temporary directory.</summary>
public sealed class GrepRunner : IDisposable
{
    private readonly string _previousDirectory = Environment.CurrentDirectory;

    public GrepRunner()
    {
        Root = Directory.CreateTempSubdirectory("grep-tests-").FullName;
        Environment.CurrentDirectory = Root;
    }

    public string Root { get; }

    public GrepRunner File(string relativePath, string content, Encoding? encoding = null)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return this;
    }

    public GrepRunner Bytes(string relativePath, byte[] content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return this;
    }

    public GrepResult Pipe(string stdin, params string[] args) => Pipe(Encoding.UTF8.GetBytes(stdin), args);

    public GrepResult Pipe(byte[] stdin, params string[] args)
    {
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        int code;
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(errStream))
        {
            code = GrepCommand.Run(args, new MemoryStream(stdin), stdout, stderr);
        }
        return new GrepResult(code, Encoding.UTF8.GetString(outStream.ToArray()), Encoding.UTF8.GetString(errStream.ToArray()));
    }

    public GrepResult Run(params string[] args) => Pipe("", args);

    public byte[] RunBytes(params string[] args)
    {
        var outStream = new MemoryStream();
        using (var stdout = OutputSink.ForStream(outStream))
        using (var stderr = OutputSink.ForStream(new MemoryStream()))
        {
            GrepCommand.Run(args, new MemoryStream(), stdout, stderr);
        }
        return outStream.ToArray();
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _previousDirectory;
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Tests change the current directory, so they can't run in parallel.</summary>
[CollectionDefinition("grep", DisableParallelization = true)]
public sealed class GrepCollection;
