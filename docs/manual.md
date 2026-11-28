# Glacier.Grep User Manual

This manual documents both the embedded C# library (`Glacier.Grep`) and the CLI / MCP executable (`Glacier.Grep.Host`).

---

## 1. CLI Usage (`Glacier.Grep.Host`)

Syntax:

```bash
Glacier.Grep.Host <query> [options]
Glacier.Grep.Host --mcp
```

When invoked with **no arguments** or with `--mcp`, `Glacier.Grep.Host` starts in stdio JSON-RPC MCP server mode.

### Supported Flags

| Short | Long | Value | Default | Description |
| :--- | :--- | :--- | :--- | :--- |
| `-d` | `--dir` | `<path>` | Current directory | Root directory to search |
| `-r` | `--regex` | *(flag)* | `false` | Interpret `<query>` as a regular expression |
| `-s` | `--sensitive` | *(flag)* | `false` | Perform case-sensitive matching (default is case-insensitive) |
| `-c` | `--context` | `<int>` | `0` | Lines of context to include before and after each match |
| `-g` | `--globs` | `<patterns>` | None | Comma- or semicolon-separated filename globs (e.g. `"*.cs,*.json"`) |
| `-h` | `--hidden` | *(flag)* | `false` | Include dotfiles and hidden files/directories (still skips `.git`, `.vs`, `.idea`, `.vscode`) |
| `-t` | `--text` | *(flag)* | `false` | Search files containing `NUL` (`0x00`) bytes instead of skipping binary files |
| `-v` | `--invert` | *(flag)* | `false` | Return lines that do **not** match the query |
| `-m` | `--max-depth` | `<int>` | Unlimited | Maximum subdirectory recursion depth |
| — | `--mcp` | *(flag)* | — | Launch stdio JSON-RPC MCP server mode |

*Note:* The `-h` short flag stands for `--hidden` (not `--help`).

---

## 2. C# Library API (`SearchEngine`, `SearchResult`)

```csharp
using System.Threading;
using Glacier.Grep;

var engine = new SearchEngine(@"C:\workspace\MyRepo");

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
List<SearchResult> matches = await engine.SearchAsync(
    query: "TODO|FIXME",
    isRegex: true,
    caseSensitive: false,
    contextLines: 2,
    fileGlobs: ["*.cs", "*.ts"],
    searchHidden: false,
    searchBinary: false,
    invertMatch: false,
    maxDepth: 5,
    cancellationToken: cts.Token);
```

`SearchResult` fields:

| Property | Type | Description |
| :--- | :--- | :--- |
| `FilePath` | `string` | Full path to the matched file |
| `LineNumber` | `int` | 1-based line number (clamped to `int.MaxValue` on huge files) |
| `LongLineNumber` | `long` | 64-bit 1-based line number |
| `MatchContent` | `string` | Matched line text (trailing CR/LF trimmed) |
| `MatchStartIndex` | `int` | Zero-based byte/character offset of the match within the line |
| `MatchLength` | `int` | Length of the match |
| `ContextBefore` | `IReadOnlyList<string>` | Context lines immediately preceding the match |
| `ContextAfter` | `IReadOnlyList<string>` | Context lines immediately following the match |

---

## 3. Ignore Rules, Binary Detection & Regex Semantics

- **Ignore files (`DirectoryTraverser` & `GitIgnoreCompiler`):** Automatically loads `.gitignore`, `.ignore`, and `.rgignore` per directory as it recurses, evaluating nested negation (`!pattern`), directory-only rules (`dir/`), rooted rules (`/path`), and wildcards. Build/VCS directories (`.git`, `.vs`, `.idea`, `.vscode`, `bin`, `obj`, `node_modules`) are always skipped during traversal.
- **Binary guard:** Unless `searchBinary` / `-t` is enabled, files containing null (`0x00`) bytes in the scanned header/chunk are skipped automatically.
- **Linear-time regex execution:** All regular expressions execute with `RegexOptions.Multiline | RegexOptions.NonBacktracking` so pathological patterns cannot trigger catastrophic backtracking. Unsupported .NET backtracking constructs (e.g. lookarounds/backreferences) throw `NotSupportedException` / JSON-RPC `-32602` immediately.
