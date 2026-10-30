using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WinUtils.FindW;

/// <summary>-exec / -execdir, in both the one-file-per-command (";") and batched ("+") forms.</summary>
internal sealed class ExecAction(FindCommand find, List<string> template, bool batch, bool inDirectory)
{
    // CreateProcess allows 32767 chars; leave room for the command and quoting.
    private const int MaxBatchLength = 30_000;

    private readonly List<string> _pending = [];
    private int _pendingLength;
    private string? _pendingDirectory;

    public bool Eval(FileEntry e)
    {
        string? directory = null;
        string target = e.Path;
        if (inDirectory)
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(e.Path));
            target = @".\" + e.Name;
        }

        if (!batch)
            return Run(template.Select(arg => arg.Replace("{}", target)).ToList(), directory) == 0;

        if (_pending.Count > 0 && (_pendingLength + target.Length + 3 > MaxBatchLength || directory != _pendingDirectory))
            Flush();
        _pending.Add(target);
        _pendingLength += target.Length + 3;
        _pendingDirectory = directory;
        return true;
    }

    /// <summary>Runs the batched command for the files collected so far.</summary>
    public void Flush()
    {
        if (_pending.Count == 0) return;
        if (Run([.. template, .. _pending], _pendingDirectory) != 0) find.Failed = true;
        _pending.Clear();
        _pendingLength = 0;
    }

    private int Run(List<string> argv, string? directory)
    {
        find.Out.Flush(); // keep our output ahead of the child's
        var psi = StartInfo(argv);
        psi.UseShellExecute = false;
        if (directory is not null) psi.WorkingDirectory = directory;
        try
        {
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception e)
        {
            find.Error($"'{argv[0]}': {e.Message}");
            return 127;
        }
    }

    /// <summary>
    /// Runs .exe and .com files directly. Batch files and cmd.exe built-ins (echo, del, copy, ...)
    /// aren't programs CreateProcess can start, so they go through cmd /c.
    /// </summary>
    private static ProcessStartInfo StartInfo(List<string> argv)
    {
        string? path = FindExecutable(argv[0]);
        if (path is not null && Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".com")
        {
            var psi = new ProcessStartInfo(path);
            foreach (string arg in argv.Skip(1)) psi.ArgumentList.Add(arg);
            return psi;
        }
        var line = new StringBuilder(QuoteForCmd(path ?? argv[0]));
        foreach (string arg in argv.Skip(1)) line.Append(' ').Append(QuoteForCmd(arg));
        return new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", $"/d /s /c \"{line}\"");
    }

    private static string QuoteForCmd(string arg) =>
        arg.Length == 0 || arg.AsSpan().IndexOfAny(' ', '\t') >= 0 ? $"\"{arg}\"" : arg;

    private static string? FindExecutable(string command)
    {
        string[] extensions = Path.HasExtension(command)
            ? [""]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> directories = command.AsSpan().IndexOfAny('\\', '/') >= 0
            ? [""]
            : [Environment.CurrentDirectory, .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string dir in directories)
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Join(dir, command + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
