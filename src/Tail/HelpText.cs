namespace WinUtils.Tail;

internal static class HelpText
{
    public const string Version = """
        tail (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU tail, written in C#.

        """;

    public const string Text = """
        Usage: tail [OPTION]... [FILE]...
        Print the last 10 lines of each FILE to standard output.
        With more than one FILE, precede each with a header giving the file name.

        With no FILE, or when FILE is -, read standard input.

          -c, --bytes=[+]NUM       output the last NUM bytes; or use -c +NUM to
                                     output starting with byte NUM of each file
          -f, --follow[={name|descriptor}]
                                   output appended data as the file grows;
                                     an absent option argument means 'descriptor'
          -F                       same as --follow=name --retry
          -n, --lines=[+]NUM       output the last NUM lines, instead of the last 10;
                                     or use -n +NUM to skip NUM-1 lines at the start
          -NUM                     same as -n NUM
              --pid=PID            with -f, terminate after process ID, PID dies
          -q, --quiet, --silent    never output headers giving file names
              --retry              keep trying to open a file if it is inaccessible
          -s, --sleep-interval=N   with -f, check for new data every N seconds
                                     (default 0.25)
          -v, --verbose            always output headers giving file names
          -z, --zero-terminated    line delimiter is NUL, not newline
              --help               display this help and exit
              --version            output version information and exit

        NUM may have a multiplier suffix: b 512, kB 1000, K 1024, MB 1000*1000,
        M 1024*1024, GB 1000*1000*1000, G 1024*1024*1024, and so on for T, P, E.
        Binary prefixes can be used, too: KiB=K, MiB=M, and so on.

        With --follow (-f), tail follows the file even if it is renamed. Use
        --follow=name (or -F) to follow the name instead, which survives log
        rotation: if the file is replaced, deleted or truncated, tail reopens it.
        Followed files are opened with full sharing, so writers and rotation are
        never blocked. Stop with Ctrl+C.

        Large files are read from the end, so tail is fast on any size of file.
        Output is byte for byte, so CRLF line endings are kept. UTF-16 files are
        printed as UTF-8. Wildcards (* and ?) in FILE are expanded.

        """;
}
