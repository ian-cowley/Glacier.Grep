![Glacier.Grep Banner](assets/banner.jpg)

# Glacier.Grep

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Grep.svg)](https://www.nuget.org/packages/Glacier.Grep/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Grep.svg)](https://www.nuget.org/packages/Glacier.Grep/)
[![Ecosystem](https://img.shields.io/badge/Glacier-Ecosystem-blue)](https://github.com/ian-cowley)

**Glacier.Grep** is a native, high-performance, zero-allocation C# search engine and index for .NET 10. Built to take on `ripgrep` within the Glacier ecosystem, it provides aggressive hardware acceleration (SIMD), concurrent work-stealing directory scanning, and built-in support for the Model Context Protocol (MCP).

---

## Key Features

*   🚀 **SIMD-Accelerated Hot Path**: Utilizes hardware-accelerated set searching via .NET 10 `SearchValues<byte>` and auto-vectorized scanning on raw UTF-8 bytes without string conversions.
*   📂 **Lock-Free Directory Enumeration**: Implements custom `FileSystemEnumerable` checking which evaluates file metadata (hidden status, symlinks) and ignore matches entirely on the stack before materializing path strings.
*   ⚙️ **Advanced Ignore File Support**: Evaluates rules from `.gitignore`, `.ignore`, and `.rgignore` hierarchically, matching `ripgrep`'s prioritized exclusion behavior.
*   ⚡ **Hybrid I/O Dispatcher**: Automatically routes files based on size (using zero-copy Memory-Mapped Files for >1MB and RandomAccess read into `ArrayPool<byte>` for <1MB).
*   🔍 **Zero-Allocation Regex Matching**: Leverages .NET JIT-compiled regex engines with `Regex.EnumerateMatches(ReadOnlySpan<char>)` for zero heap allocation searches.
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

## Performance

Glacier.Grep is engineered in pure C# .NET 10 to saturate CPU memory bandwidth. On exact case-sensitive code symbol searches, Glacier.Grep **outperforms Rust's Ripgrep by 12%**, while maintaining near-parity (within 1.2x–1.5x) on complex case-insensitive searches with zero native DLL dependencies.

### Benchmark: Full Glacier / PolarsPlus Repository Search
- **Target Corpus**: 590 files, 257.83 MB
- **Platform**: Windows x64, AMD Ryzen AI 9 HX 370

| Search Query | Match Mode | Ripgrep (Rust) | Glacier.Grep (.NET 10) | Result |
| :--- | :--- | :---: | :---: | :--- |
| `"public class"` | Case-Sensitive | 134.9 ms | **120.4 ms** | 🚀 **Glacier is 12% FASTER** |
| `"ThreadIndependentReaderWriterLock"` | Case-Insensitive | **115.7 ms** | 142.3 ms | ⚡ **Near-Parity** (within 1.23x of Rust) |
| `"public class"` | Case-Insensitive | **137.9 ms** | 210.4 ms | 🔹 Ripgrep leads (within 1.52x of Rust) |

### Memory-Mapped File Pooling (PB-21 Optimization)
To eliminate Windows OS section object and view creation overhead on medium-sized files (1MB to 8MB), `Glacier.Grep` utilizes an 8MB anonymous `MemoryMappedFile` handle pool (`s_mediumMmfPool`):
- **Medium File Processing**: 2MB file processing and 30,000-line search completed in **23.27 ms** on AMD Ryzen AI 9 HX 370.
- **Section Handle Churn**: Reduced by 100% via pooled view re-mapping (`CreateViewAccessor(0, fileLen)`).
- **Test Suite**: 14/14 unit, regex, ignore rule, and MCP tests pass cleanly.

---

## Architecture

1.  **Directory Traverser**: Zero-allocation metadata filtration. Prunes ignored folders (like `.git`, `node_modules`, `bin/`) and applies `.gitignore`, `.ignore`, and `.rgignore` rules.
2.  **Hybrid I/O Dispatcher**: Chooses the fastest reading technique depending on file size.
3.  **Search Core**: Evaluates the bytes using `ReadOnlySpan<byte>` and JIT vectorization.
4.  **MCP Integration**: Integrates the search capabilities over standard stdio JSON-RPC.

---

## 🆕 What's New in v1.0.1

- **1 GB chunk overlap margin corrected** — boundary formula fixed to `chunkOffset - patternLength + 1`, preventing missed matches spanning chunk boundaries.
- **Monotonic global line numbering across chunk boundaries** — line numbers are now globally consistent regardless of how the file is split into chunks.
- **14 tests** passing (100 %).

---

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
