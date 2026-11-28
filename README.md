![Glacier.Grep Banner](assets/banner.jpg)

# Glacier.Grep

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Grep.svg)](https://www.nuget.org/packages/Glacier.Grep/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Grep.svg)](https://www.nuget.org/packages/Glacier.Grep/)
[![Ecosystem](https://img.shields.io/badge/Glacier-Ecosystem-blue)](https://github.com/ian-cowley)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-Support-orange.svg?logo=buy-me-a-coffee)](https://buymeacoffee.com/glaciersoftware)

**Glacier.Grep** is a native, high-performance, low-allocation C# search engine for .NET 10. Built for the Glacier ecosystem, it combines hardware-accelerated SIMD UTF-8 scanning, bounded-channel concurrent directory scanning, and built-in support for the Model Context Protocol (MCP). (Glacier.Grep scans files directly without maintaining a persistent search index.)

---

## Key Features

*   🚀 **SIMD-Accelerated Hot Path**: Uses .NET 10 `SearchValues<byte>`, `ReadOnlySpan<byte>.IndexOf`, and `Vector128`/`Vector256`/`Vector512` scanning on raw UTF-8 bytes for case-sensitive, case-insensitive, inverted, and `ClassifyPatternFull`-lowered literal/anchored regexes.
*   📂 **Low-Allocation Directory Enumeration**: Implements custom `FileSystemEnumerable` traversal that evaluates file metadata (hidden/system attributes) and ignore rules using stack-allocated and pooled `Span<char>` buffers.
*   ⚙️ **Hierarchical Ignore File Support**: Evaluates rules from `.gitignore`, `.ignore`, and `.rgignore` hierarchically within the search root (supporting basic wildcards, `!` negation, and `/` directory/rooted rules; `**` globstar, character classes, and backslash escapes are not implemented).
*   ⚡ **Hybrid I/O Dispatcher**: Automatically routes files based on size using per-chunk sliding `MemoryMappedFile` views for files >= 1 MB (or > `MaxChunkSize`) and `RandomAccess` read loops into `ArrayPool<byte>` for files < 1 MB.
*   🔍 **Linear-Time Regex Matching**: Uses `RegexOptions.NonBacktracking` with `Regex.EnumerateMatches(ReadOnlySpan<char>)` over pooled `ArrayPool<char>` chunk buffers for general patterns, while lowering eligible literal/anchored regexes directly to UTF-8 byte scans.
*   🤖 **MCP Server Support**: Built-in support for the Model Context Protocol, allowing seamless integration with AI agents and tools.

---

## Installation

To build and run Glacier.Grep, clone this repository and build using .NET 10 CLI:

```bash
dotnet build src/Glacier.Grep.Host/Glacier.Grep.Host.csproj -c Release
```

---

## Quick Start (CLI Mode)

You can run Glacier.Grep directly from the command line:

```bash
# Basic literal search
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj "public class"

# Case-sensitive search with 2 lines of context in a specific directory
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj "public class" --dir "C:\src\myproject" --sensitive --context 2

# Regex search restricted to C# files
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj "void\s+\w+Async" --regex --globs "*.cs"

# Search hidden files, binary files, and invert the match (lines NOT containing "Oranges")
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj "Oranges" --hidden --text --invert

# Limit search depth to 2 directories
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj "target" --max-depth 2
```

---

## MCP Server Setup

Glacier.Grep includes a built-in MCP (Model Context Protocol) server host, allowing you to use your search engine as a tool for AI agents (like Claude or Antigravity).

### 1. Build the Server
Build the host application in Release mode:

```bash
dotnet build src/Glacier.Grep.Host/Glacier.Grep.Host.csproj -c Release
```

### 2. Configure Your Client
Add the following entry to your `mcp_config.json`:

```json
{
  "mcpServers": {
    "glacier-grep": {
      "command": "dotnet",
      "args": [
        "ABS_PATH_TO_REPO/src/Glacier.Grep.Host/bin/Release/net10.0/Glacier.Grep.Host.dll"
      ]
    }
  }
}
```

### 3. Available Tools

- **`search_grep`**: Performs a high-performance search across files in a directory.
  - `query` (string, required): The search query (literal or regex).
  - `path` (string, optional): Base directory to search.
  - `isRegex` (boolean, optional): Treat query as regular expression.
  - `caseSensitive` (boolean, optional): Perform case-sensitive match.
  - `contextLines` (integer, optional): Lines of context to include around matches.
  - `fileGlobs` (array of strings, optional): Filter files (e.g. `["*.cs"]`).
  - `searchHidden` (boolean, optional): Search hidden files and folders.
  - `searchBinary` (boolean, optional): Search binary files (do not skip).
  - `invertMatch` (boolean, optional): Invert the match (show lines that do NOT contain the query).
  - `maxDepth` (integer, optional): Maximum recursion depth for subdirectories.

---

## Architecture & I/O Design

Glacier.Grep is engineered in pure C# .NET 10 with zero native DLL dependencies. Note that no standalone benchmark suite is currently shipped in this repository.

### File-Backed Memory Mapping & Bounded Sliding Views
For files >= 1 MB (or exceeding `HybridIoDispatcher.MaxChunkSize`), `HybridIoDispatcher` maps files directly from the OS page cache via file-backed `MemoryMappedFile.CreateFromFile` and slides bounded per-chunk `MemoryMappedViewAccessor` windows (defaulting to 16 MB UTF-8 codepoint-aligned chunks plus overlap and 64 KB newline lookahead), avoiding single whole-file mappings and multi-gigabyte regex decode buffers. For files < 1 MB, `HybridIoDispatcher` reads via `RandomAccess.Read` loops into pooled `ArrayPool<byte>` buffers and handles files whose size changes between enumeration and read.

1.  **Directory Traverser**: Low-allocation metadata filtration. Prunes ignored folders (like `.git`, `node_modules`, `bin/`, `obj/`) and applies `.gitignore`, `.ignore`, and `.rgignore` rules.
2.  **Hybrid I/O Dispatcher**: Routes files < 1 MB through `RandomAccess` pooled reads and files >= 1 MB through sliding `MemoryMappedFile` views.
3.  **Search Core**: Evaluates raw UTF-8 `ReadOnlySpan<byte>` via `SearchValues<byte>`, `Vector128`/`Vector256`/`Vector512` case-insensitive anchors, `ClassifyPatternFull` regex lowering, and `RegexOptions.NonBacktracking` fallback.
4.  **MCP Integration**: Exposes search capabilities over standard stdio JSON-RPC with strict `-32700`/`-32600`/`-32602` validation.

---

## 🆕 What's New in v1.1.1

- **Genuine file-backed memory mapping & per-chunk sliding views** — replaces full-file single-view mappings with per-chunk sliding `MemoryMappedViewAccessor` windows and adds `< 1 MB` `RandomAccess` read loops resilient to stale/0-byte/growing lengths.
- **Cross-chunk regex, long-line inverted streaming & UTF-16 offset parity** — fixes non-newline boundary regex drops, supports lines `> MaxChunkSize` in `invertMatch` mode, and aligns `MatchStartIndex`/`MatchLength` to UTF-16 `MatchContent` indices.
- **SIMD multi-byte case-insensitive search & Opt #12 `ClassifyPatternFull`** — adds `Vector512`/`Vector256`/`Vector128` 2-anchor + rarest-byte `SearchValues<byte>` scanning and lowers eligible literal/anchored regexes directly to UTF-8 byte scans.
- **100% Native AOT MCP Server (`v1.1.1`)** — strongly-typed source-generated `McpJsonContext` JSON-RPC envelopes for `initialize`, `tools/list`, and `tools/call` with strict `-32602` parameter validation.
- **Accurate directory traversal** — removed hardcoded skipping of legitimate `release/` folders and `RELEASE` files.

---

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## ❄️ Glacier Ecosystem & Support

Part of the **[Glacier Ecosystem](https://glaciersoftware.dev/)** — high-performance, low-allocation developer tooling and data engines natively built for modern C# and .NET 10.

* 🌐 **Website & Documentation**: [glaciersoftware.dev](https://glaciersoftware.dev/)
* ☕ **Buy Me a Coffee**: [buymeacoffee.com/glaciersoftware](https://buymeacoffee.com/glaciersoftware)
* ❤️ **GitHub Sponsors**: [github.com/sponsors/ian-cowley](https://github.com/sponsors/ian-cowley)
* 🐙 **All Repositories**: [github.com/ian-cowley](https://github.com/ian-cowley)
