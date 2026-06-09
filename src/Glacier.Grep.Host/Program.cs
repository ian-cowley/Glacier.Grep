using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Grep.Host.Mcp;

namespace Glacier.Grep.Host
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // If run with --mcp or no arguments, run as MCP server
            if (args.Length == 0 || args.Contains("--mcp", StringComparer.OrdinalIgnoreCase))
            {
                var server = new McpServer();
                Console.Error.WriteLine("Glacier.Grep MCP Server started.");
                Console.Error.WriteLine("Listening for JSON-RPC on stdin...");
                await server.RunAsync();
                return;
            }

            // Parse arguments for CLI search mode
            string query = args[0];
            string searchPath = Directory.GetCurrentDirectory();
            bool isRegex = false;
            bool caseSensitive = false;
            int contextLines = 0;
            var fileGlobs = new List<string>();
            bool searchHidden = false;
            bool searchBinary = false;
            bool invertMatch = false;
            int? maxDepth = null;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("-d", StringComparison.OrdinalIgnoreCase) || 
                    args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        searchPath = args[++i];
                    }
                }
                else if (args[i].Equals("-r", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--regex", StringComparison.OrdinalIgnoreCase))
                {
                    isRegex = true;
                }
                else if (args[i].Equals("-s", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--sensitive", StringComparison.OrdinalIgnoreCase))
                {
                    caseSensitive = true;
                }
                else if (args[i].Equals("-c", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--context", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int context))
                    {
                        contextLines = context;
                    }
                }
                else if (args[i].Equals("-g", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--globs", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        var globs = args[++i].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        fileGlobs.AddRange(globs);
                    }
                }
                else if (args[i].Equals("-h", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--hidden", StringComparison.OrdinalIgnoreCase))
                {
                    searchHidden = true;
                }
                else if (args[i].Equals("-t", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--text", StringComparison.OrdinalIgnoreCase))
                {
                    searchBinary = true;
                }
                else if (args[i].Equals("-v", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--invert", StringComparison.OrdinalIgnoreCase))
                {
                    invertMatch = true;
                }
                else if (args[i].Equals("-m", StringComparison.OrdinalIgnoreCase) || 
                         args[i].Equals("--max-depth", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int depth))
                    {
                        maxDepth = depth;
                    }
                }
            }

            if (!Directory.Exists(searchPath))
            {
                Console.Error.WriteLine($"Directory not found: {searchPath}");
                Environment.Exit(1);
            }

            var engine = new SearchEngine(searchPath);
            Console.WriteLine($"Searching for '{query}' in '{searchPath}'...");
            var start = DateTime.UtcNow;

            var results = await engine.SearchAsync(
                query, 
                isRegex, 
                caseSensitive, 
                contextLines, 
                fileGlobs.Count > 0 ? fileGlobs.ToArray() : null,
                searchHidden,
                searchBinary,
                invertMatch,
                maxDepth
            );
            var duration = DateTime.UtcNow - start;

            Console.WriteLine($"Found {results.Count} matches in {duration.TotalMilliseconds:F2}ms\n");

            foreach (var result in results)
            {
                // Print context before
                int contextBeforeStartLine = result.LineNumber - result.ContextBefore.Count;
                for (int idx = 0; idx < result.ContextBefore.Count; idx++)
                {
                    Console.WriteLine($"{result.FilePath}:{contextBeforeStartLine + idx}:  {result.ContextBefore[idx]}");
                }

                // Print the matched line
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"{result.FilePath}:{result.LineNumber}:> {result.MatchContent}");
                Console.ResetColor();

                // Print context after
                for (int idx = 0; idx < result.ContextAfter.Count; idx++)
                {
                    Console.WriteLine($"{result.FilePath}:{result.LineNumber + 1 + idx}:  {result.ContextAfter[idx]}");
                }

                if (contextLines > 0)
                {
                    Console.WriteLine(new string('-', 50));
                }
            }
        }
    }
}
