using WinUtils.Common.Text;

namespace WinUtils.Grep;

public enum PatternSyntax { Basic, Extended, Fixed, Perl }

public enum BinaryFiles { Binary, Text, WithoutMatch }

public enum DirectoryAction { Read, Skip, Recurse }

public enum ListFiles { None, WithMatches, WithoutMatch }

public enum ColorWhen { Never, Auto, Always }

public sealed class GrepOptions
{
    public PatternSyntax Syntax { get; set; } = PatternSyntax.Basic;
    public List<string> Patterns { get; } = [];
    public bool IgnoreCase { get; set; }
    public bool InvertMatch { get; set; }
    public bool WordRegexp { get; set; }
    public bool LineRegexp { get; set; }
    public bool NullData { get; set; }

    public bool Count { get; set; }
    public ColorWhen Color { get; set; } = ColorWhen.Never;
    public ListFiles List { get; set; }
    /// <summary>Stop after this many selected lines; negative means no limit.</summary>
    public long MaxCount { get; set; } = -1;
    public bool OnlyMatching { get; set; }
    public bool Quiet { get; set; }
    public bool NoMessages { get; set; }

    public bool ByteOffset { get; set; }
    /// <summary>Explicit -H/-h; null means "decide from the number of files".</summary>
    public bool? WithFilename { get; set; }
    public string? Label { get; set; }
    public bool LineNumber { get; set; }
    public bool InitialTab { get; set; }
    public bool NullAfterName { get; set; }
    public bool LineBuffered { get; set; }

    public int Before { get; set; }
    public int After { get; set; }
    /// <summary>Printed between non-adjacent context groups; null disables it.</summary>
    public string? GroupSeparator { get; set; } = "--";

    public BinaryFiles Binary { get; set; } = BinaryFiles.Binary;
    /// <summary>-U: keep '\r' as part of the line instead of treating CRLF as the terminator.</summary>
    public bool KeepCarriageReturn { get; set; }
    public DirectoryAction Directories { get; set; } = DirectoryAction.Read;
    public bool SkipDevices { get; set; }
    public bool FollowSymlinks { get; set; }
    /// <summary>--include/--exclude in command-line order; the last matching filter wins.</summary>
    public List<(Glob Glob, bool Include)> FileFilters { get; } = [];
    public List<Glob> ExcludeDirs { get; } = [];

    public List<string> Files { get; } = [];
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }

    public bool Recursive => Directories == DirectoryAction.Recurse;
}
