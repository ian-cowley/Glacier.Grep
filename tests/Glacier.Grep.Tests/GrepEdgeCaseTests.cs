namespace Glacier.Grep.Tests;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Glacier.Grep;
using Xunit;

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
}
