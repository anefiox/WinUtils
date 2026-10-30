namespace WinUtils.Sed;

internal static class HelpText
{
    public const string Version = """
        sed (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU sed, written in C#.

        """;

    public const string Text = """
        Usage: sed [OPTION]... {script-only-if-no-other-script} [input-file]...

          -n, --quiet, --silent
                         suppress automatic printing of pattern space
          -e script, --expression=script
                         add the script to the commands to be executed
          -f script-file, --file=script-file
                         add the contents of script-file to the commands to be executed
          -i[SUFFIX], --in-place[=SUFFIX]
                         edit files in place (makes backup if SUFFIX supplied;
                         a '*' in SUFFIX is replaced by the file name)
          -l N, --line-length=N
                         specify the desired line-wrap length for the `l' command
          -E, -r, --regexp-extended
                         use extended regular expressions in the script
          -s, --separate
                         consider files as separate rather than as a single
                         continuous long stream
          -u, --unbuffered
                         flush output after every line
          -z, --null-data
                         separate lines by NUL characters
          -b, --binary
                         keep CR characters (don't treat CRLF as a line ending)
              --help     display this help and exit
              --version  output version information and exit

        If no -e, --expression, -f, or --file option is given, then the first
        non-option argument is taken as the sed script to interpret. All
        remaining arguments are names of input files; if no input files are
        specified, then the standard input is read. Wildcards (* and ?) in file
        names are expanded.

        Commands: s y p P d D n N g G h H x b t T : = l q Q a i c r R w W z F { }
        Addresses: N, $, /regex/, \%regex%, first~step, addr,+N, addr,~N, 0,/regex/
        (regex addresses take I and M flags). s/// flags: g p N i m w FILE.
        Replacements support & \1..\9 \n \t and the case conversions \U \L \u \l \E.

        CRLF line endings, UTF-8 and UTF-16 files are handled; -i keeps a file's
        line endings, encoding and byte-order mark.

        """;
}
