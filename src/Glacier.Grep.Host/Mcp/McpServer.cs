using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Glacier.Grep.Host.Mcp
{
    /// <summary>
    /// Native AOT-compatible Model Context Protocol (MCP) server over JSON-RPC stdio for Glacier.Grep.
    /// Uses 100% source-generated <see cref="McpJsonContext"/> serialization with zero runtime reflection.
    /// </summary>
    public class McpServer
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string? _logPath;

        public McpServer()
            : this(Console.OpenStandardInput(), Console.OpenStandardOutput())
        {
        }

        public McpServer(Stream input, Stream output)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);

            string? configuredLog = Environment.GetEnvironmentVariable("GLACIER_GREP_MCP_LOG");
            _logPath = string.IsNullOrWhiteSpace(configuredLog) ? null : configuredLog;
            Log("Grep MCP Server instance starting up.");

            _reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        private void Log(string message)
        {
            if (_logPath == null) return;
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.UtcNow:O}] {message}\n");
            }
            catch { /* Ignore logging failures */ }
        }

        public async Task RunAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement requestId = default;
                try
                {
                    Log($"Received: {line}");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("method", out JsonElement methodElem))
                    {
                        Log("Invalid message: missing 'method' property.");
                        continue;
                    }

                    string method = methodElem.GetString() ?? string.Empty;
                    if (root.TryGetProperty("id", out JsonElement rawId))
                    {
                        requestId = rawId.Clone();
                    }

                    switch (method)
                    {
                        case "initialize":
                            await SendInitializeResponseAsync(requestId, HandleInitialize());
                            break;

                        case "notifications/initialized":
                            Log("Client initialization confirmed.");
                            break;

                        case "tools/list":
                            await SendToolsListResponseAsync(requestId, HandleToolsList());
                            break;

                        case "tools/call":
                            if (root.TryGetProperty("params", out JsonElement parameters))
                            {
                                var result = await HandleToolCallAsync(parameters);
                                await SendToolCallResponseAsync(requestId, result);
                            }
                            else
                            {
                                await SendErrorAsync(requestId, -32602, "Parameters missing for tools/call");
                            }
                            break;

                        default:
                            Log($"Unhandled method: {method}");
                            if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                            {
                                await SendErrorAsync(requestId, -32601, $"Method '{method}' not implemented.");
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"CRITICAL error: {ex.Message}");
                    if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                    {
                        try { await SendErrorAsync(requestId, -32603, $"Internal Server Error: {ex.Message}"); } catch { }
                    }
                }
            }
        }

        private static McpInitializeResult HandleInitialize() => new()
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpCapabilities { Tools = new McpToolsCapability { ListChanged = false } },
            ServerInfo = new McpServerInfo { Name = "Glacier.Grep", Version = "1.1.0" }
        };

        private static McpToolsListResult HandleToolsList() => new()
        {
            Tools =
            [
                new McpToolDefinition
                {
                    Name = "search_grep",
                    Description = "Searches directory files utilizing SIMD pre-filtering or span-based Regex matching.",
                    InputSchema = new McpInputSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, McpPropertySchema>
                        {
                            ["query"] = new() { Type = "string", Description = "The search query (literal string or regex pattern)." },
                            ["path"] = new() { Type = "string", Description = "Base directory to search in. Defaults to current workspace root." },
                            ["isRegex"] = new() { Type = "boolean", Description = "Whether the query is a regular expression. Default: false." },
                            ["caseSensitive"] = new() { Type = "boolean", Description = "Whether to perform case-sensitive search. Default: false." },
                            ["contextLines"] = new() { Type = "integer", Description = "Lines of context before/after matches. Default: 2." },
                            ["fileGlobs"] = new()
                            {
                                Type = "array",
                                Items = new McpPropertyItemSchema { Type = "string" },
                                Description = "Specific file glob patterns to search (e.g. ['*.cs', '*.json'])."
                            },
                            ["searchHidden"] = new() { Type = "boolean", Description = "Search hidden files and folders. Default: false." },
                            ["searchBinary"] = new() { Type = "boolean", Description = "Search binary files. Default: false." },
                            ["invertMatch"] = new() { Type = "boolean", Description = "Show lines that do NOT contain the pattern. Default: false." },
                            ["maxDepth"] = new() { Type = "integer", Description = "Maximum recursion depth for folders." }
                        },
                        Required = ["query"]
                    }
                }
            ]
        };

        private async Task<McpToolResponse> HandleToolCallAsync(JsonElement parameters)
        {
            string toolName = parameters.GetProperty("name").GetString() ?? string.Empty;
            JsonElement arguments = parameters.GetProperty("arguments");

            if (toolName == "search_grep")
            {
                string query = arguments.GetProperty("query").GetString() ?? throw new ArgumentException("Missing 'query' parameter");

                string searchDir = arguments.TryGetProperty("path", out JsonElement pathElem)
                    ? pathElem.GetString() ?? Directory.GetCurrentDirectory()
                    : Directory.GetCurrentDirectory();

                bool isRegex = arguments.TryGetProperty("isRegex", out JsonElement regexElem) && regexElem.GetBoolean();
                bool caseSensitive = arguments.TryGetProperty("caseSensitive", out JsonElement caseElem) && caseElem.GetBoolean();
                int contextLines = arguments.TryGetProperty("contextLines", out JsonElement contextElem) ? contextElem.GetInt32() : 2;
                bool searchHidden = arguments.TryGetProperty("searchHidden", out JsonElement hiddenElem) && hiddenElem.GetBoolean();
                bool searchBinary = arguments.TryGetProperty("searchBinary", out JsonElement binaryElem) && binaryElem.GetBoolean();
                bool invertMatch = arguments.TryGetProperty("invertMatch", out JsonElement invertElem) && invertElem.GetBoolean();
                int? maxDepth = arguments.TryGetProperty("maxDepth", out JsonElement depthElem) ? depthElem.GetInt32() : null;

                string[]? fileGlobs = null;
                if (arguments.TryGetProperty("fileGlobs", out JsonElement globsElem) && globsElem.ValueKind == JsonValueKind.Array)
                {
                    var globsList = new List<string>();
                    foreach (var item in globsElem.EnumerateArray())
                    {
                        if (item.GetString() is { } glob) globsList.Add(glob);
                    }
                    fileGlobs = globsList.ToArray();
                }

                if (!Directory.Exists(searchDir))
                {
                    return CreateToolResponse($"Directory not found: {searchDir}");
                }

                var engine = new SearchEngine(searchDir);
                var matches = await engine.SearchAsync(
                    query,
                    isRegex,
                    caseSensitive,
                    contextLines,
                    fileGlobs,
                    searchHidden,
                    searchBinary,
                    invertMatch,
                    maxDepth);

                const int maxResults = 250;
                if (matches.Count > maxResults)
                {
                    var truncated = matches.GetRange(0, maxResults);
                    string truncatedJson = JsonSerializer.Serialize(truncated, McpJsonContext.Default.ListSearchResult);
                    string summary = $"Showing first {maxResults} out of {matches.Count} matches found.\n\n" + truncatedJson;
                    return CreateToolResponse(summary);
                }
                else
                {
                    string resultsJson = JsonSerializer.Serialize(matches, McpJsonContext.Default.ListSearchResult);
                    return CreateToolResponse(resultsJson);
                }
            }

            throw new ArgumentException($"Tool '{toolName}' is not supported.");
        }

        private static McpToolResponse CreateToolResponse(string text) => new()
        {
            Content = [new McpTextContent { Type = "text", Text = text }]
        };

        private Task SendInitializeResponseAsync(JsonElement id, McpInitializeResult result)
        {
            var envelope = new McpJsonRpcInitializeResponse
            {
                Id = id.ValueKind == JsonValueKind.Undefined ? null : id,
                Result = result
            };
            string json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpJsonRpcInitializeResponse);
            return _writer.WriteLineAsync(json);
        }

        private Task SendToolsListResponseAsync(JsonElement id, McpToolsListResult result)
        {
            var envelope = new McpJsonRpcToolsListResponse
            {
                Id = id.ValueKind == JsonValueKind.Undefined ? null : id,
                Result = result
            };
            string json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpJsonRpcToolsListResponse);
            return _writer.WriteLineAsync(json);
        }

        private Task SendToolCallResponseAsync(JsonElement id, McpToolResponse result)
        {
            var envelope = new McpJsonRpcToolCallResponse
            {
                Id = id.ValueKind == JsonValueKind.Undefined ? null : id,
                Result = result
            };
            string json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpJsonRpcToolCallResponse);
            return _writer.WriteLineAsync(json);
        }

        private Task SendErrorAsync(JsonElement id, int code, string message)
        {
            var envelope = new McpJsonRpcErrorResponse
            {
                Id = id.ValueKind == JsonValueKind.Undefined ? null : id,
                Error = new McpErrorDetail { Code = code, Message = message }
            };
            string json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpJsonRpcErrorResponse);
            return _writer.WriteLineAsync(json);
        }
    }

    public sealed class McpJsonRpcInitializeResponse
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement? Id { get; set; }
        [JsonPropertyName("result")] public McpInitializeResult Result { get; set; } = new();
    }

    public sealed class McpJsonRpcToolsListResponse
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement? Id { get; set; }
        [JsonPropertyName("result")] public McpToolsListResult Result { get; set; } = new();
    }

    public sealed class McpJsonRpcToolCallResponse
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement? Id { get; set; }
        [JsonPropertyName("result")] public McpToolResponse Result { get; set; } = new();
    }

    public sealed class McpJsonRpcErrorResponse
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
        [JsonPropertyName("id")] public JsonElement? Id { get; set; }
        [JsonPropertyName("error")] public McpErrorDetail Error { get; set; } = new();
    }

    public sealed class McpInitializeResult
    {
        [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "2024-11-05";
        [JsonPropertyName("capabilities")] public McpCapabilities Capabilities { get; set; } = new();
        [JsonPropertyName("serverInfo")] public McpServerInfo ServerInfo { get; set; } = new();
    }

    public sealed class McpCapabilities
    {
        [JsonPropertyName("tools")] public McpToolsCapability Tools { get; set; } = new();
    }

    public sealed class McpToolsCapability
    {
        [JsonPropertyName("listChanged")] public bool ListChanged { get; set; }
    }

    public sealed class McpServerInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "Glacier.Grep";
        [JsonPropertyName("version")] public string Version { get; set; } = "1.1.0";
    }

    public sealed class McpToolsListResult
    {
        [JsonPropertyName("tools")] public McpToolDefinition[] Tools { get; set; } = [];
    }

    public sealed class McpToolDefinition
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("inputSchema")] public McpInputSchema InputSchema { get; set; } = new();
    }

    public sealed class McpInputSchema
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "object";
        [JsonPropertyName("properties")] public Dictionary<string, McpPropertySchema> Properties { get; set; } = new();
        [JsonPropertyName("required")] public string[] Required { get; set; } = [];
    }

    public sealed class McpPropertySchema
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "string";
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("items")] public McpPropertyItemSchema? Items { get; set; }
    }

    public sealed class McpPropertyItemSchema
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "string";
    }

    public sealed class McpTextContent
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "text";
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    public sealed class McpToolResponse
    {
        [JsonPropertyName("content")] public McpTextContent[] Content { get; set; } = [];
    }

    public sealed class McpErrorDetail
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(List<SearchResult>))]
    [JsonSerializable(typeof(SearchResult))]
    [JsonSerializable(typeof(McpJsonRpcInitializeResponse))]
    [JsonSerializable(typeof(McpJsonRpcToolsListResponse))]
    [JsonSerializable(typeof(McpJsonRpcToolCallResponse))]
    [JsonSerializable(typeof(McpJsonRpcErrorResponse))]
    internal partial class McpJsonContext : JsonSerializerContext
    {
    }
}
