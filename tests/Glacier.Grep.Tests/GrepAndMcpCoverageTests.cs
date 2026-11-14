using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Glacier.Grep.Host.Mcp;
using Xunit;

namespace Glacier.Grep.Tests;

public class GrepAndMcpCoverageTests
{
    [Fact]
    public void GitIgnoreFile_CommentsNegationDirectoryAndRootedRules_WorkCorrectly()
    {
        var gitignore = new GitIgnoreFile(@"C:\repo", new[]
        {
            "# comment",
            "   ",
            "*.log",
            "!important.log",
            "build/",
            "/root_only.txt"
        });

        Assert.Equal(4, gitignore.Rules.Count);
        Assert.True(gitignore.IsIgnored(@"C:\repo\app.log", isDirectory: false));
        Assert.False(gitignore.IsIgnored(@"C:\repo\important.log", isDirectory: false));
        Assert.True(gitignore.IsIgnored(@"C:\repo\build", isDirectory: true));
        Assert.True(gitignore.IsIgnored(@"C:\repo\build\output.dll", isDirectory: false));
        Assert.True(gitignore.IsIgnored(@"C:\repo\root_only.txt", isDirectory: false));
        Assert.Null(gitignore.IsIgnored(@"C:\repo\src\root_only.txt", isDirectory: false));
        Assert.Null(gitignore.IsIgnored(@"C:\other\app.log", isDirectory: false));
    }

    [Fact]
    public void McpServer_NullStreams_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new McpServer(null!, new MemoryStream()));
        Assert.Throws<ArgumentNullException>(() => new McpServer(new MemoryStream(), null!));
    }

    [Fact]
    public async Task McpServer_InitializeToolsListUnknownMethodAndSearchGrep_RespondOverJsonRpc()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"glacier_grep_mcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.cs"), "line1\npublic class TargetWidget {}\nline3\n");

        try
        {
            string escapedPath = JsonEncodedText.Encode(tempDir).ToString();
            string requests = string.Join("\n", new[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"unknown/method\"}",
                $"{{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"TargetWidget\",\"path\":\"{escapedPath}\",\"contextLines\":1}}}}}}"
            }) + "\n";

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
            using var output = new MemoryStream();

            var server = new McpServer(input, output);
            await server.RunAsync();

            output.Position = 0;
            using var reader = new StreamReader(output, Encoding.UTF8);
            string allResponses = await reader.ReadToEndAsync();
            var lines = allResponses.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(5, lines.Length);
            Assert.Contains("Glacier.Grep", lines[0]);
            Assert.Contains("search_grep", lines[1]);
            Assert.Contains("-32602", lines[2]);
            Assert.Contains("-32601", lines[3]);
            Assert.Contains("TargetWidget", lines[4]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
