namespace Glacier.Grep.Tests;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Glacier.Grep;
using Xunit;

[Collection("MaxChunkSizeTests")]
public class GrepEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public GrepEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GlacierGrepEdgeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task SearchEngine_EmptyFile_ReturnsNoMatches()
    {
        string emptyFile = Path.Combine(_tempDir, "empty.txt");
        File.WriteAllText(emptyFile, "");

        var engine = new SearchEngine(_tempDir);
        var results = await engine.SearchAsync("test", isRegex: false, caseSensitive: false, contextLines: 0, fileGlobs: null);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchEngine_EmptyQuery_ReturnsNoMatches()
    {
        string file = Path.Combine(_tempDir, "data.txt");
        File.WriteAllText(file, "Some text content");

        var engine = new SearchEngine(_tempDir);
        var results = await engine.SearchAsync("", isRegex: false, caseSensitive: false, contextLines: 0, fileGlobs: null);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchEngine_LongLines_FindsMatchAccurately()
    {
        string file = Path.Combine(_tempDir, "longline.txt");
        var sb = new StringBuilder();
        sb.Append(new string('a', 5000));
        sb.Append("TARGET_KEYWORD");
        sb.Append(new string('z', 5000));
        File.WriteAllText(file, sb.ToString());

        var engine = new SearchEngine(_tempDir);
        var results = await engine.SearchAsync("TARGET_KEYWORD", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null);

        Assert.Single(results);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Contains("TARGET_KEYWORD", results[0].MatchContent);
    }

    [Fact]
    public async Task SearchEngine_MultipleMatchesPerFile_FindsAllOccurrences()
    {
        string file = Path.Combine(_tempDir, "multi.txt");
        File.WriteAllText(file, "token line 1\nno match\ntoken line 3\nno match\ntoken line 5");

        var engine = new SearchEngine(_tempDir);
        var results = await engine.SearchAsync("token", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].LineNumber);
        Assert.Equal(3, results[1].LineNumber);
        Assert.Equal(5, results[2].LineNumber);
    }

    [Fact]
    public async Task DirectoryTraverser_DoesNotSkipReleaseFolderOrFile()
    {
        string relDir = Path.Combine(_tempDir, "release");
        string docsDir = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(relDir);
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(Path.Combine(relDir, "notes.txt"), "SHIP_READY_TOKEN");
        File.WriteAllText(Path.Combine(docsDir, "RELEASE"), "SHIP_READY_TOKEN");

        var engine = new SearchEngine(_tempDir);
        var results = await engine.SearchAsync("SHIP_READY_TOKEN", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task McpServer_InitializeToolsListAndSearchGrep_WorkEndToEndWithoutReflection()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sample.cs"), "public class McpVerifiedTarget { }");
        string escapedPath = _tempDir.Replace("\\", "\\\\");

        string requests =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n" +
            $"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"McpVerifiedTarget\",\"path\":\"{escapedPath}\"}}}}}}\n";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        using var output = new MemoryStream();

        var server = new Glacier.Grep.Host.Mcp.McpServer(input, output);
        await server.RunAsync();

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"protocolVersion\":\"2024-11-05\"", lines[0]);
        Assert.Contains("\"search_grep\"", lines[1]);
        Assert.Contains("McpVerifiedTarget", lines[2]);
    }

    [Fact]
    public async Task SearchEngine_RegexAndInvertedAcrossChunkBoundary_NoDuplicatesOrTruncation()
    {
        string file = Path.Combine(_tempDir, "chunk_boundary.txt");
        var sb = new StringBuilder();
        for (int i = 1; i <= 40; i++)
        {
            sb.Append($"line_{i:D2}_marker_alpha_beta_gamma_1234567890\n");
        }
        File.WriteAllText(file, sb.ToString());

        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 256; // force many chunk boundaries mid-line
        try
        {
            var engine = new SearchEngine(_tempDir);
            var regexResults = await engine.SearchAsync(@"marker_alpha_beta_gamma_\d+", isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: null);
            Assert.Equal(40, regexResults.Count);
            for (int i = 0; i < 40; i++)
            {
                Assert.Equal(i + 1, regexResults[i].LineNumber);
            }

            var invResults = await engine.SearchAsync("NONEXISTENT_TOKEN_XYZ", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, invertMatch: true);
            Assert.Equal(40, invResults.Count);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }
}
