using System.Diagnostics;
using System.Text;

namespace WinUtils.Common.IO;

/// <summary>
/// Starts commands the way a Unix user expects. .exe and .com files run directly with their
/// arguments quoted properly. Batch files and cmd.exe built-ins (echo, del, copy, ...) aren't
/// programs CreateProcess can start, so they go through cmd /c.
/// </summary>
public static class CommandLauncher
{
    /// <summary>Longest command line CreateProcess accepts, less a little room for quoting.</summary>
    public const int MaxProcessCommandLine = 32_000;

    /// <summary>Longest command line cmd.exe accepts.</summary>
    public const int MaxShellCommandLine = 8_000;

    public static ProcessStartInfo StartInfo(IReadOnlyList<string> argv)
    {
        string? path = FindExecutable(argv[0]);
        if (path is not null && IsProgram(path))
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (string arg in argv.Skip(1)) psi.ArgumentList.Add(arg);
            return psi;
        }
        return new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            $"/d /s /c \"{ShellLine(path ?? argv[0], argv.Skip(1))}\"") { UseShellExecute = false };
    }

    private static readonly HashSet<string> ShellBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "assoc", "break", "call", "cd", "chdir", "cls", "color", "copy", "date", "del", "dir", "echo",
        "endlocal", "erase", "for", "ftype", "goto", "if", "md", "mkdir", "mklink", "move", "path",
        "pause", "popd", "prompt", "pushd", "rd", "rem", "ren", "rename", "rmdir", "set", "setlocal",
        "shift", "start", "time", "title", "type", "ver", "verify", "vol",
    };

    /// <summary>True if the command can be run: a program or script on PATH, or a cmd.exe built-in.</summary>
    public static bool Exists(string command) => FindExecutable(command) is not null || ShellBuiltins.Contains(command);

    /// <summary>True if the command runs through cmd /c, which has a much shorter line limit.</summary>
    public static bool UsesShell(string command) => FindExecutable(command) is not { } path || !IsProgram(path);

    /// <summary>
    /// Length of a command line, counted like GNU xargs (each argument plus a separator), plus
    /// the two quotes Windows needs around arguments that are empty or contain blanks or quotes.
    /// </summary>
    public static int CommandLineLength(IEnumerable<string> argv) => argv.Sum(ArgumentLength);

    public static int ArgumentLength(string arg) =>
        arg.Length + 1 + (arg.Length == 0 || arg.AsSpan().IndexOfAny(" \t\"") >= 0 ? 2 : 0);

    /// <summary>Finds a command the way cmd.exe does: the current directory, then PATH, trying PATHEXT.</summary>
    public static string? FindExecutable(string command)
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

    private static bool IsProgram(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".com";

    private static string ShellLine(string command, IEnumerable<string> args)
    {
        var line = new StringBuilder(QuoteForShell(command));
        foreach (string arg in args) line.Append(' ').Append(QuoteForShell(arg));
        return line.ToString();
    }

    private static string QuoteForShell(string arg) =>
        arg.Length == 0 || arg.AsSpan().IndexOfAny(' ', '\t') >= 0 ? $"\"{arg}\"" : arg;
}
