# WinUtils

Native Windows ports of the best Unix command-line tools, written in C#. Each tool is a
single self-contained `.exe` (Native AOT, no .NET install needed) that behaves like its GNU
counterpart but understands Windows: CRLF line endings, UTF-16 files, `\` paths, and shells
that don't expand wildcards.

| Tool | Status |
|------|--------|
| `grep` | ✅ 0.1 |
| `findw` (find) | ✅ 0.1 |
| `sed` | ✅ 0.1 |
| `head` | ✅ 0.1 |
| `tail` | ✅ 0.1 |
| `wc` | ✅ 0.1 |
| `xargs` | ✅ 0.1 |

## Build

```powershell
dotnet test              # run the tests
.\build.ps1              # publish every tool (needs the MSVC build tools for AOT)
.\build.ps1 -Tool grep   # publish just one
```

Every tool ships on its own: `publish\<tool>\<tool>.exe` is a standalone exe with no
dependencies, and `publish\<tool>-<version>-win-x64.zip` is its release download. Install
one tool or all of them by putting the exes somewhere on your `PATH`.

## Layout

```
src/WinUtils.Common   shared by every tool
  CommandLine/          GNU getopt_long-style option parser
  IO/                   LineReader (encoding detection), OutputSink (Unicode console output),
                        Wildcards (expands *.txt, since cmd/PowerShell don't), FileIdentity,
                        ByteLines/ByteTextWriter (byte-exact line handling), StdStreams,
                        CommandLauncher (runs .exe directly, .bat/.cmd and built-ins via cmd)
  Text/                 Glob (fnmatch), PosixRegex (POSIX BRE/ERE to .NET regex), Sizes (10K, 5MiB)
src/Grep              grep
src/FindW             findw
src/Sed               sed
src/Head, src/Tail    head, tail
src/Wc                wc
src/Xargs             xargs
tests/<Tool>.Tests    xUnit tests for each tool
```

To add a tool: create `src/<Tool>` with `<AssemblyName>tool</AssemblyName>` and
`<PublishAot>true</PublishAot>`, reference `WinUtils.Common`, and add it to `WinUtils.slnx`.

## grep

Supports essentially all of GNU grep 3.x's options: `-E -F -G -P -e -f -i -v -w -x -z -c -l -L
-o -q -s -m -n -b -H -h -T -Z -A -B -C -NUM -r -R -a -I -U --include --exclude --exclude-dir
--exclude-from --color --label --line-buffered --binary-files --directories --group-separator`.
Run `grep --help` for the full list.

**Windows behaviour**

- **Line endings:** CRLF counts as the line terminator, so `foo$` matches `foo\r\n`. Lines are
  written back with their original endings. Use `-U` to treat `\r` as part of the line.
- **Encodings:** UTF-8 and UTF-16 files with a BOM are decoded, so files written by Windows
  PowerShell's `>` can be searched. UTF-16 matches are printed as UTF-8. Files that aren't valid
  UTF-8 pass through byte-for-byte.
- **Console output:** characters display correctly whatever the console code page is, because
  output goes through `WriteConsoleW`. Colors use ANSI sequences, enabled automatically.
- **Wildcards:** `grep foo *.log` expands `*.log` itself.
- **Paths:** recursion prints paths with `\`. Symlinks and junctions are followed only with `-R`.
- **Output file as input:** `grep -r x . > out.txt` skips `out.txt` instead of reading its own
  output (in shells that redirect directly, such as cmd.exe).

**Differences from GNU grep**

- `-P` uses .NET regular expressions, which are close to PCRE.
- In BRE/ERE, `\d` and `\D` match digits, as an extension. GNU treats `\d` as a literal `d`.
- Alternation is leftmost-first (.NET) instead of POSIX leftmost-longest. This only changes
  which text `-o` and `--color` show, never which lines match.
- Invalid UTF-8 doesn't make a file "binary". Only NUL bytes do.

## findw

GNU `find`, named `findw` because Windows already has an unrelated `find.exe` in System32.

```powershell
findw src -name '*.cs' -mtime -1
findw . -name node_modules -prune -o -name '*.js' -print
findw . -type f -size +10M -printf '%s\t%p\n'
findw logs -name '*.log' -mtime +30 -delete
findw . -name '*.tmp' -exec del {} ';'
```

It supports the expression language (`( ) ! -a -o ,`), `-name -iname -path -regex -type -size
-empty -mtime -mmin -atime -amin -ctime -cmin -newer -newermt`, `-maxdepth -mindepth -depth
-prune -quit`, and the actions `-print -print0 -printf -delete -exec ... ; -exec ... {} +
-execdir`. Run `findw --help` for the full list.

**Windows behaviour**

- **Case:** `-name` and `-path` ignore case, as Windows file names do. `-iname` and `-ipath` are
  accepted and behave the same.
- **Paths:** paths print with `\`, and in `-path` patterns `/` and `\` are interchangeable.
- **`-exec`:** `.exe` programs run directly. `.bat` and `.cmd` files, and cmd built-ins such as
  `del`, `copy` and `echo`, run through `cmd /c`. In PowerShell, quote the terminator: `';'`.
- **Links:** symlinks and junctions are treated as links (`-type l`) and are followed only with
  `-L` or `-H`. Following links can't loop forever: loops are detected.
- **`-hidden`:** a Windows extension that matches files with the Hidden attribute.

**Differences from GNU find**

- `-ctime`, `-cmin` and `%C` use the creation time, because Windows has no inode change time.
- `-regex` defaults to POSIX extended syntax rather than Emacs syntax. Use `-regextype` with
  `posix-basic` or `dotnet` to change it.
- Not yet supported: `-user`, `-group`, `-perm`, `-links`, `-inum`, `-ls`, `-fprint`, `-ok`.

## sed

GNU `sed`. Its output matches GNU sed 4.x byte for byte across a 276-case comparison, including
exit codes and error messages.

```powershell
sed 's/foo/bar/g' file.txt
sed -i.bak '/^#/d' config.ini
sed -n '/BEGIN/,/END/p' log.txt
sed -E 's/(\w+)@(\w+)/\2 at \1/' emails.txt
sed ':a;N;$!ba;s/\n/,/g' list.txt
```

It supports every command (`s y p P d D n N g G h H x b t T : = l q Q a i c r R w W z F { }`),
every address form (`N`, `$`, `/re/I`, `\%re%`, `first~step`, `addr,+N`, `addr,~N`, `0,/re/`),
`s` flags `g p N i m w`, the case conversions `\U \L \u \l \E`, and the options `-n -e -f -E -i
-s -z -u -l`. Run `sed --help` for the full list.

**Windows behaviour**

- **Line endings:** CRLF counts as the line terminator, so `s/$/!/` puts the `!` before the CR.
  Output keeps each file's line endings. Use `-b` to treat `\r` as part of the line.
- **In-place editing:** `-i` keeps the file's line endings, encoding (UTF-8, UTF-16, or any
  single-byte encoding) and byte-order mark. It writes a temp file beside the original and swaps
  it in.
- **Encodings:** UTF-16 input sent to the console or a pipe is printed as UTF-8.
- **Script files:** `-f` script files can have CRLF line endings.
- **Wildcards:** `sed -i s/a/b/ *.txt` expands `*.txt` itself.

**Differences from GNU sed**

- The `e` command and the `s///e` flag (run the pattern space as a shell command) aren't
  supported.
- `--posix`, `--debug`, `--sandbox` and `--follow-symlinks` are accepted and ignored.

## head and tail

GNU `head` and `tail`. Their output matches GNU's byte for byte across a 324-case comparison.

```powershell
head -n 20 file.txt          # first 20 lines
head -n -5 file.txt          # all but the last 5 lines
tail -n 50 app.log           # last 50 lines, read from the end of the file
tail -n +2 data.csv          # everything after the header line
tail -c 1M huge.bin          # last MiB
tail -f app.log              # follow appended data
tail -F app.log              # follow the name, surviving log rotation
tail -f build.log --pid 1234 # stop following when process 1234 exits
```

**Windows behaviour**

- **Speed:** `tail` reads files from the end, so `tail app.log` is instant even on a
  multi-GB file. `tail < file` gets the same speed, because redirected stdin is seekable.
- **Byte-exact output:** output keeps CRLF line endings and any encoding. UTF-16 files are
  counted in whole characters and printed as UTF-8.
- **Following:** `tail -f` opens files with full sharing, so it never blocks the program writing
  the log or a tool rotating or deleting it. `-F` notices when the file is renamed, replaced,
  truncated, deleted or recreated, and switches to the new file. Windows has no inotify, so
  files are checked every 0.25 s; change this with `-s`.

**Differences from GNU**

- `tail` checks for new data every 0.25 s by default, rather than GNU's 1 s.
- The obsolete `tail +N` form, a positional argument starting with `+`, isn't supported. Use
  `tail -n +N`.

## wc

GNU `wc`. Its output matches GNU's byte for byte across a 168-case comparison, including the
column widths.

```powershell
wc file.txt                          # lines, words, bytes
wc -l *.cs                           # line counts with a total
findw src -name '*.cs' -print0 | wc -l --files0-from=-
wc -L log.txt                        # longest line, in display columns
```

It supports `-l -w -c -m -L`, `--files0-from` and `--total=auto|always|only|never`.

**Windows behaviour**

- **Encodings:** UTF-8, UTF-16 (with a byte-order mark) and single-byte encodings are detected.
  `-m`, `-w` and `-L` count characters, not bytes, so `café` is 4 characters, `日本語` is 6
  columns wide, and an emoji is one character two columns wide. `-c` always counts raw bytes.
- **Line endings:** a CRLF line ending counts as one line.
- **Speed:** it's about 4× faster than GNU for word counts and the longest line, and 2× faster
  for line counts. `-c` alone reads only the file size.

## xargs

GNU `xargs`. Its output matches GNU's byte for byte across a 32-case comparison, apart from the
backslash rule below.

```powershell
findw . -name '*.tmp' -print0 | xargs -0 del
findw src -name '*.cs' -print0 | xargs -0 grep -l TODO
dir /b *.log | xargs -n 1 -P 4 gzip
type urls.txt | xargs -I {} curl -O {}
```

It supports `-0 -a -d -E -I -i -L -l -n -P -p -r -s -t -x`, `--process-slot-var` and
`--show-limits`, with GNU's exit statuses (123, 124, 125, 126, 127).

**Windows behaviour**

- **Running commands:** `.exe` programs run directly with properly quoted arguments. `.bat` and
  `.cmd` files, and cmd built-ins such as `del`, `copy` and `type`, run through `cmd /c`.
  Batches are sized to fit the command-line limit: 32,767 characters, or 8,191 through cmd.exe.
- **`echo`:** the default command, and `echo` itself, is a built-in POSIX-style echo, as on
  Linux, rather than cmd.exe's echo, which prints quotes literally.
- **Backslashes:** a backslash escapes only a blank or a quote (`a\ b`, `\"`). Anywhere else
  it's literal, so Windows paths such as `C:\dir\file` and `\\server\share` pass through
  intact. GNU xargs would strip every backslash.
- **Line endings:** CRLF input from PowerShell or cmd doesn't leave a stray `\r` on the last
  argument of each line.
- **Missing commands:** a command that isn't on PATH and isn't a cmd built-in is reported as
  not found (exit 127), without running cmd.exe.
- **`-p`:** answers are read from the console, because standard input holds the items.

**Differences from GNU**

- `-o`/`--open-tty` isn't supported.
- `--show-limits` reports Windows' limits.
