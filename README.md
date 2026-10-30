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
                        Wildcards (expands *.txt, since cmd/PowerShell don't), FileIdentity
  Text/                 Glob (fnmatch), PosixRegex (POSIX BRE/ERE to .NET regex)
src/Grep              grep
src/FindW             findw
src/Sed               sed
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
