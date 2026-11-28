# Changelog

All notable changes to `Glacier.Grep` are documented here.

## v1.1.1

- Fixed multi-chunk regex overlap deduplication across `HybridIoDispatcher` seams.
- Fixed newline chunk boundary alignment in `SearchEngine` line counting and context extraction.

## v1.1.0

- Hardened `Glacier.Grep.Host` MCP JSON-RPC validation (`-32700`, `-32600`, `-32601`, `-32602`) and capped `search_grep` output cleanly at 250 matches with explicit counts.
- Added UTF-8 codepoint-safe chunk boundary advancement and `precedingRune` lookbehind context in `HybridIoDispatcher`.
- Enforced `RegexOptions.NonBacktracking` across general regex searches and `RegexPatternClassifier` optimization dispatch.
