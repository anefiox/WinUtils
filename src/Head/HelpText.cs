namespace WinUtils.Head;

internal static class HelpText
{
    public const string Version = """
        head (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU head, written in C#.

        """;

    public const string Text = """
        Usage: head [OPTION]... [FILE]...
        Print the first 10 lines of each FILE to standard output.
        With more than one FILE, precede each with a header giving the file name.

        With no FILE, or when FILE is -, read standard input.

          -c, --bytes=[-]NUM       print the first NUM bytes of each file;
                                     with the leading '-', print all but the last
                                     NUM bytes of each file
          -n, --lines=[-]NUM       print the first NUM lines instead of the first 10;
                                     with the leading '-', print all but the last
                                     NUM lines of each file
          -NUM                     same as -n NUM
          -q, --quiet, --silent    never print headers giving file names
          -v, --verbose            always print headers giving file names
          -z, --zero-terminated    line delimiter is NUL, not newline
              --help               display this help and exit
              --version            output version information and exit

        NUM may have a multiplier suffix: b 512, kB 1000, K 1024, MB 1000*1000,
        M 1024*1024, GB 1000*1000*1000, G 1024*1024*1024, and so on for T, P, E.
        Binary prefixes can be used, too: KiB=K, MiB=M, and so on.

        Output is byte for byte, so CRLF line endings are kept. UTF-16 files are
        printed as UTF-8. Wildcards (* and ?) in FILE are expanded.

        """;
}
