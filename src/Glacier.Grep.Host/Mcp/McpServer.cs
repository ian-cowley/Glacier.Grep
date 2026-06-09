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
    /// A zero-dependency Model Context Protocol (MCP) server over Stdio for Glacier.Grep.
    /// </summary>
    public class McpServer
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string _logPath;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public McpServer()
        {
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_log.txt");
            Log("Grep MCP Server instance starting up.");

            // Setup stdio for JSON-RPC communication
            _reader = new StreamReader(Console.OpenStandardInput());
            _writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            _writer.NewLine = "\n";
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:T}] {message}\n");
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
                    root.TryGetProperty("id", out requestId);

                    switch (method)
                    {
                        case "initialize":
                            await SendResponseAsync(requestId, HandleInitialize());
                            break;

                        case "notifications/initialized":
                            Log("Client initialization confirmed.");
                            break;

                        case "tools/list":
                            await SendResponseAsync(requestId, HandleToolsList());
                            break;

                        case "tools/call":
                            if (root.TryGetProperty("params", out JsonElement parameters))
                            {
                                var result = await HandleToolCallAsync(parameters);
                                await SendResponseAsync(requestId, result);
                            }
                            else
                            {
                                await SendErrorAsync(requestId, -32602, "Parameters missing for tools/call");
                            }
                            break;

                        default:
                            Log($"Unhandle method: {method}");
                            if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                            {
                                await SendErrorAsync(requestId, -32601, $"Method '{method}' not implemented.");
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"CRITICAL error: {ex.Message}\n{ex.StackTrace}");
                    if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                    {
                        try { await SendErrorAsync(requestId, -32603, $"Internal Server Error: {ex.Message}"); } catch { }
                    }
                }
            }
        }

        private object HandleInitialize()
        {
            return new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "Glacier.Grep", version = "1.0.0" }
            };
        }

        private object HandleToolsList()
        {
            return new
            {
                tools = new[]
                {
                    new Dictionary<string, object>
                    {
                        { "name", "search_grep" },
                        { "description", "Searches the directory files utilizing high-performance SIMD pre-filtering or Regex zero-allocation matching." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "query", new Dictionary<string, object>
                                            {
                                                { "type", "string" },
                                                { "description", "The search query (literal string or regex pattern)." }
                                            }
                                        },
                                        { "path", new Dictionary<string, object>
                                            {
                                                { "type", "string" },
                                                { "description", "The base directory to start searching in. Defaults to the current workspace root." }
                                            }
                                        },
                                        { "isRegex", new Dictionary<string, object>
                                            {
                                                { "type", "boolean" },
                                                { "description", "Whether the query should be treated as a regular expression. Default: false." }
                                            }
                                        },
                                        { "caseSensitive", new Dictionary<string, object>
                                            {
                                                { "type", "boolean" },
                                                { "description", "Whether to perform case-sensitive search. Default: false." }
                                            }
                                        },
                                        { "contextLines", new Dictionary<string, object>
                                            {
                                                { "type", "integer" },
                                                { "description", "The number of lines of context before/after to include around matches. Default: 2." }
                                            }
                                        },
                                        { "fileGlobs", new Dictionary<string, object>
                                            {
                                                { "type", "array" },
                                                { "items", new Dictionary<string, object> { { "type", "string" } } },
                                                { "description", "Specific file glob patterns to search (e.g. ['*.cs', '*.json']). If not provided, searches all files." }
                                            }
                                        },
                                        { "searchHidden", new Dictionary<string, object>
                                            {
                                                { "type", "boolean" },
                                                { "description", "Search hidden files and folders. Default: false." }
                                            }
                                        },
                                        { "searchBinary", new Dictionary<string, object>
                                            {
                                                { "type", "boolean" },
                                                { "description", "Search binary files (do not skip them). Default: false." }
                                            }
                                        },
                                        { "invertMatch", new Dictionary<string, object>
                                            {
                                                { "type", "boolean" },
                                                { "description", "Invert the match: show lines that do NOT contain the pattern. Default: false." }
                                            }
                                        },
                                        { "maxDepth", new Dictionary<string, object>
                                            {
                                                { "type", "integer" },
                                                { "description", "Maximum recursion depth for folders. Default: unlimited." }
                                            }
                                        }
                                    }
                                },
                                { "required", new[] { "query" } }
                            }
                        }
                    }
                }
            };
        }

        private async Task<object> HandleToolCallAsync(JsonElement parameters)
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

                Log($"Starting search for query: '{query}' (isRegex: {isRegex}, caseSensitive: {caseSensitive}) in '{searchDir}'");
                
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
                    maxDepth
                );

                Log($"Search finished. Found {matches.Count} matches.");

                // Limit result size for LLM context compatibility
                const int maxResults = 250;
                if (matches.Count > maxResults)
                {
                    var truncated = matches.GetRange(0, maxResults);
                    string truncatedJson = JsonSerializer.Serialize(truncated, JsonOpts);
                    string summary = $"Showing first {maxResults} out of {matches.Count} matches found.\n\n" + truncatedJson;
                    return CreateToolResponse(summary);
                }
                else
                {
                    string resultsJson = JsonSerializer.Serialize(matches, JsonOpts);
                    return CreateToolResponse(resultsJson);
                }
            }

            throw new ArgumentException($"Tool '{toolName}' is not supported.");
        }

        private static object CreateToolResponse(string text)
        {
            return new
            {
                content = new[]
                {
                    new { type = "text", text = text }
                }
            };
        }

        private async Task SendResponseAsync(JsonElement id, object result)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["result"] = result
            };

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                response["id"] = id;
            }

            string json = JsonSerializer.Serialize(response, JsonOpts);
            Log($"Sending: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendErrorAsync(JsonElement id, int code, string message)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new { code, message }
            };

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                response["id"] = id;
            }

            string json = JsonSerializer.Serialize(response, JsonOpts);
            Log($"Sending error: {json}");
            await _writer.WriteLineAsync(json);
        }
    }
}
