# Glacier.Grep Manual

Glacier.Grep is a native, high-performance, zero-allocation C# search engine and index for .NET 10. It is designed to run close to the metal with zero-copy semantics, multi-threaded querying, and optimized directory enumeration suitable for indexing and searching large codebases and documents.

---

## 1. Directory Enumeration (`DirectoryTraverser`)

Glacier.Grep uses `System.IO.Enumeration.FileSystemEnumerable<T>` to perform lock-free, zero-allocation traversal.
*   **Pruning on the Stack**: Skips directories and files (like `.git`, `node_modules`, `bin/`, `obj/`, `release/`, and hidden/system files) entirely on the stack using `ReadOnlySpan<char>` comparison on `ref FileSystemEntry` properties.
*   **Ignore Files Hierarchy**: Supports `.gitignore`, `.ignore`, and `.rgignore` rules. It automatically loads and compiles these rules hierarchically as it traverses directories. Deeper rules override parent rules, mirroring Git and Ripgrep's standard path exclusion behavior.
*   **Max Depth Limits**: Supports a recursion depth limit (`maxDepth`) to restrict searches to specific directory levels.
*   **Hidden File Support**: Supports scanning hidden files and folders via `searchHidden` configurations, with safety exclusions to always skip control folders (like `.git` and `.vs`).

---

## 2. Hybrid I/O Dispatcher (`HybridIoDispatcher`)

The dispatcher chooses the fastest input/output pathway depending on the targeted file's size:
*   **For files < 1MB (ArrayPool Path)**: Uses `RandomAccess.Read` into a byte array rented from `ArrayPool<byte>.Shared`. This bypasses `FileStream` and `StreamReader` overhead and eliminates OS paging setup costs for small files.
*   **For files >= 1MB (MemoryMapped Path)**: Mapped using `MemoryMappedFile` and `MemoryMappedViewAccessor`. Acquires a direct `byte*` pointer to the OS page cache using unsafe blocks, wrapped in a `ReadOnlySpan<byte>`.
*   **Binary Filtering**: Automatically skips binary files by checking the first 1024 bytes for a null byte (`\0`), unless `searchBinary` is enabled to search binary files as text.

---

## 3. Search Engine Kernels (`SearchEngine`)

The engine splits work across a thread-safe, concurrent producer-consumer pipeline:
*   **Producer-Consumer Channel**: A single thread traverses directories and pushes files to a `Channel<FileSearchTask>`. Worker threads (1 per logical core) read tasks and scan files concurrently.
*   **SIMD Literal Search**: Searches for literal query strings on raw UTF-8 bytes using .NET 10 `SearchValues<byte>` to find character/byte candidates instantly, followed by stack-allocated line slice tracking and context line resolution.
*   **Zero-Allocation Regex**: Compiles patterns using `RegexOptions.NonBacktracking` (to guarantee linear-time matching and avoid catastrophic backtracking) and runs search using the zero-allocation `Regex.EnumerateMatches(ReadOnlySpan<char>)` API on rented line buffers.
*   **Inverted Matching**: Supports `invertMatch` to show lines that do not match the query string or regex pattern.

---

## 4. MCP Integration

Glacier.Grep includes built-in Model Context Protocol (MCP) server support, enabling LLMs or agents to search codebases:
*   `search_grep`: Searches the directory files utilizing high-performance SIMD pre-filtering or Regex zero-allocation matching.
    *   `query` (string, required): Search term/regex.
    *   `path` (string, optional): Base search directory.
    *   `isRegex` (boolean, default: false): Use regular expression.
    *   `caseSensitive` (boolean, default: false): Match casing exactly.
    *   `contextLines` (integer, default: 2): Lines of context to return around matches.
    *   `fileGlobs` (array of strings, optional): Filter files (e.g. `["*.cs"]`).
    *   `searchHidden` (boolean, default: false): Search hidden files.
    *   `searchBinary` (boolean, default: false): Search binary files as text.
    *   `invertMatch` (boolean, default: false): Show lines not containing the pattern.
    *   `maxDepth` (integer, optional): Maximum folder recursion depth.
*   Results are returned in a clean JSON format containing match offsets, file paths, line numbers, match content, and context before/after.
