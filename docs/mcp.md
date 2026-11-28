# Glacier.Grep MCP Server (`Glacier.Grep.Host`)

`Glacier.Grep.Host` implements an AOT-compatible Model Context Protocol (`2024-11-05`) server over JSON-RPC stdio using source-generated `System.Text.Json` serialization (`McpJsonContext`).

---

## Build or Publish

```bash
dotnet publish src/Glacier.Grep.Host/Glacier.Grep.Host.csproj -c Release -o ./publish/glacier-grep
```

Launching `Glacier.Grep.Host` with **no arguments** or with `--mcp` starts the stdio MCP server loop.

Optional diagnostic logging is disabled by default; set `GLACIER_GREP_MCP_LOG=/path/to/grep_mcp.log` to enable file logging.

---

## MCP Client Configuration Example

```json
{
  "mcpServers": {
    "glacier-grep": {
      "command": "/path/to/publish/glacier-grep/Glacier.Grep.Host",
      "args": ["--mcp"]
    }
  }
}
```

---

## Exposed Tool: `search_grep`

Searches files in a workspace directory using SIMD pre-filtering and non-backtracking regex matching. Returns JSON-serialized `SearchResult[]` (capped at `250` results with a truncation summary header when more matches exist).

### Parameters

| Parameter | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `query` | `string` *(required)* | — | Literal text or regular expression pattern |
| `path` | `string` | Current working directory | Root directory to search |
| `isRegex` | `boolean` | `false` | Interpret `query` as a regular expression |
| `caseSensitive` | `boolean` | `false` | Enable case-sensitive matching |
| `contextLines` | `integer` (`>= 0`) | `2` | Context lines before and after each match |
| `fileGlobs` | `string[]` | `null` | Filename glob patterns (e.g. `["*.cs", "*.json"]`) |
| `searchHidden` | `boolean` | `false` | Include dotfiles and hidden directories |
| `searchBinary` | `boolean` | `false` | Search binary files containing null bytes |
| `invertMatch` | `boolean` | `false` | Return non-matching lines |
| `maxDepth` | `integer` (`>= 0`) | `null` (unlimited) | Maximum directory recursion depth |

---

## JSON-RPC Error Behavior

- `-32700`: Invalid JSON input line
- `-32600`: Invalid request envelope or missing `method` / invalid `id`
- `-32601`: Unimplemented JSON-RPC method
- `-32602`: Unknown tool name, invalid argument types/ranges, negative `contextLines`/`maxDepth`, or invalid/unsupported regex pattern
- `-32603`: Internal server error
