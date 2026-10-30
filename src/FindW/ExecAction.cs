using System.ComponentModel;
using System.Diagnostics;
using WinUtils.Common.IO;

namespace WinUtils.FindW;

/// <summary>-exec / -execdir, in both the one-file-per-command (";") and batched ("+") forms.</summary>
internal sealed class ExecAction(FindCommand find, List<string> template, bool batch, bool inDirectory)
{
    // Batches must fit on one command line, which is much shorter when cmd.exe runs it.
    private readonly int _maxBatchLength =
        (CommandLauncher.UsesShell(template[0]) ? CommandLauncher.MaxShellCommandLine : CommandLauncher.MaxProcessCommandLine)
        - CommandLauncher.CommandLineLength(template);

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

        int length = CommandLauncher.ArgumentLength(target);
        if (_pending.Count > 0 && (_pendingLength + length > _maxBatchLength || directory != _pendingDirectory))
            Flush();
        _pending.Add(target);
        _pendingLength += length;
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
        var psi = CommandLauncher.StartInfo(argv);
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

}
