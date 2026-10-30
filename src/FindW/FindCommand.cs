using System.Security;
using WinUtils.Common.IO;

namespace WinUtils.FindW;

public enum LinkMode { Never, CommandLine, All }

public enum RegexType { Extended, Basic, DotNet }

/// <summary>Walks the starting points and evaluates the expression for every file found.</summary>
public sealed class FindCommand
{
    private static readonly EnumerationOptions DirectoryListing = new()
    {
        AttributesToSkip = 0, // include hidden and system files
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly OutputSink _stderr;
    private Expr _root = Primary.True;

    private FindCommand(OutputSink stdout, OutputSink stderr)
    {
        Out = stdout;
        _stderr = stderr;
    }

    internal OutputSink Out { get; }
    internal DateTime StartTime { get; } = DateTime.UtcNow;
    internal int MaxDepth { get; set; } = int.MaxValue;
    internal int MinDepth { get; set; }
    internal bool DepthFirst { get; set; }
    internal LinkMode Follow { get; set; } = LinkMode.Never;
    internal RegexType RegexType { get; set; } = RegexType.Extended;
    internal List<ExecAction> Batches { get; } = [];
    /// <summary>Set by -prune: don't descend into the directory being evaluated.</summary>
    internal bool PruneCurrent { get; set; }
    /// <summary>Set by -quit: stop the walk.</summary>
    internal bool Quit { get; set; }
    /// <summary>Something went wrong; the exit status will be 1.</summary>
    internal bool Failed { get; set; }

    /// <summary>Runs findw and returns its exit status: 0 on success, 1 if anything failed.</summary>
    public static int Run(IReadOnlyList<string> args, OutputSink stdout, OutputSink stderr)
    {
        var find = new FindCommand(stdout, stderr);
        try
        {
            return find.Execute(args);
        }
        catch (FindException e)
        {
            stderr.Write($"findw: {e.Message}\n");
            stderr.Flush();
            return 1;
        }
        finally
        {
            stdout.Flush();
        }
    }

    private int Execute(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] is "--help" or "--version")
        {
            Out.Write(args[0] == "--help" ? HelpText.Text : HelpText.Version);
            return 0;
        }

        int i = 0;
        for (; i < args.Count && args[i] is "-H" or "-L" or "-P"; i++)
        {
            Follow = args[i] switch { "-H" => LinkMode.CommandLine, "-L" => LinkMode.All, _ => LinkMode.Never };
        }

        // Starting points run up to the first argument that begins an expression.
        var paths = new List<string>();
        for (; i < args.Count && !StartsExpression(args[i]); i++) paths.AddRange(Wildcards.Expand(args[i]));
        if (paths.Count == 0) paths.Add(".");

        var parser = new ExpressionParser(this, args.Skip(i).ToList());
        var expr = parser.Parse();
        var print = new Primary(e =>
        {
            Print(e.Path, '\n');
            return true;
        });
        _root = expr is null ? print : parser.HasAction ? expr : new AndExpr(expr, print);

        foreach (string path in paths)
        {
            if (Quit) break;
            VisitStartPoint(path);
        }
        foreach (var batch in Batches) batch.Flush();
        return Failed ? 1 : 0;
    }

    private static bool StartsExpression(string arg) =>
        (arg.Length > 1 && arg[0] == '-') || arg is "(" or "!" or ",";

    private void VisitStartPoint(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null)
        {
            Error($"'{path}': No such file or directory");
            return;
        }
        var ancestors = Follow == LinkMode.Never ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(info, path, path, 0, ancestors);
    }

    private void Visit(FileSystemInfo info, string path, string startPoint, int depth, HashSet<string>? ancestors)
    {
        bool follow = Follow == LinkMode.All || (Follow == LinkMode.CommandLine && depth == 0);
        var entry = new FileEntry(info, path, startPoint, depth, follow);
        bool descend = entry.IsDirectory && depth < MaxDepth;

        // Following links can lead back to a directory we're already inside.
        string? real = null;
        if (descend && ancestors is not null)
        {
            real = Path.TrimEndingDirectorySeparator(entry.Stat.FullName);
            if (!ancestors.Add(real))
            {
                Error($"File system loop detected; '{path}' is part of the same file system loop.");
                descend = false;
                real = null;
            }
        }

        if (!DepthFirst && depth >= MinDepth)
        {
            PruneCurrent = false;
            _root.Eval(entry);
            if (PruneCurrent) descend = false;
        }

        if (descend && !Quit)
        {
            foreach (var child in List(entry))
            {
                Visit(child, Path.Join(path, child.Name), startPoint, depth + 1, ancestors);
                if (Quit) break;
            }
        }
        if (real is not null) ancestors!.Remove(real);

        if (DepthFirst && depth >= MinDepth && !Quit) _root.Eval(entry);
        if (Out.IsConsole) Out.Flush();
    }

    private List<FileSystemInfo> List(FileEntry dir)
    {
        try
        {
            var entries = new DirectoryInfo(dir.Info.FullName).EnumerateFileSystemInfos("*", DirectoryListing).ToList();
            entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Error($"'{dir.Path}': {Describe(e)}");
            return [];
        }
    }

    internal void Print(string path, char terminator)
    {
        Out.Write(path);
        Out.Write(terminator);
    }

    internal bool Delete(FileEntry e)
    {
        if (e.Depth == 0 && e.Path == ".") return true; // GNU find never deletes "."
        try
        {
            if (e.Info is DirectoryInfo) Directory.Delete(e.Info.FullName); // fails unless empty
            else
            {
                if (e.Info.Attributes.HasFlag(FileAttributes.ReadOnly))
                    e.Info.Attributes &= ~FileAttributes.ReadOnly;
                e.Info.Delete();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Error($"cannot delete '{e.Path}': {Describe(ex)}");
            return false;
        }
    }

    internal void Error(string message)
    {
        Failed = true;
        Out.Flush(); // keep stdout and stderr in order on a shared console
        _stderr.Write($"findw: {message}\n");
        _stderr.Flush();
    }

    private static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException or SecurityException => "Permission denied",
        _ => e.Message,
    };
}
