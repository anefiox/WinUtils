using WinUtils.Common.Text;
using System.Security;
using WinUtils.Common.IO;

namespace WinUtils.Grep;

public sealed class GrepCommand
{
    private const string UsageLine = "Usage: grep [OPTION]... PATTERNS [FILE]...";

    private static readonly EnumerationOptions DirectoryListing = new()
    {
        AttributesToSkip = 0, // like GNU grep, include hidden and system files
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly GrepOptions _o;
    private readonly Matcher _matcher;
    private readonly Stream _stdin;
    private readonly OutputSink _stdout;
    private readonly OutputSink _stderr;
    private readonly LinePrinter _printer;
    private readonly FileIdentity? _outputFile;
    private bool? _showNames;
    private bool _success;
    private bool _error;
    private bool _stop;

    private GrepCommand(GrepOptions options, Matcher matcher, Stream stdin, OutputSink stdout, OutputSink stderr, FileIdentity? outputFile)
    {
        _o = options;
        _matcher = matcher;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _outputFile = outputFile;
        _printer = new LinePrinter(stdout, options, UseColor(options, stdout) ? ColorScheme.FromEnvironment() : null);
    }

    /// <summary>Runs grep and returns its exit status: 0 if a line was selected, 1 if not, 2 on error.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr, FileIdentity? outputFile = null)
    {
        GrepOptions options;
        Matcher matcher;
        try
        {
            options = GrepArguments.Parse(args, stdin);
            if (options.ShowHelp)
            {
                stdout.Write(HelpText.Text);
                stdout.Flush();
                return 0;
            }
            if (options.ShowVersion)
            {
                stdout.Write(HelpText.Version);
                stdout.Flush();
                return 0;
            }
            matcher = Matcher.Create(options);
        }
        catch (UsageException e)
        {
            if (e.Message.Length > 0) stderr.Write($"grep: {e.Message}\n");
            stderr.Write($"{UsageLine}\nTry 'grep --help' for more information.\n");
            stderr.Flush();
            return 2;
        }
        catch (Exception e) when (e is FatalException or PatternException)
        {
            stderr.Write($"grep: {e.Message}\n");
            stderr.Flush();
            return 2;
        }

        var command = new GrepCommand(options, matcher, stdin, stdout, stderr, outputFile);
        try
        {
            command.Execute();
        }
        finally
        {
            stdout.Flush();
        }
        if (command._error && !(options.Quiet && command._success)) return 2;
        return command._success ? 0 : 1;
    }

    private static bool UseColor(GrepOptions o, OutputSink stdout) => o.Color switch
    {
        ColorWhen.Always => true,
        ColorWhen.Auto => stdout.IsConsole
            && Environment.GetEnvironmentVariable("TERM") != "dumb"
            && Environment.GetEnvironmentVariable("NO_COLOR") is null
            && stdout.TryEnableVirtualTerminal(),
        _ => false,
    };

    private void Execute()
    {
        if (_o.MaxCount == 0) return;

        var files = _o.Files;
        bool implicitDot = false;
        if (files.Count == 0)
        {
            implicitDot = _o.Recursive;
            files.Add(_o.Recursive ? "." : "-");
        }
        // Without -H/-h, names are shown for several operands, or once -r descends into a directory.
        _showNames = _o.WithFilename ?? (files.Count > 1 ? true : null);

        foreach (string file in files)
        {
            if (_stop) break;
            SearchOperand(file, implicitDot);
        }
    }

    private void SearchOperand(string path, bool implicitDot)
    {
        if (path == "-")
        {
            Search(_stdin, _o.Label ?? "(standard input)");
            return;
        }
        if (Directory.Exists(path))
        {
            switch (_o.Directories)
            {
                case DirectoryAction.Skip:
                    return;
                case DirectoryAction.Read:
                    Message($"{path}: Is a directory");
                    return;
            }
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (path != "." && IsExcludedDirectory(name)) return;
            _showNames ??= true;
            var visited = _o.FollowSymlinks ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) } : null;
            Walk(path, implicitDot ? null : path, visited);
            return;
        }
        if (!IsIncludedFile(path, commandLine: true)) return;
        SearchFile(path, path);
    }

    private void Walk(string directory, string? display, HashSet<string>? visited)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", DirectoryListing).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Message($"{display ?? "."}: {Describe(e)}");
            return;
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        foreach (var entry in entries)
        {
            if (_stop) return;
            string path = Path.Join(directory, entry.Name);
            string childDisplay = display is null ? entry.Name : Path.Join(display, entry.Name);
            // Symlinks and junctions are reparse points with a link target; other reparse points
            // (OneDrive placeholders, dedup) are ordinary files and folders.
            bool isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null;
            if (isLink && !_o.FollowSymlinks) continue; // -r follows only command-line links; -R follows all

            if (entry is DirectoryInfo dir)
            {
                if (IsExcludedDirectory(entry.Name)) continue;
                if (visited is not null && !visited.Add(RealPath(dir))) continue; // a link cycle
                Walk(path, childDisplay, visited);
            }
            else if (IsIncludedFile(entry.Name, commandLine: false))
            {
                SearchFile(path, childDisplay);
            }
        }
    }

    private static string RealPath(DirectoryInfo dir)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName);
        }
        catch (IOException)
        {
            return dir.FullName;
        }
    }

    private bool IsExcludedDirectory(string name) => _o.ExcludeDirs.Any(g => g.IsMatch(name));

    /// <summary>Applies --include/--exclude: the last matching filter wins; if none match, the file is
    /// searched unless the first filter is an --include.</summary>
    private bool IsIncludedFile(string path, bool commandLine)
    {
        if (_o.FileFilters.Count == 0) return true;
        string name = Path.GetFileName(path);
        for (int i = _o.FileFilters.Count - 1; i >= 0; i--)
        {
            var (glob, include) = _o.FileFilters[i];
            if (glob.IsMatch(name) || (commandLine && glob.IsMatch(path))) return include;
        }
        return !_o.FileFilters[0].Include;
    }

    private void SearchFile(string path, string display)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 0, // LineReader does its own buffering
                Options = FileOptions.SequentialScan,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            Message($"{display}: {Describe(e)}");
            return;
        }

        using (stream)
        {
            // "grep -r foo . > out.txt" would otherwise read its own output forever.
            if (_outputFile is not null && FileIdentity.Of(stream.SafeFileHandle) == _outputFile)
            {
                Message($"{display}: input file is also the output");
                return;
            }
            try
            {
                Search(stream, display);
            }
            catch (IOException e)
            {
                Message($"{display}: {Describe(e)}");
            }
        }
    }

    private void Search(Stream stream, string display)
    {
        bool showName = _showNames == true;
        long selected = new FileSearcher(_o, _matcher, _printer, display, showName).Search(stream);

        if (_o.Quiet)
        {
            if (selected > 0)
            {
                _success = true;
                _stop = true;
            }
            return;
        }

        char terminator = _o.NullAfterName ? '\0' : '\n';
        switch (_o.List)
        {
            case ListFiles.WithMatches when selected > 0:
            case ListFiles.WithoutMatch when selected == 0:
                _printer.FileName(display);
                _stdout.Write(terminator);
                _success = true;
                break;
            case ListFiles.None:
                if (_o.Count)
                {
                    if (showName)
                    {
                        _printer.FileName(display);
                        _stdout.Write(_o.NullAfterName ? '\0' : ':');
                    }
                    _stdout.Write($"{selected}\n");
                }
                if (selected > 0) _success = true;
                break;
        }

        if (_stdout.IsConsole || _o.LineBuffered) _stdout.Flush();
    }

    private void Message(string text)
    {
        _error = true;
        if (_o.NoMessages) return;
        _stdout.Flush(); // keep stdout and stderr in order on a shared console
        _stderr.Write($"grep: {text}\n");
        _stderr.Flush();
    }

    internal static string Describe(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException or SecurityException => "Permission denied",
        _ => e.Message,
    };
}
