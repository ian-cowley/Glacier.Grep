using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Glacier.Grep.Host.Mcp
{
    /// <summary>
    /// Native AOT-compatible Model Context Protocol (MCP) server over JSON-RPC stdio for Glacier.Grep.
    /// Uses 100% source-generated <see cref="McpJsonContext"/> serialization with zero runtime reflection.
    /// </summary>
    public class McpServer
    {
        private static readonly string ServerVersion = ResolveServerVersion();

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

        private static string ResolveServerVersion()
        {
            Assembly assembly = typeof(McpServer).Assembly;
            string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                int plusIdx = infoVersion.IndexOf('+');
                return plusIdx >= 0 ? infoVersion.Substring(0, plusIdx) : infoVersion;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.1.1";
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
                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(line);
                    }
                    catch (JsonException)
                    {
                        await SendErrorAsync(default, -32700, "Parse error: invalid JSON.");
                        continue;
                    }

                    using (doc)
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object)
                        {
                            await SendErrorAsync(default, -32600, "Invalid Request: JSON-RPC request must be a JSON object.");
                            continue;
                        }

                        if (root.TryGetProperty("id", out JsonElement rawId))
                        {
                            if (rawId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
                            {
                                await SendErrorAsync(default, -32600, "Invalid Request: 'id' must be a string, number, or null.");
                                continue;
                            }
                            requestId = rawId.Clone();
                        }

                        if (!root.TryGetProperty("method", out JsonElement methodElem) ||
                            methodElem.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(methodElem.GetString()))
                        {
                            await SendErrorAsync(requestId, -32600, "Invalid Request: missing or invalid 'method' property.");
                            continue;
                        }

                        string method = methodElem.GetString()!;

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
                                if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object)
                                {
                                    await SendErrorAsync(requestId, -32602, "Invalid params: 'params' must be a JSON object.");
                                }
                                else
                                {
                                    var result = await HandleToolCallAsync(parameters);
                                    await SendToolCallResponseAsync(requestId, result);
                                }
                                break;

                            default:
                                Log($"Unhandled method: {method}");
                                if (requestId.ValueKind != JsonValueKind.Undefined)
                                {
                                    await SendErrorAsync(requestId, -32601, $"Method '{method}' not implemented.");
                                }
                                break;
                        }
                    }
                }
                catch (McpInvalidParamsException ex)
                {
                    Log($"Invalid params: {ex.Message}");
                    if (requestId.ValueKind != JsonValueKind.Undefined)
                    {
                        await SendErrorAsync(requestId, -32602, ex.Message);
                    }
                }
                catch (RegexParseException ex)
                {
                    Log($"Invalid regex: {ex.Message}");
                    if (requestId.ValueKind != JsonValueKind.Undefined)
                    {
                        await SendErrorAsync(requestId, -32602, $"Invalid regex pattern: {ex.Message}");
                    }
                }
                catch (ArgumentException ex)
                {
                    Log($"Invalid argument: {ex.Message}");
                    if (requestId.ValueKind != JsonValueKind.Undefined)
                    {
                        await SendErrorAsync(requestId, -32602, ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log($"CRITICAL error: {ex.Message}");
                    if (requestId.ValueKind != JsonValueKind.Undefined)
                    {
                        try { await SendErrorAsync(requestId, -32603, $"Internal Server Error: {ex.Message}"); }
                        catch (Exception sendEx) when (sendEx is IOException or ObjectDisposedException or InvalidOperationException)
                        {
                            Log($"Failed to send error response: {sendEx.Message}");
                        }
                    }
                }
            }
        }

        private static McpInitializeResult HandleInitialize() => new()
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new McpCapabilities { Tools = new McpToolsCapability { ListChanged = false } },
            ServerInfo = new McpServerInfo { Name = "Glacier.Grep", Version = ServerVersion }
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

        private static async Task<McpToolResponse> HandleToolCallAsync(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("name", out JsonElement nameElem) ||
                nameElem.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameElem.GetString()))
            {
                throw new McpInvalidParamsException("Invalid params: 'name' must be a non-empty string.");
            }

            string toolName = nameElem.GetString()!;
            if (!string.Equals(toolName, "search_grep", StringComparison.Ordinal))
            {
                throw new McpInvalidParamsException($"Tool '{toolName}' is not supported.");
            }

            if (!parameters.TryGetProperty("arguments", out JsonElement arguments) ||
                arguments.ValueKind != JsonValueKind.Object)
            {
                throw new McpInvalidParamsException("Invalid params: 'arguments' must be a JSON object.");
            }

            if (!arguments.TryGetProperty("query", out JsonElement queryElem) ||
                queryElem.ValueKind != JsonValueKind.String)
            {
                throw new McpInvalidParamsException("Invalid params: 'query' must be a string.");
            }

            string query = queryElem.GetString() ?? throw new McpInvalidParamsException("Invalid params: 'query' must be a string.");
            string searchDir = GetOptionalNullableString(arguments, "path") is { Length: > 0 } configuredPath
                ? configuredPath
                : Directory.GetCurrentDirectory();

            bool isRegex = GetOptionalBoolean(arguments, "isRegex", defaultValue: false);
            bool caseSensitive = GetOptionalBoolean(arguments, "caseSensitive", defaultValue: false);
            int contextLines = GetOptionalNonNegativeInt32(arguments, "contextLines", defaultValue: 2);
            bool searchHidden = GetOptionalBoolean(arguments, "searchHidden", defaultValue: false);
            bool searchBinary = GetOptionalBoolean(arguments, "searchBinary", defaultValue: false);
            bool invertMatch = GetOptionalBoolean(arguments, "invertMatch", defaultValue: false);
            int? maxDepth = GetOptionalNullableNonNegativeInt32(arguments, "maxDepth");
            string[]? fileGlobs = GetOptionalNullableStringArray(arguments, "fileGlobs");

            if (!Directory.Exists(searchDir))
            {
                return CreateToolResponse($"Directory not found: {searchDir}");
            }

            var engine = new SearchEngine(searchDir);
            List<SearchResult> matches;
            try
            {
                matches = await engine.SearchAsync(
                    query,
                    isRegex,
                    caseSensitive,
                    contextLines,
                    fileGlobs,
                    searchHidden,
                    searchBinary,
                    invertMatch,
                    maxDepth);
            }
            catch (NotSupportedException ex) when (isRegex)
            {
                throw new McpInvalidParamsException($"Unsupported regex pattern: {ex.Message}");
            }

            const int maxResults = 250;
            if (matches.Count > maxResults)
            {
                var truncated = matches.GetRange(0, maxResults);
                string truncatedJson = JsonSerializer.Serialize(truncated, McpJsonContext.Default.ListSearchResult);
                string summary = $"Showing first {maxResults} out of {matches.Count} matches found.\n\n" + truncatedJson;
                return CreateToolResponse(summary);
            }

            string resultsJson = JsonSerializer.Serialize(matches, McpJsonContext.Default.ListSearchResult);
            return CreateToolResponse(resultsJson);
        }

        private static string? GetOptionalNullableString(JsonElement arguments, string propertyName)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement elem) || elem.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (elem.ValueKind != JsonValueKind.String)
            {
                throw new McpInvalidParamsException($"Invalid params: '{propertyName}' must be a string or null.");
            }

            return elem.GetString();
        }

        private static bool GetOptionalBoolean(JsonElement arguments, string propertyName, bool defaultValue)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement elem))
            {
                return defaultValue;
            }

            return elem.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new McpInvalidParamsException($"Invalid params: '{propertyName}' must be a boolean.")
            };
        }

        private static int GetOptionalNonNegativeInt32(JsonElement arguments, string propertyName, int defaultValue)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement elem))
            {
                return defaultValue;
            }

            if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetInt32(out int value) || value < 0)
            {
                throw new McpInvalidParamsException($"Invalid params: '{propertyName}' must be a non-negative 32-bit integer.");
            }

            return value;
        }

        private static int? GetOptionalNullableNonNegativeInt32(JsonElement arguments, string propertyName)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement elem) || elem.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (elem.ValueKind != JsonValueKind.Number || !elem.TryGetInt32(out int value) || value < 0)
            {
                throw new McpInvalidParamsException($"Invalid params: '{propertyName}' must be a non-negative 32-bit integer or null.");
            }

            return value;
        }

        private static string[]? GetOptionalNullableStringArray(JsonElement arguments, string propertyName)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement elem) || elem.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (elem.ValueKind != JsonValueKind.Array)
            {
                throw new McpInvalidParamsException($"Invalid params: '{propertyName}' must be an array of strings or null.");
            }

            var list = new List<string>();
            foreach (JsonElement item in elem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } glob)
                {
                    throw new McpInvalidParamsException($"Invalid params: every element in '{propertyName}' must be a string.");
                }
                list.Add(glob);
            }
            return list.ToArray();
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

        private sealed class McpInvalidParamsException(string message) : Exception(message);
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
        [JsonPropertyName("version")] public string Version { get; set; } = "1.1.1";
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
