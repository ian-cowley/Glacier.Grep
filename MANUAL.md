# Glacier.Grep Manual

Glacier.Grep is a native, high-performance, low-allocation C# search engine for .NET 10. It is designed to run close to the metal with direct UTF-8 span semantics, multi-threaded querying, and optimized directory enumeration for searching large codebases and documents without maintaining a persistent index.

---

## 1. Directory Enumeration (`DirectoryTraverser`)

Glacier.Grep uses `System.IO.Enumeration.FileSystemEnumerable<T>` to perform lock-free, low-allocation traversal.
*   **Span-Based Filtering**: Skips built-in noisy folders (`.git`, `.vs`, `.idea`, `.vscode`, `node_modules`, `bin/`, `obj/`) and hidden/system files using `ReadOnlySpan<char>` comparison on `ref FileSystemEntry` properties and stack/pooled path buffers. (Legitimate `release/` folders and `RELEASE` files are preserved.)
*   **Ignore Files Hierarchy**: Supports `.gitignore`, `.ignore`, and `.rgignore` rules within the search root. It automatically loads and compiles these rules hierarchically as it traverses directories, with deeper rules overriding parent rules (basic wildcards, `!` negation, and `/` directory/rooted rules; `**` globstar, character classes, and backslash escapes are not implemented).
*   **Max Depth Limits**: Supports a recursion depth limit (`maxDepth`) to restrict searches to specific directory levels.
*   **Hidden File Support**: Supports scanning hidden files and folders via `searchHidden` configurations, with safety exclusions to always skip control folders (like `.git` and `.vs`).

---

## 2. Hybrid I/O Dispatcher (`HybridIoDispatcher`)

The dispatcher chooses the fastest input/output pathway depending on the targeted file's actual handle length (`RandomAccess.GetLength`):
*   **For files < 1 MB (and `<= MaxChunkSize`, ArrayPool Path)**: Uses a `RandomAccess.Read` loop into a byte array rented from `ArrayPool<byte>.Shared`, handling stale enumeration lengths, shrinking files, and files that grow past `< 1 MB` or `MaxChunkSize` by promoting seamlessly to memory-mapped I/O.
*   **For files >= 1 MB (or `> MaxChunkSize`, Sliding MemoryMapped Path)**: Maps the file using `MemoryMappedFile.CreateFromFile` and slides per-chunk `MemoryMappedViewAccessor` windows aligned to 64 KB granularity, acquiring a `byte*` pointer per chunk wrapped in a UTF-8 codepoint-safe `ReadOnlySpan<byte>`.
*   **Binary Filtering**: Automatically skips binary files by checking the first 1024 bytes of chunk 0 for a null byte (`\0`) and recording `IsBinary` on `FileSearchContext`, unless `searchBinary` is enabled to search binary files as text.

---

## 3. Search Engine Kernels (`SearchEngine`)

The engine splits work across an asynchronous producer-consumer pipeline:
*   **Producer-Consumer Channel**: A producer task traverses directories and writes tasks to a bounded `Channel<FileSearchTask>` (`SingleWriter = true`). Worker tasks (1 per logical core) read tasks and scan files concurrently.
*   **SIMD Literal Search**: Searches for literal query strings on raw UTF-8 bytes using `ReadOnlySpan<byte>.IndexOf` (case-sensitive) and a `Vector512`/`Vector256`/`Vector128` 2-anchor + rarest-byte `SearchValues<byte>` matcher (case-insensitive), with UTF-16 `MatchStartIndex`/`MatchLength` offsets matching `MatchContent`.
*   **Lowered & Linear-Time Regex**: Lowering via `RegexPatternClassifier.ClassifyPatternFull` executes eligible literal and anchored regexes directly on UTF-8 bytes without UTF-16 decoding; general patterns compile with `RegexOptions.NonBacktracking | RegexOptions.Multiline` and scan via `Regex.EnumerateMatches(ReadOnlySpan<char>)` over pooled `ArrayPool<char>` chunk buffers with cross-boundary deduplication.
*   **Inverted Matching**: Supports `invertMatch` to show lines that do not match the query string or regex pattern, including streaming accumulation for single lines exceeding `MaxChunkSize`.

---

## 4. MCP Integration

Glacier.Grep includes built-in Model Context Protocol (MCP) server support, enabling LLMs or agents to search codebases:
*   `search_grep`: Searches the directory files utilizing SIMD UTF-8 byte scanning or `NonBacktracking` regex matching.
    *   `query` (string, required): Search term/regex.
    *   `path` (string, optional): Base search directory.
    *   `isRegex` (boolean, optional, default: false): Use regular expression.
    *   `caseSensitive` (boolean, optional, default: false): Match casing exactly.
    *   `contextLines` (integer, optional, default: 2): Lines of context to return around matches.
    *   `fileGlobs` (array of strings, optional): Filter files (e.g. `["*.cs"]`).
    *   `searchHidden` (boolean, optional, default: false): Search hidden files.
    *   `searchBinary` (boolean, optional, default: false): Search binary files as text.
    *   `invertMatch` (boolean, optional, default: false): Show lines not containing the pattern.
    *   `maxDepth` (integer, optional): Maximum folder recursion depth.
*   Results are returned in a clean JSON format containing UTF-16 match offsets, file paths, line numbers, match content, and context before/after, with strict JSON-RPC `-32700` / `-32600` / `-32602` error handling.
