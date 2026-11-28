![Glacier.Grep Banner](assets/banner.jpg)

# Glacier.Grep

[![CI](https://github.com/ian-cowley/Glacier.Grep/actions/workflows/dotnet.yml/badge.svg)](https://github.com/ian-cowley/Glacier.Grep/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)
![NativeAOT](https://img.shields.io/badge/NativeAOT-Compatible-brightgreen.svg)

`Glacier.Grep` is a .NET 10 file search library, CLI tool, and Native AOT Model Context Protocol (MCP) server. It traverses directories while pruning `.gitignore`, `.ignore`, and `.rgignore` rules, selects buffered or memory-mapped I/O by file size, and accelerates literal, wildcard, and non-backtracking regex matching using UTF-8 byte spans and SIMD instructions.

---

## Install

Add the search library to a .NET project:

```bash
dotnet add package Glacier.Grep
```

Or build the CLI and MCP host from source:

```bash
git clone https://github.com/ian-cowley/Glacier.Grep.git
cd Glacier.Grep
dotnet test Glacier.Grep.slnx -c Release
```

---

## Quick Start

### Embedded C# API

```csharp
using Glacier.Grep;

var engine = new SearchEngine("./my-workspace");

List<SearchResult> results = await engine.SearchAsync(
    query: "MemoryMappedFile",
    isRegex: false,
    caseSensitive: true,
    contextLines: 2,
    fileGlobs: ["*.cs"]);

foreach (SearchResult match in results)
{
    Console.WriteLine($"{match.FilePath}:{match.LineNumber}: {match.MatchContent}");
}
```

### CLI & MCP Host (`Glacier.Grep.Host`)

```bash
# Search the current directory with 2 context lines across C# files
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj -c Release -- "SearchEngine" -d ./src -g "*.cs" -c 2

# Start as a stdio JSON-RPC MCP server (--mcp or zero arguments)
dotnet run --project src/Glacier.Grep.Host/Glacier.Grep.Host.csproj -c Release -- --mcp
```

---

## What You Can Do

- **`.gitignore` / `.ignore` / `.rgignore` Pruning:** Skip ignored paths and build artifacts (`.git`, `.vs`, `.idea`, `.vscode`, `bin`, `obj`, `node_modules`) during directory enumeration before opening file handles.
- **Literal & Non-Backtracking Regex Search:** Lower common regex patterns (`foo`, `^prefix`, `suffix$`, `^exact$`, `^prefix.*suffix$`, `a.*b`) into direct byte/SIMD matchers while falling back to `RegexOptions.NonBacktracking` for full regular expressions.
- **Context, Globs, Binary & Inverted Filtering:** Filter filenames with globs (`-g`), limit traversal depth (`-m`), include hidden or binary files (`-h`, `-t`), print surrounding context lines (`-c`), or invert matches (`-v`).
- **Native AOT MCP Server (`search_grep`):** Expose the engine directly to AI coding agents over stdio JSON-RPC via `Glacier.Grep.Host`.

---

## Platform & Compatibility

- **Runtime:** .NET 10 (`net10.0`), Native AOT compatible (`IsAotCompatible=true`, `EnableTrimAnalyzer=true`).
- **SIMD Acceleration:** Hardware-accelerated UTF-8 searching (`Vector128` / `Vector256` / `SearchValues`) plus ASCII case-folding (`AsciiIgnoreCaseMatcher`).
- **Cross-Platform:** Tested on Windows and Linux x64 in GitHub Actions CI.

---

## Documentation

- [User Manual](docs/manual.md) — CLI flags, `SearchEngine` / `SearchResult` C# API, glob filtering, ignore rules, and binary detection
- [Architecture](docs/architecture.md) — Bounded channel pipeline, `DirectoryTraverser`, `HybridIoDispatcher` chunk overlap, and `RegexPatternClassifier`
- [MCP Server Setup](docs/mcp.md) — Connecting AI clients to `Glacier.Grep.Host` (`search_grep` schema and JSON-RPC error codes)
- [Changelog](CHANGELOG.md) — Version history

---

## License & Credits

Licensed under the [MIT License](LICENSE).

Developed by Ian Cowley, Antigravity (Google DeepMind) and Claude.
