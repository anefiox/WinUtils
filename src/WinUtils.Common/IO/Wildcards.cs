using WinUtils.Common.Text;

namespace WinUtils.Common.IO;

/// <summary>
/// Expands <c>*</c> and <c>?</c> in file operands. Unix shells do this before a program runs;
/// cmd.exe and PowerShell do not, so every tool in the suite expands its own file operands.
/// Neither character is legal in a Windows file name, so expansion never misreads a real path.
/// </summary>
public static class Wildcards
{
    private static readonly EnumerationOptions ListAll = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    public static bool HasWildcards(string path) => path.AsSpan().IndexOfAny('*', '?') >= 0;

    /// <summary>Expands each operand; operands that match nothing are kept as-is so the caller reports them.</summary>
    public static List<string> ExpandAll(IEnumerable<string> operands)
    {
        var result = new List<string>();
        foreach (string operand in operands) result.AddRange(Expand(operand));
        return result;
    }

    public static IReadOnlyList<string> Expand(string pattern)
    {
        if (!HasWildcards(pattern)) return [pattern];

        string root = Path.GetPathRoot(pattern) ?? "";
        char separator = pattern.Contains('\\') || !pattern.Contains('/') ? '\\' : '/';
        string[] segments = pattern[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        List<string> current = [root];
        for (int s = 0; s < segments.Length && current.Count > 0; s++)
        {
            string segment = segments[s];
            bool last = s == segments.Length - 1;
            var next = new List<string>();
            foreach (string prefix in current)
            {
                if (!HasWildcards(segment))
                {
                    next.Add(Join(prefix, segment, separator));
                    continue;
                }
                next.AddRange(Matches(prefix, segment, directoriesOnly: !last).Select(name => Join(prefix, name, separator)));
            }
            current = next;
        }
        return current.Count == 0 ? [pattern] : current;
    }

    private static IEnumerable<string> Matches(string prefix, string segment, bool directoriesOnly)
    {
        string dir = prefix.Length == 0 ? "." : prefix;
        if (!Directory.Exists(dir)) return [];
        // Only * and ? are wildcards here: '[' and ']' are legal in Windows file names.
        var glob = new Glob(segment.Replace("[", @"\["));
        bool showHidden = segment.StartsWith('.');
        try
        {
            var entries = directoriesOnly
                ? new DirectoryInfo(dir).EnumerateDirectories("*", ListAll).Cast<FileSystemInfo>()
                : new DirectoryInfo(dir).EnumerateFileSystemInfos("*", ListAll);
            return entries
                .Select(e => e.Name)
                .Where(name => (showHidden || !name.StartsWith('.')) && glob.IsMatch(name))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Join(string prefix, string name, char separator) =>
        prefix.Length == 0 ? name
        : prefix[^1] is '\\' or '/' or ':' ? prefix + name
        : prefix + separator + name;
}
