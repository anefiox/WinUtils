namespace WinUtils.FindW;

internal static class HelpText
{
    public const string Version = """
        findw (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU find, written in C#.

        """;

    public const string Text = """
        Usage: findw [-H] [-L] [-P] [starting-point...] [expression]
        Search directory trees for files matching an expression.
        Example: findw src -name '*.cs' -mtime -1

        The default starting point is '.'; the default action is -print.
        Expressions combine with ( ), ! or -not, -a or -and (implied), -o or -or, and ','.

        Link handling:
          -P            never follow symlinks or junctions (default)
          -H            follow links named as starting points
          -L, -follow   follow all links

        Global options:
          -maxdepth N   descend at most N levels below the starting points
          -mindepth N   don't apply the expression above level N
          -depth        process a directory's contents before the directory
          -regextype T  regex syntax for -regex: posix-extended (default),
                        posix-basic, or dotnet

        Tests (N may be +N for more than N, -N for less than N, or N for exactly N):
          -name GLOB    base name matches GLOB (case-insensitive, like Windows)
          -iname GLOB   same as -name
          -path GLOB    whole path matches GLOB; '/' and '\' are interchangeable
          -ipath, -wholename, -iwholename   same as -path
          -regex RE     whole path matches RE;  -iregex RE  ignoring case
          -type C       file type: f (file), d (directory), l (symlink or junction);
                        several may be given as f,d
          -size N[ckMGwb]  size in units (rounded up): c bytes, k KiB, M MiB, G GiB,
                        w 2-byte words, b 512-byte blocks (default)
          -empty        empty file or directory
          -hidden       has the Windows Hidden attribute
          -mtime N      modified N days ago;  -mmin N  N minutes ago
          -atime N      accessed N days ago;  -amin N
          -ctime N      created N days ago;   -cmin N  (Windows has no change time)
          -newer FILE   modified more recently than FILE
          -newermt DATE modified after DATE, e.g. 2026-01-31 or "2026-01-31 14:00"
          -true, -false

        Actions:
          -print        print the path followed by a newline
          -print0       print the path followed by a NUL
          -printf FMT   print FMT; %p path, %f name, %h directory, %P path below
                        the starting point, %s size, %k size in KiB, %d depth,
                        %y type, %l link target, %t modified time, %TY %Tm %Td
                        %TH %TM %TS %TF %TT %T+ %T@ time fields (%A.. accessed,
                        %C.. created), \n \t \0 escapes
          -delete       delete the file or empty directory (implies -depth)
          -exec CMD ;   run CMD with {} replaced by the path; true if it exits 0
          -exec CMD {} +  run CMD once with many paths at a time
          -execdir ...  like -exec, but run in the file's directory with .\name
          -prune        don't descend into this directory
          -quit         stop immediately

        In PowerShell, quote the terminator: -exec cmd /c type {} ';'
        Programs (.exe) run directly; .bat/.cmd files and cmd built-ins such as
        echo, copy and del run through cmd /c.
        Exit status is 0 on success and 1 if any error occurred.

        """;
}
