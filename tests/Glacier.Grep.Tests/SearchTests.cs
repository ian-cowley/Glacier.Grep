using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Glacier.Grep;
using Xunit;

namespace Glacier.Grep.Tests
{
    public class SearchTests : IDisposable
    {
        private readonly string _tempDir;

        public SearchTests()
        {
            // Create a unique temporary directory inside the system temp path
            _tempDir = Path.Combine(Path.GetTempPath(), "GlacierGrepTests_" + Guid.NewGuid().ToString("N"));
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
        public void Test_GitIgnoreRule_DirectoryMatching()
        {
            var rule = new GitIgnoreRule("bin/");
            Assert.True(rule.Matches("bin/foo.txt", isDirectory: false));
            Assert.True(rule.Matches("src/bin/foo.txt", isDirectory: false));
            Assert.False(rule.Matches("bind/foo.txt", isDirectory: false));
        }

        [Fact]
        public void Test_GitIgnoreRule_ExtensionMatching()
        {
            var rule = new GitIgnoreRule("*.dll");
            Assert.True(rule.Matches("foo.dll", isDirectory: false));
            Assert.True(rule.Matches("src/bin/foo.dll", isDirectory: false));
            Assert.False(rule.Matches("foo.dll.txt", isDirectory: false));
        }

        [Fact]
        public void Test_GitIgnoreRule_RootedMatching()
        {
            var rule = new GitIgnoreRule("/src/bin/");
            Assert.True(rule.Matches("src/bin/foo.txt", isDirectory: false));
            Assert.False(rule.Matches("sub/src/bin/foo.txt", isDirectory: false));
        }

        [Fact]
        public void Test_GitIgnoreFile_ExclusionAndNegation()
        {
            var lines = new[]
            {
                "*.txt",
                "!important.txt",
                "bin/"
            };

            var gitignore = new GitIgnoreFile(_tempDir, lines);

            // Path must be absolute under _tempDir
            string normalFile = Path.Combine(_tempDir, "normal.txt");
            string importantFile = Path.Combine(_tempDir, "important.txt");
            string binFile = Path.Combine(_tempDir, "bin", "app.dll");
            string otherFile = Path.Combine(_tempDir, "other.csv");

            Assert.True(gitignore.IsIgnored(normalFile, isDirectory: false));
            Assert.False(gitignore.IsIgnored(importantFile, isDirectory: false));
            Assert.True(gitignore.IsIgnored(binFile, isDirectory: false));
            Assert.Null(gitignore.IsIgnored(otherFile, isDirectory: false));
        }

        [Fact]
        public async Task Test_SearchEngine_Literal_CaseSensitive()
        {
            string file1 = Path.Combine(_tempDir, "file1.txt");
            File.WriteAllText(file1, "Line 1\nHello World\nLine 3");

            string file2 = Path.Combine(_tempDir, "file2.txt");
            File.WriteAllText(file2, "line 1\nhello world\nline 3");

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync("Hello", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null);

            Assert.Single(results);
            Assert.Equal("Hello World", results[0].MatchContent);
            Assert.Equal(2, results[0].LineNumber);
            Assert.Equal("file1.txt", Path.GetFileName(results[0].FilePath));
        }

        [Fact]
        public async Task Test_SearchEngine_Literal_CaseInsensitive()
        {
            string file1 = Path.Combine(_tempDir, "file1.txt");
            File.WriteAllText(file1, "Line 1\nHello World\nLine 3");

            string file2 = Path.Combine(_tempDir, "file2.txt");
            File.WriteAllText(file2, "line 1\nhello world\nline 3");

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync("Hello", isRegex: false, caseSensitive: false, contextLines: 0, fileGlobs: null);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.MatchContent == "Hello World" && r.LineNumber == 2);
            Assert.Contains(results, r => r.MatchContent == "hello world" && r.LineNumber == 2);
        }

        [Fact]
        public async Task Test_SearchEngine_Regex_Search()
        {
            string file1 = Path.Combine(_tempDir, "file1.txt");
            File.WriteAllText(file1, "class CustomerService\n{\n    public int Id { get; set; }\n}");

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync(@"public\s+int\s+\w+", isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: null);

            Assert.Single(results);
            Assert.Equal("    public int Id { get; set; }", results[0].MatchContent);
            Assert.Equal(3, results[0].LineNumber);
            Assert.Equal(4, results[0].MatchStartIndex);
            Assert.Equal(13, results[0].MatchLength);
        }

        [Fact]
        public async Task Test_SearchEngine_ContextLines()
        {
            string file1 = Path.Combine(_tempDir, "file1.txt");
            File.WriteAllText(file1, "Line A\nLine B\nLine C\nMatch Here\nLine E\nLine F\nLine G");

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync("Match Here", isRegex: false, caseSensitive: true, contextLines: 2, fileGlobs: null);

            Assert.Single(results);
            var match = results[0];
            Assert.Equal("Match Here", match.MatchContent);
            Assert.Equal(4, match.LineNumber);

            Assert.Equal(2, match.ContextBefore.Count);
            Assert.Equal("Line B", match.ContextBefore[0]);
            Assert.Equal("Line C", match.ContextBefore[1]);

            Assert.Equal(2, match.ContextAfter.Count);
            Assert.Equal("Line E", match.ContextAfter[0]);
            Assert.Equal("Line F", match.ContextAfter[1]);
        }

        [Fact]
        public async Task Test_SearchEngine_InvertMatch()
        {
            string file1 = Path.Combine(_tempDir, "file1.txt");
            File.WriteAllText(file1, "Apples\nOranges\nBananas");

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync("Oranges", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: false, searchBinary: false, invertMatch: true);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.MatchContent == "Apples" && r.LineNumber == 1);
            Assert.Contains(results, r => r.MatchContent == "Bananas" && r.LineNumber == 3);
        }

        [Fact]
        public async Task Test_SearchEngine_MaxDepth()
        {
            string dir1 = Path.Combine(_tempDir, "level1");
            string dir2 = Path.Combine(dir1, "level2");
            Directory.CreateDirectory(dir2);

            File.WriteAllText(Path.Combine(_tempDir, "file0.txt"), "target");
            File.WriteAllText(Path.Combine(dir1, "file1.txt"), "target");
            File.WriteAllText(Path.Combine(dir2, "file2.txt"), "target");

            var engine = new SearchEngine(_tempDir);
            
            // maxDepth = 0 (only root dir)
            var results0 = await engine.SearchAsync("target", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: false, searchBinary: false, invertMatch: false, maxDepth: 0);
            Assert.Single(results0);
            Assert.Equal("file0.txt", Path.GetFileName(results0[0].FilePath));

            // maxDepth = 1 (root and level1)
            var results1 = await engine.SearchAsync("target", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: false, searchBinary: false, invertMatch: false, maxDepth: 1);
            Assert.Equal(2, results1.Count);
        }

        [Fact]
        public async Task Test_SearchEngine_SearchHidden()
        {
            string hiddenDir = Path.Combine(_tempDir, ".hidden_dir");
            Directory.CreateDirectory(hiddenDir);

            File.WriteAllText(Path.Combine(_tempDir, "file1.txt"), "target");
            File.WriteAllText(Path.Combine(hiddenDir, "file2.txt"), "target");

            var engine = new SearchEngine(_tempDir);

            // searchHidden = false (default)
            var resultsDefault = await engine.SearchAsync("target", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: false);
            Assert.Single(resultsDefault);
            Assert.Equal("file1.txt", Path.GetFileName(resultsDefault[0].FilePath));

            // searchHidden = true
            var resultsHidden = await engine.SearchAsync("target", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: true);
            Assert.Equal(2, resultsHidden.Count);
        }

        [Fact]
        public async Task Test_SearchEngine_SearchBinary()
        {
            string binFile = Path.Combine(_tempDir, "app.bin");
            // Write a binary structure with null bytes and search term
            byte[] binData = new byte[] { 0, 1, 2, 0, (byte)'t', (byte)'a', (byte)'r', (byte)'g', (byte)'e', (byte)'t', 0, 9 };
            File.WriteAllBytes(binFile, binData);

            var engine = new SearchEngine(_tempDir);

            // searchBinary = false (should skip binary file)
            var resultsDefault = await engine.SearchAsync("target", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: false, searchBinary: false);
            Assert.Empty(resultsDefault);

            // searchBinary = true (should search binary file)
            var resultsBinary = await engine.SearchAsync("target", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null, searchHidden: false, searchBinary: true);
            Assert.Single(resultsBinary);
            Assert.Equal("app.bin", Path.GetFileName(resultsBinary[0].FilePath));
        }

        [Fact]
        public void Test_HybridIoDispatcher_MediumFilePooled_ReadsDataCorrectly()
        {
            // 2 MB file (in the 1-8 MB medium range)
            int mediumFileSize = 2 * 1024 * 1024;
            string mediumFile = Path.Combine(_tempDir, "medium_test.dat");
            byte[] dummyData = new byte[mediumFileSize];
            Array.Fill(dummyData, (byte)'A');
            dummyData[100] = (byte)'X';
            dummyData[mediumFileSize - 10] = (byte)'Z';
            File.WriteAllBytes(mediumFile, dummyData);

            long length = new FileInfo(mediumFile).Length;
            Assert.Equal(mediumFileSize, length);

            bool processed = false;
            // Process file twice to verify handle pooling / reuse
            for (int run = 0; run < 2; run++)
            {
                HybridIoDispatcher.ProcessFile(mediumFile, length, (ReadOnlySpan<byte> span) =>
                {
                    Assert.Equal(mediumFileSize, span.Length);
                    Assert.Equal((byte)'X', span[100]);
                    Assert.Equal((byte)'Z', span[mediumFileSize - 10]);
                    processed = true;
                });
                Assert.True(processed);
            }
        }

        [Fact]
        public async Task Test_SearchEngine_MediumFileSearch_FindsTarget()
        {
            // Create a 2 MB text file with 30,000 lines
            string mediumTextFile = Path.Combine(_tempDir, "medium_source.txt");
            using (var writer = new StreamWriter(mediumTextFile))
            {
                for (int i = 1; i <= 30000; i++)
                {
                    if (i == 15432)
                    {
                        writer.WriteLine("Line 15432: TargetKeywordFoundHere in medium file!");
                    }
                    else
                    {
                        writer.WriteLine($"Line {i}: Normal dummy text content for medium file search testing.");
                    }
                }
            }

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync("TargetKeywordFoundHere", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: null);

            Assert.Single(results);
            Assert.Equal(15432, results[0].LineNumber);
            Assert.Contains("TargetKeywordFoundHere", results[0].MatchContent);
            Assert.Equal("medium_source.txt", Path.GetFileName(results[0].FilePath));
        }

        [Fact]
        public async Task Test_SearchEngine_ChunkBoundarySpan_FindsPatternCrossingBoundary()
        {
            int originalMaxChunk = HybridIoDispatcher.MaxChunkSize;
            try
            {
                // Set chunk size to 256 bytes to force chunking on small files
                HybridIoDispatcher.MaxChunkSize = 256;

                string filePath = Path.Combine(_tempDir, "boundary_crossing.txt");

                // Line 1: 250 bytes
                // Line 2 starts at 251: 4 'B's + TARGET (starts at 255, crossing 256 boundary into chunk 1)
                using (var stream = File.Create(filePath))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    writer.Write(new string('A', 249) + "\n");
                    writer.Write("BBBB" + "CROSS_BOUNDARY_TARGET" + "BBBB\n");
                    writer.Write(new string('C', 200) + "\n");
                }

                var engine = new SearchEngine(_tempDir);
                var results = await engine.SearchAsync("CROSS_BOUNDARY_TARGET", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: new[] { "boundary_crossing.txt" });

                Assert.Single(results);
                Assert.Equal(2, results[0].LineNumber);
                Assert.Contains("CROSS_BOUNDARY_TARGET", results[0].MatchContent);
            }
            finally
            {
                HybridIoDispatcher.MaxChunkSize = originalMaxChunk;
            }
        }

        [Fact]
        public async Task Test_SearchEngine_ChunkBoundary_PreservesMonotonicGlobalLineNumbers()
        {
            int originalMaxChunk = HybridIoDispatcher.MaxChunkSize;
            try
            {
                // Set chunk size to 128 bytes to create multiple chunks
                HybridIoDispatcher.MaxChunkSize = 128;

                string filePath = Path.Combine(_tempDir, "monotonic_lines.txt");

                using (var writer = new StreamWriter(filePath))
                {
                    for (int i = 1; i <= 30; i++)
                    {
                        if (i == 3)
                        {
                            writer.WriteLine($"Line {i}: KEYWORD_ALPHA in chunk 0");
                        }
                        else if (i == 12)
                        {
                            writer.WriteLine($"Line {i}: KEYWORD_BETA in middle chunk");
                        }
                        else if (i == 25)
                        {
                            writer.WriteLine($"Line {i}: KEYWORD_GAMMA in later chunk");
                        }
                        else
                        {
                            writer.WriteLine($"Line {i}: filler padding content for test");
                        }
                    }
                }

                var engine = new SearchEngine(_tempDir);

                var resultsAlpha = await engine.SearchAsync("KEYWORD_ALPHA", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: new[] { "monotonic_lines.txt" });
                Assert.Single(resultsAlpha);
                Assert.Equal(3, resultsAlpha[0].LineNumber);

                var resultsBeta = await engine.SearchAsync("KEYWORD_BETA", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: new[] { "monotonic_lines.txt" });
                Assert.Single(resultsBeta);
                Assert.Equal(12, resultsBeta[0].LineNumber);

                var resultsGamma = await engine.SearchAsync("KEYWORD_GAMMA", isRegex: false, caseSensitive: true, contextLines: 0, fileGlobs: new[] { "monotonic_lines.txt" });
                Assert.Single(resultsGamma);
                Assert.Equal(25, resultsGamma[0].LineNumber);
            }
            finally
            {
                HybridIoDispatcher.MaxChunkSize = originalMaxChunk;
            }
        }
    }
}
