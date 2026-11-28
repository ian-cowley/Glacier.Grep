using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Grep.Host.Mcp;
using Xunit;

namespace Glacier.Grep.Tests;

[Collection("MaxChunkSizeTests")]
public sealed class HardeningTests : IDisposable
{
    private readonly string _tempDir;

    public HardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GlacierGrepHardening_" + Guid.NewGuid().ToString("N"));
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
    public async Task SearchRegex_CrossChunkNonNewlineBoundary_FindsMatchOnceWithExactLineNumberAndOffsets()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 4096; // Forces non-newline split once line > 4096 + 65536
        try
        {
            string file = Path.Combine(_tempDir, "cross_chunk_regex.txt");
            var sb = new StringBuilder();
            sb.Append("header_line_1\n");
            sb.Append("header_line_2\n");
            // Line 3: exceed 4096 + 65536 bytes so HybridIoDispatcher splits mid-line without a newline
            // First two lines are 28 bytes; place marker straddling byte 4096
            int padBefore = 4096 - 28 - "MARKER_".Length;
            sb.Append(new string('x', padBefore));
            sb.Append("MARKER_123456789");
            sb.Append(new string('y', 75_000));
            sb.Append(" café 🚀 MARKER_98765");
            sb.Append("\nline_4_after\n");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);
            var results = await engine.SearchAsync(@"MARKER_\d+", isRegex: true, caseSensitive: true, contextLines: 0);

            Assert.Equal(2, results.Count);
            Assert.All(results, r =>
            {
                Assert.Equal(3, r.LineNumber);
                Assert.Equal(3L, r.LongLineNumber);
            });
            Assert.Equal("MARKER_123456789", results[0].MatchContent.Substring(results[0].MatchStartIndex, results[0].MatchLength));
            Assert.Equal("MARKER_98765", results[1].MatchContent.Substring(results[1].MatchStartIndex, results[1].MatchLength));
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchRegex_MultilineCrossBoundaryAndSmallChunkBelowOverlap_NoDuplicatesAndExactLineNumbers()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 2048; // Smaller than 4096 overlapMargin so chunkStartOffset can be 0 on chunk 1
        try
        {
            string file = Path.Combine(_tempDir, "multiline_cross.txt");
            var sb = new StringBuilder();
            for (int i = 1; i <= 59; i++)
            {
                sb.Append($"line_{i:D3}_");
                sb.Append(new string('a', 60));
                sb.Append('\n');
            }
            // 59 * 70 = 4130 bytes; BEFORE_MULTILINE\n is 4130..4147, so CROSS_TOKEN straddles chunk 1's logicalEnd (4148)
            // while line 61 continues > 70 KB without a newline so chunk 1 splits mid-token.
            sb.Append("BEFORE_MULTILINE\n"); // line 60
            sb.Append("CROSS_TOKEN ");       // line 61 starts before boundary, token ends inside chunk 2's overlap
            sb.Append(new string('y', 72_000));
            sb.Append('\n');
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);

            // Match across line 60 -> 61 where line 61 spans chunks
            var multiResults = await engine.SearchAsync(@"BEFORE_MULTILINE\nCROSS_\w+", isRegex: true, caseSensitive: true, contextLines: 0);
            Assert.Single(multiResults);
            Assert.Equal(60, multiResults[0].LineNumber);
            Assert.Equal(60L, multiResults[0].LongLineNumber);

            // Simple regex when MaxChunkSize (2048) < overlapMargin (4096) must not duplicate lines in chunk 0
            var lineResults = await engine.SearchAsync(@"line_\d{3}", isRegex: true, caseSensitive: true, contextLines: 0);
            Assert.Equal(59, lineResults.Count);
            for (int i = 0; i < 59; i++)
            {
                Assert.Equal(i + 1, lineResults[i].LineNumber);
            }
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task MatchStartIndex_And_MatchLength_AreConsistentUtf16OffsetsAcrossAllModes()
    {
        string fileLf = Path.Combine(_tempDir, "utf8_lf.txt");
        string fileCrlf = Path.Combine(_tempDir, "utf8_crlf.txt");
        const string line = "café 🚀 target end";
        File.WriteAllText(fileLf, "intro\n" + line + "\noutro\n", new UTF8Encoding(false));
        File.WriteAllText(fileCrlf, "intro\r\n" + line + "\r\noutro\r\n", new UTF8Encoding(false));

        var engine = new SearchEngine(_tempDir);

        var litSensitive = await engine.SearchAsync("target", isRegex: false, caseSensitive: true);
        var litInsensitive = await engine.SearchAsync("TARGET", isRegex: false, caseSensitive: false);
        var regexPlain = await engine.SearchAsync(@"t[a-z]+t", isRegex: true, caseSensitive: true);
        var regexLowered = await engine.SearchAsync("target", isRegex: true, caseSensitive: false);

        foreach (var set in new[] { litSensitive, litInsensitive, regexPlain, regexLowered })
        {
            Assert.Equal(2, set.Count);
            foreach (var r in set)
            {
                Assert.Equal(2, r.LineNumber);
                Assert.Equal(2L, r.LongLineNumber);
                Assert.Equal(line, r.MatchContent);
                Assert.Equal("target", r.MatchContent.Substring(r.MatchStartIndex, r.MatchLength));
            }
        }

        var inverted = await engine.SearchAsync("NONEXISTENT", isRegex: false, caseSensitive: true, invertMatch: true);
        Assert.Equal(6, inverted.Count);
        foreach (var r in inverted)
        {
            Assert.Equal(0, r.MatchStartIndex);
            Assert.Equal(r.MatchContent.Length, r.MatchLength);
        }

        var matchRef = new MatchRef("abc"u8, 0, 3, (long)int.MaxValue + 500L);
        Assert.Equal(int.MaxValue, matchRef.LineNumber);
        Assert.Equal((long)int.MaxValue + 500L, matchRef.LongLineNumber);
    }

    [Fact]
    public async Task SearchInverted_LinesLargerThanMaxChunkSize_EvaluatesFullLineWithoutFalsePositivesOrSkipping()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 256;
        try
        {
            // Case 1: Pattern appears ONLY in chunk 1+ (> 70 KB into line 2) -> line 2 MUST NOT emit
            string matchingGiant = Path.Combine(_tempDir, "giant_match.txt");
            var sb1 = new StringBuilder();
            sb1.Append("line1_ok\n");
            sb1.Append(new string('a', 72_000));
            sb1.Append("FINDME_LATE");
            sb1.Append(new string('b', 1000));
            sb1.Append("\nline3_ok\n");
            File.WriteAllText(matchingGiant, sb1.ToString(), new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);

            // Literal case-sensitive, literal case-insensitive (m>1), literal (m==1), and FullRegex
            var rLitCs = await engine.SearchAsync("FINDME_LATE", isRegex: false, caseSensitive: true, contextLines: 1, fileGlobs: new[] { "giant_match.txt" }, invertMatch: true);
            var rLitCi = await engine.SearchAsync("findme_late", isRegex: false, caseSensitive: false, contextLines: 1, fileGlobs: new[] { "giant_match.txt" }, invertMatch: true);
            var rFullRe = await engine.SearchAsync(@"FINDME_\w+", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: new[] { "giant_match.txt" }, invertMatch: true);

            foreach (var res in new[] { rLitCs, rLitCi, rFullRe })
            {
                Assert.Equal(2, res.Count);
                Assert.Equal(1, res[0].LineNumber);
                Assert.Equal("line1_ok", res[0].MatchContent);
                Assert.Equal(3, res[1].LineNumber);
                Assert.Equal("line3_ok", res[1].MatchContent);
            }

            // Case 2: Pattern straddles exact chunk boundary (~256) in a > 70 KB line -> line 1 MUST NOT emit
            string straddleGiant = Path.Combine(_tempDir, "giant_straddle.txt");
            var sb2 = new StringBuilder();
            sb2.Append(new string('a', 252));
            sb2.Append("STRADDLE"); // bytes 252..259 across 256 boundary
            sb2.Append(new string('b', 72_000));
            sb2.Append("\ntail_line\n");
            File.WriteAllText(straddleGiant, sb2.ToString(), new UTF8Encoding(false));

            var rStraddle = await engine.SearchAsync("straddle", isRegex: false, caseSensitive: false, contextLines: 0, fileGlobs: new[] { "giant_straddle.txt" }, invertMatch: true);
            Assert.Single(rStraddle);
            Assert.Equal(2, rStraddle[0].LineNumber);
            Assert.Equal("tail_line", rStraddle[0].MatchContent);

            // Case 3: Pattern absent across entire > 70 KB line -> emits ONCE with full line content & context
            string absentGiant = Path.Combine(_tempDir, "giant_absent.txt");
            string giantLine = new string('p', 72_000);
            File.WriteAllText(absentGiant, "before_ctx\n" + giantLine + "\nafter_ctx\n", new UTF8Encoding(false));

            var rAbsent = await engine.SearchAsync("ABSENT_TOKEN", isRegex: false, caseSensitive: false, contextLines: 1, fileGlobs: new[] { "giant_absent.txt" }, invertMatch: true);
            Assert.Equal(3, rAbsent.Count);
            Assert.Equal(1, rAbsent[0].LineNumber);
            Assert.Equal(2, rAbsent[1].LineNumber);
            Assert.Equal(giantLine.Length, rAbsent[1].MatchContent.Length);
            Assert.Equal(giantLine.Length, rAbsent[1].MatchLength);
            Assert.Equal(new[] { "before_ctx" }, rAbsent[1].ContextBefore);
            Assert.Equal(new[] { "after_ctx" }, rAbsent[1].ContextAfter);
            Assert.Equal(3, rAbsent[2].LineNumber);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchEngine_MultiChunkBinaryFile_SkippedInAllChunksWhenSearchBinaryFalse()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 256;
        try
        {
            string binFile = Path.Combine(_tempDir, "large_binary.bin");
            byte[] data = new byte[1024];
            Array.Fill(data, (byte)'A');
            data[10] = 0; // Null byte in chunk 0
            Encoding.ASCII.GetBytes("HIDDEN_TARGET").CopyTo(data, 700); // In chunk 2+
            File.WriteAllBytes(binFile, data);

            var engine = new SearchEngine(_tempDir);
            var skipped = await engine.SearchAsync("HIDDEN_TARGET", searchBinary: false);
            Assert.Empty(skipped);

            var found = await engine.SearchAsync("HIDDEN_TARGET", searchBinary: true);
            Assert.Single(found);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchEngine_CancellationToken_CancelsCleanly()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sample.txt"), "hello world\n");
        var engine = new SearchEngine(_tempDir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.SearchAsync("hello", false, false, 0, null, false, false, false, null, cts.Token));
    }

    [Fact]
    public void HybridIoDispatcher_SlidingMmfViews_ReconstructsLargeFileByteForByte()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 100_000; // Non-64KB-aligned over > 1 MB file
        try
        {
            string largeFile = Path.Combine(_tempDir, "sliding_2mb.txt");
            var sb = new StringBuilder();
            int lineIdx = 0;
            while (sb.Length < 2_100_000)
            {
                sb.Append($"L{lineIdx:D6}_café_🚀_");
                sb.Append(new string((char)('a' + (lineIdx % 26)), 77));
                sb.Append('\n');
                lineIdx++;
            }
            byte[] expectedBytes = Encoding.UTF8.GetBytes(sb.ToString());
            File.WriteAllBytes(largeFile, expectedBytes);

            using var reconstructed = new MemoryStream();
            int chunkCount = 0;
            bool sawLastChunk = false;

            HybridIoDispatcher.ProcessFile(largeFile, expectedBytes.Length, 4096, (chunk, chunkStart, logicalStart, logicalEnd, totalLength, isLast) =>
            {
                Assert.Equal(expectedBytes.Length, totalLength);
                Assert.False(sawLastChunk);
                if (isLast) sawLastChunk = true;

                Assert.True(logicalStart >= chunkStart);
                Assert.True(logicalEnd > logicalStart);
                int logicalOffsetInChunk = checked((int)(logicalStart - chunkStart));
                int logicalLen = checked((int)(logicalEnd - logicalStart));
                reconstructed.Write(chunk.Slice(logicalOffsetInChunk, logicalLen));
                chunkCount++;
            });

            Assert.True(chunkCount >= 20);
            Assert.True(sawLastChunk);
            Assert.Equal(expectedBytes, reconstructed.ToArray());
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public void HybridIoDispatcher_StaleLengthAndZeroByteAndGrowthPromotion_HandledSafely()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 1024;
        try
        {
            // 1. Stale length == 0 on non-empty 256-byte file (e.g. symlink / growing file)
            string nonEmpty = Path.Combine(_tempDir, "stale_zero.txt");
            byte[] data256 = Enumerable.Range(0, 256).Select(i => (byte)('A' + (i % 26))).ToArray();
            File.WriteAllBytes(nonEmpty, data256);

            byte[]? readZeroHint = null;
            HybridIoDispatcher.ProcessFile(nonEmpty, 0, span => readZeroHint = span.ToArray());
            Assert.Equal(data256, readZeroHint);

            // 2. Stale smaller length on < MaxChunkSize file
            byte[]? readSmallHint = null;
            HybridIoDispatcher.ProcessFile(nonEmpty, 16, span => readSmallHint = span.ToArray());
            Assert.Equal(data256, readSmallHint);

            // 3. Stale larger length (file shrank)
            string small = Path.Combine(_tempDir, "shrank.txt");
            byte[] data64 = Encoding.ASCII.GetBytes(new string('Z', 64));
            File.WriteAllBytes(small, data64);

            byte[]? readLargeHint = null;
            HybridIoDispatcher.ProcessFile(small, 50_000, span => readLargeHint = span.ToArray());
            Assert.Equal(data64, readLargeHint);

            // 4. Stale small length on file larger than MaxChunkSize (routes to sliding MMF)
            string medium = Path.Combine(_tempDir, "grew_past_maxchunk.txt");
            byte[] data10k = Enumerable.Range(0, 10_000).Select(i => (byte)(i % 95 == 0 ? '\n' : 'M')).ToArray();
            File.WriteAllBytes(medium, data10k);

            using var ms = new MemoryStream();
            HybridIoDispatcher.ProcessFile(medium, 32, 64, (chunk, chunkStart, logicalStart, logicalEnd, _, _) =>
            {
                ms.Write(chunk.Slice((int)(logicalStart - chunkStart), (int)(logicalEnd - logicalStart)));
            });
            Assert.Equal(data10k, ms.ToArray());

            // 5. Genuine 0-byte file
            string empty = Path.Combine(_tempDir, "genuine_empty.txt");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            bool emptyCalled = false;
            HybridIoDispatcher.ProcessFile(empty, 100, 0, (chunk, _, _, _, total, isLast) =>
            {
                emptyCalled = true;
                Assert.True(chunk.IsEmpty);
                Assert.Equal(0, total);
                Assert.True(isLast);
            });
            Assert.True(emptyCalled);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchEngine_SymlinkTarget_IsSearchedEvenWhenEntryLengthIsZero()
    {
        string target = Path.Combine(_tempDir, "real_target.txt");
        string link = Path.Combine(_tempDir, "link_target.txt");
        File.WriteAllText(target, "SYMLINK_VERIFIED_TOKEN\n");

        bool symlinkCreated = false;
        try
        {
            File.CreateSymbolicLink(link, target);
            symlinkCreated = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        if (!symlinkCreated)
        {
            // Fallback verification: HybridIoDispatcher directly with length: 0 on real file
            byte[]? captured = null;
            HybridIoDispatcher.ProcessFile(target, 0, span => captured = span.ToArray());
            Assert.NotNull(captured);
            Assert.Contains("SYMLINK_VERIFIED_TOKEN", Encoding.UTF8.GetString(captured!));
            return;
        }

        var engine = new SearchEngine(_tempDir);
        var results = await engine.SearchAsync("SYMLINK_VERIFIED_TOKEN", fileGlobs: new[] { "link_target.txt" });
        Assert.Single(results);
        Assert.Equal("link_target.txt", Path.GetFileName(results[0].FilePath));
    }

    [Fact]
    public async Task SearchEngine_SimdCaseInsensitiveMultiByte_MatchesAcrossLengthsAndOverlaps()
    {
        string file = Path.Combine(_tempDir, "simd_ci.txt");
        var sb = new StringBuilder();
        sb.AppendLine("prefix aabaa mid AAbaa tail");
        sb.AppendLine("short ab");
        sb.AppendLine(new string('q', 80) + "_LongPattern_With_Digits_12345_And_Spaces_Here_" + new string('z', 40));
        sb.AppendLine("café_CAFÉ_needle");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));

        var engine = new SearchEngine(_tempDir);

        var r2 = await engine.SearchAsync("AB", isRegex: false, caseSensitive: false);
        Assert.Equal(3, r2.Count); // twice on line 1 (aabaa -> ab?), wait: aabaa has 'ab' once! Plus line 2 'ab' = 2? Wait: 'aabaa' has 'ab' once, 'AAbaa' has 'ab' once, line 2 has 'ab' once = 3!

        var rOverlap = await engine.SearchAsync("AABAA", isRegex: false, caseSensitive: false);
        Assert.Equal(2, rOverlap.Count);

        var rLong = await engine.SearchAsync("_longpattern_with_digits_12345_and_spaces_here_", isRegex: false, caseSensitive: false);
        Assert.Single(rLong);
        Assert.Equal(3, rLong[0].LineNumber);

        var rUtf8 = await engine.SearchAsync("CAFÉ_needle", isRegex: false, caseSensitive: false);
        Assert.Single(rUtf8);
        Assert.Equal(4, rUtf8[0].LineNumber);
    }

    [Fact]
    public async Task ClassifyPatternFull_MatchesNetRegexAcrossAllClassesCrlfAndInvertModes()
    {
        string corpus = string.Join("", new[]
        {
            "alpha beta gamma\n",
            "alpha beta gamma\r\n",
            "ALPHA middle GAMMA\n",
            "foo1foo2bar1bar2\r\n",
            "exact_only\n",
            "exact_only\r\n",
            "café 🚀 start_mid_end\n",
            "\n",
            "\r\n",
            "tail_no_newline"
        });

        string file = Path.Combine(_tempDir, "parity.txt");
        File.WriteAllText(file, corpus, new UTF8Encoding(false));
        var engine = new SearchEngine(_tempDir);

        string[] patterns =
        [
            "beta",
            "^alpha",
            "gamma$",
            "^exact_only$",
            "^alpha.*gamma$",
            "foo.*bar",
            ".*middle.*",
            ".*gamma",
            "alpha.*",
            "^alpha.*",
            ".*gamma$",
            "^$",
            "^",
            "$",
            @"foo\d+",
            "café"
        ];

        string[] linesRaw = corpus.Split('\n');

        foreach (string pattern in patterns)
        {
            foreach (bool caseSensitive in new[] { false, true })
            {
                foreach (bool invertMatch in new[] { false, true })
                {
                    var engineResults = await engine.SearchAsync(
                        pattern,
                        isRegex: true,
                        caseSensitive: caseSensitive,
                        contextLines: 0,
                        fileGlobs: new[] { "parity.txt" },
                        invertMatch: invertMatch);

                    var options = RegexOptions.Multiline | RegexOptions.NonBacktracking;
                    if (!caseSensitive) options |= RegexOptions.IgnoreCase;
                    var referenceRegex = new Regex(pattern, options);

                    if (!invertMatch)
                    {
                        var expected = new List<(int LineNumber, string Content, int Start, int Length)>();
                        foreach (ValueMatch m in referenceRegex.EnumerateMatches(corpus))
                        {
                            int lineStart = corpus.AsSpan(0, m.Index).LastIndexOf('\n') + 1;
                            int relLineEnd = corpus.AsSpan(m.Index).IndexOf('\n');
                            int lineEnd = relLineEnd < 0 ? corpus.Length : m.Index + relLineEnd;
                            ReadOnlySpan<char> lineSpan = corpus.AsSpan(lineStart, lineEnd - lineStart);
                            if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                                lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                            int lineNum = corpus.AsSpan(0, lineStart).Count('\n') + 1;
                            int relStart = Math.Clamp(m.Index - lineStart, 0, lineSpan.Length);
                            int relLen = Math.Clamp(m.Length, 0, lineSpan.Length - relStart);
                            expected.Add((lineNum, new string(lineSpan), relStart, relLen));
                        }

                        var actual = engineResults.Select(r => (r.LineNumber, r.MatchContent, r.MatchStartIndex, r.MatchLength)).ToList();
                        Assert.Equal(expected, actual);
                    }
                    else
                    {
                        var expected = new List<(int LineNumber, string Content)>();
                        for (int idx = 0; idx < linesRaw.Length; idx++)
                        {
                            // Match SearchInverted trailing-\n handling: empty segment after final \n is not a line
                            if (idx == linesRaw.Length - 1 && linesRaw[idx].Length == 0)
                                continue;

                            string rawLine = linesRaw[idx];
                            string cleanLine = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
                            if (!referenceRegex.IsMatch(rawLine))
                            {
                                expected.Add((idx + 1, cleanLine));
                            }
                        }

                        var actual = engineResults.Select(r => (r.LineNumber, r.MatchContent)).ToList();
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task McpServer_VersionAndStrictJsonRpcValidation_ReturnExpectedCodes()
    {
        File.WriteAllText(Path.Combine(_tempDir, "mcp_target.txt"), "hello mcp\n");
        string escapedPath = JsonEncodedText.Encode(_tempDir).ToString();

        string requests = string.Join("\n", new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
            "{not valid json",
            "[1,2,3]",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":123}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":null}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"unknown_tool\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"search_grep\",\"arguments\":\"bad\"}}",
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"search_grep\",\"arguments\":{}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"hello\",\"path\":\"{escapedPath}\",\"isRegex\":\"true\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"hello\",\"path\":\"{escapedPath}\",\"contextLines\":-1}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"hello\",\"path\":\"{escapedPath}\",\"maxDepth\":\"two\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"hello\",\"path\":\"{escapedPath}\",\"fileGlobs\":\"*.txt\"}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"hello\",\"path\":\"{escapedPath}\",\"fileGlobs\":[123]}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"[unclosed\",\"path\":\"{escapedPath}\",\"isRegex\":true}}}}}}",
            $"{{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"hello\",\"path\":null,\"maxDepth\":null,\"fileGlobs\":null}}}}}}"
        }) + "\n";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        using var output = new MemoryStream();

        var server = new McpServer(input, output);
        await server.RunAsync();

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(16, lines.Length);

        using (var initDoc = JsonDocument.Parse(lines[0]))
        {
            Assert.Equal("1.1.1", initDoc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());
        }

        AssertErrorCode(lines[1], -32700);
        AssertErrorCode(lines[2], -32600);
        AssertErrorCode(lines[3], -32600);
        for (int i = 4; i <= 14; i++)
        {
            AssertErrorCode(lines[i], -32602);
        }

        // id 14: explicit nulls for path/maxDepth/fileGlobs succeed without error
        using (var okDoc = JsonDocument.Parse(lines[15]))
        {
            Assert.True(okDoc.RootElement.TryGetProperty("result", out _));
            Assert.False(okDoc.RootElement.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task McpServer_NonBacktrackingUnsupportedRegexes_ReturnInvalidParamsMinus32602()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sample.txt"), "aaabbb\n");
        string escapedPath = JsonEncodedText.Encode(_tempDir).ToString();

        string[] unsupportedPatterns = ["(?=a)", "(?!a)", "(?<=a)", @"(a)\\1", "(?>a+)"];
        var requestLines = new List<string>();
        int id = 1;
        foreach (string pat in unsupportedPatterns)
        {
            requestLines.Add($"{{\"jsonrpc\":\"2.0\",\"id\":{id++},\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"{pat}\",\"path\":\"{escapedPath}\",\"isRegex\":true,\"caseSensitive\":true}}}}}}");
            requestLines.Add($"{{\"jsonrpc\":\"2.0\",\"id\":{id++},\"method\":\"tools/call\",\"params\":{{\"name\":\"search_grep\",\"arguments\":{{\"query\":\"{pat}\",\"path\":\"{escapedPath}\",\"isRegex\":true,\"caseSensitive\":false}}}}}}");
        }

        string requests = string.Join("\n", requestLines) + "\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        using var output = new MemoryStream();

        var server = new McpServer(input, output);
        await server.RunAsync();

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(requestLines.Count, lines.Length);
        foreach (string line in lines)
        {
            AssertErrorCode(line, -32602);
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(4096)]
    public async Task SearchEngine_CrossChunkContextBefore_PreservesExactPrecedingLinesAcrossAllModes(int chunkSize)
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = chunkSize;
        try
        {
            string file = Path.Combine(_tempDir, $"ctx_cross_{chunkSize}.txt");
            var lines = new List<string>(100);
            for (int i = 0; i < 100; i++)
            {
                if (i == 47)
                {
                    lines.Add($"L{i:D3}_@LITERAL_TARGET_END");
                }
                else
                {
                    lines.Add($"L{i:D3}_filler_{new string('x', 70)}");
                }
            }
            File.WriteAllText(file, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);
            string[] expectedBefore = [lines[44], lines[45], lines[46]];
            string[] expectedAfter = [lines[48], lines[49], lines[50]];

            // SearchLiteral (m == 1 and m > 1, case-sensitive and case-insensitive)
            var litM1 = await engine.SearchAsync("@", isRegex: false, caseSensitive: true, contextLines: 3);
            var litCs = await engine.SearchAsync("LITERAL_TARGET", isRegex: false, caseSensitive: true, contextLines: 3);
            var litCi = await engine.SearchAsync("literal_target", isRegex: false, caseSensitive: false, contextLines: 3);

            // SearchRegex (FullRegex) and SearchClassifiedRegex (StartsWith/EndsWith/ContainsBothOrdered)
            var fullRe = await engine.SearchAsync(@"LITERAL_\w+", isRegex: true, caseSensitive: true, contextLines: 3);
            var startsWith = await engine.SearchAsync("^L047_@", isRegex: true, caseSensitive: true, contextLines: 3);
            var endsWith = await engine.SearchAsync("LITERAL_TARGET_END$", isRegex: true, caseSensitive: true, contextLines: 3);
            var bothOrdered = await engine.SearchAsync("L047_.*TARGET_END", isRegex: true, caseSensitive: true, contextLines: 3);

            foreach (var set in new[] { litM1, litCs, litCi, fullRe, startsWith, endsWith, bothOrdered })
            {
                Assert.Single(set);
                Assert.Equal(48, set[0].LineNumber);
                Assert.Equal(lines[47], set[0].MatchContent);
                Assert.Equal(expectedBefore, set[0].ContextBefore);
                Assert.Equal(expectedAfter, set[0].ContextAfter);
            }

            // SearchInverted across chunk boundaries with contextLines = 3
            var inverted = await engine.SearchAsync("LITERAL_TARGET", isRegex: false, caseSensitive: true, contextLines: 3, invertMatch: true);
            Assert.Equal(99, inverted.Count);
            var line49Inverted = inverted.Single(r => r.LineNumber == 49);
            Assert.Equal(new[] { lines[45], lines[46], lines[47] }, line49Inverted.ContextBefore);
            Assert.Equal(new[] { lines[49], lines[50], lines[51] }, line49Inverted.ContextAfter);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(4096)]
    public async Task SearchEngine_GiantLineExceedingChunkPlusLookahead_PreservesFullLineOffsetsContextAndEol(int chunkSize)
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = chunkSize;
        try
        {
            string giantFile = Path.Combine(_tempDir, $"giant_{chunkSize}.txt");
            var line3Builder = new StringBuilder();
            line3Builder.Append("GIANT_START_");
            line3Builder.Append(new string('a', 37));
            line3Builder.Append(" TOK_12345678901 "); // 15-char token starting at offset 50
            line3Builder.Append(new string('b', 65_520));
            line3Builder.Append(" é中🚀TOK_CROSS_65K_77 ");
            line3Builder.Append(new string('c', 5_000));
            line3Builder.Append("_GIANT_END");
            string giantLine = line3Builder.ToString();

            File.WriteAllText(
                giantFile,
                "ctx_before_1\nctx_before_2\n" + giantLine + "\nctx_after_1\nctx_after_2\n",
                new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);

            // 1. SearchRegex with multiple tokens (including non-ASCII prefix & cross-boundary token)
            var tokResults = await engine.SearchAsync(@"TOK_\S+", isRegex: true, caseSensitive: true, contextLines: 2, fileGlobs: [$"giant_{chunkSize}.txt"]);
            Assert.Equal(2, tokResults.Count);
            Assert.Equal(giantLine.Length, tokResults[0].MatchContent.Length);
            Assert.Equal(giantLine.Length, tokResults[1].MatchContent.Length);
            Assert.Equal(50, tokResults[0].MatchStartIndex);
            Assert.Equal(15, tokResults[0].MatchLength);
            Assert.Equal("TOK_12345678901", tokResults[0].MatchContent.Substring(tokResults[0].MatchStartIndex, tokResults[0].MatchLength));
            Assert.Equal(giantLine.IndexOf("TOK_CROSS_65K_77", StringComparison.Ordinal), tokResults[1].MatchStartIndex);
            Assert.Equal("TOK_CROSS_65K_77", tokResults[1].MatchContent.Substring(tokResults[1].MatchStartIndex, tokResults[1].MatchLength));
            foreach (var r in tokResults)
            {
                Assert.Equal(3, r.LineNumber);
                Assert.Equal(new[] { "ctx_before_1", "ctx_before_2" }, r.ContextBefore);
                Assert.Equal(new[] { "ctx_after_1", "ctx_after_2" }, r.ContextAfter);
            }

            var utf8Tok = await engine.SearchAsync(@"é中🚀TOK_CROSS_65K_\d+", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: [$"giant_{chunkSize}.txt"]);
            Assert.Single(utf8Tok);
            Assert.Equal(giantLine.Length, utf8Tok[0].MatchContent.Length);
            Assert.Equal(giantLine.IndexOf("é中🚀TOK_CROSS_65K_77", StringComparison.Ordinal), utf8Tok[0].MatchStartIndex);
            Assert.Equal("é中🚀TOK_CROSS_65K_77", utf8Tok[0].MatchContent.Substring(utf8Tok[0].MatchStartIndex, utf8Tok[0].MatchLength));

            // 2. ContextAfter on normal line preceding a giant line must emit the full giant line, not a truncated chunk slice
            var beforeGiant = await engine.SearchAsync("ctx_before_2", isRegex: false, caseSensitive: true, contextLines: 2, fileGlobs: [$"giant_{chunkSize}.txt"]);
            Assert.Single(beforeGiant);
            Assert.Equal(new[] { giantLine, "ctx_after_1" }, beforeGiant[0].ContextAfter);

            // 3. SearchLiteral on giant line (case-sensitive and case-insensitive)
            var litCross = await engine.SearchAsync("tok_cross_65k_77", isRegex: false, caseSensitive: false, contextLines: 2, fileGlobs: [$"giant_{chunkSize}.txt"]);
            Assert.Single(litCross);
            Assert.Equal(3, litCross[0].LineNumber);
            Assert.Equal(giantLine.Length, litCross[0].MatchContent.Length);
            Assert.Equal(giantLine.IndexOf("TOK_CROSS_65K_77", StringComparison.Ordinal), litCross[0].MatchStartIndex);
            Assert.Equal(new[] { "ctx_before_1", "ctx_before_2" }, litCross[0].ContextBefore);
            Assert.Equal(new[] { "ctx_after_1", "ctx_after_2" }, litCross[0].ContextAfter);

            // 4. SearchClassifiedRegex on giant line (StartsWith, EndsWith, PrefixAndSuffix, ContainsBothOrdered)
            var sw = await engine.SearchAsync("^GIANT_START_", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: [$"giant_{chunkSize}.txt"]);
            var ew = await engine.SearchAsync("_GIANT_END$", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: [$"giant_{chunkSize}.txt"]);
            var ps = await engine.SearchAsync("^GIANT_START_.*_GIANT_END$", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: [$"giant_{chunkSize}.txt"]);
            var cb = await engine.SearchAsync("TOK_12345678901.*TOK_CROSS_65K_77", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: [$"giant_{chunkSize}.txt"]);

            Assert.Single(sw);
            Assert.Equal(giantLine.Length, sw[0].MatchContent.Length);
            Assert.Equal(0, sw[0].MatchStartIndex);
            Assert.Equal("GIANT_START_", sw[0].MatchContent.Substring(sw[0].MatchStartIndex, sw[0].MatchLength));

            Assert.Single(ew);
            Assert.Equal(giantLine.Length, ew[0].MatchContent.Length);
            Assert.Equal(giantLine.Length - "_GIANT_END".Length, ew[0].MatchStartIndex);
            Assert.Equal("_GIANT_END", ew[0].MatchContent.Substring(ew[0].MatchStartIndex, ew[0].MatchLength));

            Assert.Single(ps);
            Assert.Equal(giantLine.Length, ps[0].MatchContent.Length);
            Assert.Equal(0, ps[0].MatchStartIndex);
            Assert.Equal(giantLine.Length, ps[0].MatchLength);

            Assert.Single(cb);
            Assert.Equal(giantLine.Length, cb[0].MatchContent.Length);
            Assert.Equal(50, cb[0].MatchStartIndex);
            Assert.Equal(giantLine.IndexOf("TOK_CROSS_65K_77", StringComparison.Ordinal) + "TOK_CROSS_65K_77".Length - 50, cb[0].MatchLength);

            // 5. $ and ^ anchors on a 70,000-char single-line file without trailing newline (no false chunk-boundary EOL/BOL)
            string noNewlineGiant = Path.Combine(_tempDir, $"all_b_{chunkSize}.txt");
            string allB = new string('b', 70_000);
            File.WriteAllText(noNewlineGiant, allB, new UTF8Encoding(false));

            var eolMatches = await engine.SearchAsync("$", isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: [$"all_b_{chunkSize}.txt"]);
            Assert.Single(eolMatches);
            Assert.Equal(1, eolMatches[0].LineNumber);
            Assert.Equal(70_000, eolMatches[0].MatchContent.Length);
            Assert.Equal(70_000, eolMatches[0].MatchStartIndex);
            Assert.Equal(0, eolMatches[0].MatchLength);

            var bolAnchored = await engine.SearchAsync("^b{1,20}", isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: [$"all_b_{chunkSize}.txt"]);
            Assert.Single(bolAnchored);
            Assert.Equal(1, bolAnchored[0].LineNumber);
            Assert.Equal(70_000, bolAnchored[0].MatchContent.Length);
            Assert.Equal(0, bolAnchored[0].MatchStartIndex);
            Assert.Equal(20, bolAnchored[0].MatchLength);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchEngine_GiantLineClosingInFinalChunk_PreservesGiantLineInContextBeforeAcrossAllModes()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        HybridIoDispatcher.MaxChunkSize = 100_000;
        try
        {
            string file = Path.Combine(_tempDir, "giant_closes_in_last_chunk.txt");
            string giantLine = new string('a', 170_000);
            File.WriteAllText(
                file,
                "ctx1\nctx2\n" + giantLine + "\nTARGET_AFTER\n",
                new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);
            string[] expectedBefore = ["ctx2", giantLine];

            // 1. SearchLiteral
            var litResults = await engine.SearchAsync("TARGET_AFTER", isRegex: false, caseSensitive: true, contextLines: 2);
            Assert.Single(litResults);
            Assert.Equal(4, litResults[0].LineNumber);
            Assert.Equal(expectedBefore, litResults[0].ContextBefore);

            // 2. SearchRegex (FullRegex)
            var regexResults = await engine.SearchAsync(@"TARGET_\w+", isRegex: true, caseSensitive: true, contextLines: 2);
            Assert.Single(regexResults);
            Assert.Equal(4, regexResults[0].LineNumber);
            Assert.Equal(expectedBefore, regexResults[0].ContextBefore);

            // 3. SearchClassifiedRegex (Equals)
            var classifiedResults = await engine.SearchAsync("^TARGET_AFTER$", isRegex: true, caseSensitive: true, contextLines: 2);
            Assert.Single(classifiedResults);
            Assert.Equal(4, classifiedResults[0].LineNumber);
            Assert.Equal(expectedBefore, classifiedResults[0].ContextBefore);

            // 4. SearchInverted
            var invertedResults = await engine.SearchAsync("NEVER_MATCH_TOKEN", isRegex: false, caseSensitive: true, contextLines: 2, invertMatch: true);
            Assert.Equal(4, invertedResults.Count);
            var targetLine = invertedResults.Single(r => r.LineNumber == 4);
            Assert.Equal("TARGET_AFTER", targetLine.MatchContent);
            Assert.Equal(expectedBefore, targetLine.ContextBefore);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(4096)]
    public async Task SearchRegex_MidLineChunkStartWithStartAnchor_NoFalsePositivesAndPreservesTrueLineStart(int chunkSize)
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        try
        {
            // Case 1: Normal ~1.2 KB lines where chunkStartOffset > 0 lands mid-line (re_mid_anchor_normal)
            string normalFile = Path.Combine(_tempDir, $"mid_anchor_normal_{chunkSize}.txt");
            var normalSb = new StringBuilder();
            while (normalSb.Length < 6100)
            {
                normalSb.Append("FILLER_LINE_0123456789abcdef\n");
            }
            normalSb.Append("qqqq");
            normalSb.Append(new string('m', 1200));
            normalSb.Append("\nCONT_");
            normalSb.Append(new string('r', 1200));
            normalSb.Append("\nTAIL_LINE\n");
            File.WriteAllText(normalFile, normalSb.ToString(), new UTF8Encoding(false));

            // Case 2: Giant >70 KB open line where chunkStartOffset > 0 lands mid-line (re_mid_anchor_m)
            string giantFile = Path.Combine(_tempDir, $"mid_anchor_giant_{chunkSize}.txt");
            var giantSb = new StringBuilder();
            while (giantSb.Length < 6100)
            {
                giantSb.Append("FILLER_LINE_0123456789abcdef\n");
            }
            giantSb.Append("qqqq");
            giantSb.Append(new string('m', 70_000));
            giantSb.Append("\nCONT_");
            giantSb.Append(new string('r', 1200));
            giantSb.Append("\nTAIL_LINE\n");
            File.WriteAllText(giantFile, giantSb.ToString(), new UTF8Encoding(false));

            HybridIoDispatcher.MaxChunkSize = chunkSize;
            var engine = new SearchEngine(_tempDir);

            foreach (string glob in new[] { $"mid_anchor_normal_{chunkSize}.txt", $"mid_anchor_giant_{chunkSize}.txt" })
            {
                var res1 = await engine.SearchAsync(@"^m+\nCONT_r+\n", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: [glob]);
                Assert.Empty(res1);

                var res2 = await engine.SearchAsync(@"^m+\nCONT_r{1,50}", isRegex: true, caseSensitive: true, contextLines: 3, fileGlobs: [glob]);
                Assert.Empty(res2);
            }

            // Case 3: True '\n'-preceded chunkStartOffset > 0 still matches '^' and rejects '\A'
            if (chunkSize == 4096)
            {
                string trueStartFile = Path.Combine(_tempDir, "true_line_start_at_chunk_start.txt");
                var trueSb = new StringBuilder();
                trueSb.Append(new string('x', 2048));
                trueSb.Append('\n'); // byte 2048 -> chunkStartOffset of chunk 1 is 2049, preceded by '\n'
                trueSb.Append(new string('m', 2047));
                trueSb.Append('\n'); // byte 4096 -> chunk 0 lookahead ends at 4097
                trueSb.Append("CONT_");
                trueSb.Append(new string('r', 70_000));
                trueSb.Append("\nTAIL_LINE\n");
                File.WriteAllText(trueStartFile, trueSb.ToString(), new UTF8Encoding(false));

                var trueAnchorResults = await engine.SearchAsync(@"^m+\nCONT_r{1,50}", isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: ["true_line_start_at_chunk_start.txt"]);
                Assert.Single(trueAnchorResults);
                Assert.Equal(2, trueAnchorResults[0].LineNumber);

                var fileStartAnchorResults = await engine.SearchAsync(@"\Am+\nCONT_r{1,50}", isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: ["true_line_start_at_chunk_start.txt"]);
                Assert.Empty(fileStartAnchorResults);

                // Case 4: ^\s* and ^\s+ at '\n'-preceded chunkStartOffset > 0 (Probe A) vs mid-line rejection
                string wsStartFile = Path.Combine(_tempDir, "ws_line_start_at_chunk_start.txt");
                var wsSb = new StringBuilder();
                wsSb.Append(new string('x', 2048));
                wsSb.Append('\n'); // byte 2048 -> chunkStartOffset of chunk 1 is 2049, preceded by '\n'
                wsSb.Append("  ");
                wsSb.Append(new string('m', 2045));
                wsSb.Append('\n');
                wsSb.Append("CONT_");
                wsSb.Append(new string('r', 70_000));
                wsSb.Append("\nTAIL_LINE\n");
                File.WriteAllText(wsStartFile, wsSb.ToString(), new UTF8Encoding(false));

                foreach (string wsPat in new[] { @"^\s*m+\nCONT_r{1,50}", @"^\s+m+\nCONT_r{1,50}" })
                {
                    var wsTrue = await engine.SearchAsync(wsPat, isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: ["ws_line_start_at_chunk_start.txt"]);
                    Assert.Single(wsTrue);
                    Assert.Equal(2, wsTrue[0].LineNumber);
                    Assert.Equal(0, wsTrue[0].MatchStartIndex);

                    var wsMidRejected = await engine.SearchAsync(wsPat, isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: [$"mid_anchor_normal_{chunkSize}.txt"]);
                    Assert.Empty(wsMidRejected);
                }
            }
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(4096)]
    public async Task SearchRegex_GreedyUnanchoredTokensSpanningChunkStart_AreNotDropped(int chunkSize)
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        try
        {
            // 1. Two tokens spanning chunk boundaries on a 74.5 KB line (Challenger mid_token reproducer)
            string twoTokensFile = Path.Combine(_tempDir, $"mid_token_two_{chunkSize}.txt");
            string twoTokensContent =
                new string(' ', 50) + "qqq" + new string('m', 150) +
                new string(' ', 1800) + "qqq" + new string('m', 2500) +
                new string(' ', 70_000) + "\n";
            File.WriteAllText(twoTokensFile, twoTokensContent, new UTF8Encoding(false));

            // 2. Single 75,000-char run of 'm's (Reviewer Probe C reproducer)
            string giantRunFile = Path.Combine(_tempDir, $"mid_token_75k_{chunkSize}.txt");
            File.WriteAllText(giantRunFile, "HEADER\n" + new string('m', 75_000) + "\nTAIL\n", new UTF8Encoding(false));

            // 3. Space-prefixed 75,000-char line with lowercase token spanning chunk boundaries
            string spacePrefixFile = Path.Combine(_tempDir, $"mid_token_spaces_{chunkSize}.txt");
            File.WriteAllText(spacePrefixFile, new string(' ', 10) + new string('m', 75_000) + "\n", new UTF8Encoding(false));

            HybridIoDispatcher.MaxChunkSize = chunkSize;
            var engine = new SearchEngine(_tempDir);

            foreach (string pat in new[] { @"\w+", @"m+", @"[a-z]+" })
            {
                var twoMatches = await engine.SearchAsync(pat, isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: [$"mid_token_two_{chunkSize}.txt"]);
                Assert.Equal(2, twoMatches.Count);
                Assert.All(twoMatches, r =>
                {
                    Assert.Equal(1, r.LineNumber);
                    Assert.Equal(twoTokensContent.Length - 1, r.MatchContent.Length);
                });

                var giantMatch = await engine.SearchAsync(pat, isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: [$"mid_token_75k_{chunkSize}.txt"]);
                if (pat == @"\w+")
                {
                    // Matches HEADER (line 1), m*75000 (line 2), and TAIL (line 3)
                    Assert.Equal(3, giantMatch.Count);
                    Assert.Equal(2, giantMatch[1].LineNumber);
                    Assert.Equal(75_000, giantMatch[1].MatchContent.Length);
                }
                else
                {
                    Assert.Single(giantMatch);
                    Assert.Equal(2, giantMatch[0].LineNumber);
                    Assert.Equal(75_000, giantMatch[0].MatchContent.Length);
                }

                var spaceMatch = await engine.SearchAsync(pat, isRegex: true, caseSensitive: true, contextLines: 0, fileGlobs: [$"mid_token_spaces_{chunkSize}.txt"]);
                Assert.Single(spaceMatch);
                Assert.Equal(1, spaceMatch[0].LineNumber);
                Assert.Equal(75_010, spaceMatch[0].MatchContent.Length);
            }
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchRegex_ChunkStartWordBoundaryAndMidLinePrefix_HandlesSpaceRocketEmdashAndContext()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        try
        {
            // At MaxChunkSize = 128, overlap = 64.
            // Line ends at offset 143 ('\n'), so chunk 1 starts at chunkStartOffset = 144 - 64 = 80 (exact start of 63 'm's).
            string spaceFile = Path.Combine(_tempDir, "wb_space.txt");
            string spaceLine = new string('a', 76) + "    " + new string('m', 63);
            File.WriteAllText(spaceFile, spaceLine + "\nCONT_TAIL\n", new UTF8Encoding(false));

            // Preceded by 4-byte supplementary UTF-8 non-word '🚀': 76 'a' + 4-byte '🚀' = 80 bytes -> chunkStartOffset = 80!
            string rocketFile = Path.Combine(_tempDir, "wb_rocket.txt");
            string rocketLine = new string('a', 76) + "🚀" + new string('m', 63);
            File.WriteAllText(rocketFile, rocketLine + "\nCONT_TAIL\n", new UTF8Encoding(false));

            // Preceded by 3-byte BMP UTF-8 non-word '—' (U+2014): 77 'a' + 3-byte '—' = 80 bytes -> chunkStartOffset = 80!
            string emdashFile = Path.Combine(_tempDir, "wb_emdash.txt");
            string emdashLine = new string('a', 77) + "—" + new string('m', 63);
            File.WriteAllText(emdashFile, emdashLine + "\nCONT_TAIL\n", new UTF8Encoding(false));

            // Preceded by 2-byte UTF-8 word letter 'é': 78 'a' + 2-byte 'é' = 80 bytes -> \bm+ MUST NOT match!
            string letterFile = Path.Combine(_tempDir, "wb_letter.txt");
            string letterLine = new string('a', 78) + "é" + new string('m', 63);
            File.WriteAllText(letterFile, letterLine + "\nCONT_TAIL\n", new UTF8Encoding(false));

            // Also verify non-empty ContextBefore on a preceding-lines space boundary
            string spaceWithCtxFile = Path.Combine(_tempDir, "wb_space_ctx.txt");
            File.WriteAllText(spaceWithCtxFile, "BEFORE_1\nBEFORE_2\n" + spaceLine + "\nCONT_TAIL\n", new UTF8Encoding(false));

            HybridIoDispatcher.MaxChunkSize = 128;
            var engine = new SearchEngine(_tempDir);

            var rSpace = await engine.SearchAsync(@"\bm+\nCONT", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: ["wb_space.txt"]);
            Assert.Single(rSpace);
            Assert.Equal(1, rSpace[0].LineNumber);
            Assert.Equal(spaceLine, rSpace[0].MatchContent);
            Assert.Equal(80, rSpace[0].MatchStartIndex);
            Assert.Equal(63, rSpace[0].MatchLength);
            Assert.Empty(rSpace[0].ContextBefore);
            Assert.Equal(new[] { "CONT_TAIL" }, rSpace[0].ContextAfter);

            var rRocket = await engine.SearchAsync(@"\bm+\nCONT", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: ["wb_rocket.txt"]);
            Assert.Single(rRocket);
            Assert.Equal(1, rRocket[0].LineNumber);
            Assert.Equal(rocketLine, rRocket[0].MatchContent);
            Assert.Equal(78, rRocket[0].MatchStartIndex);
            Assert.Equal(63, rRocket[0].MatchLength);
            Assert.Equal(new[] { "CONT_TAIL" }, rRocket[0].ContextAfter);

            var rEmdash = await engine.SearchAsync(@"\bm+\nCONT", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: ["wb_emdash.txt"]);
            Assert.Single(rEmdash);
            Assert.Equal(1, rEmdash[0].LineNumber);
            Assert.Equal(emdashLine, rEmdash[0].MatchContent);
            Assert.Equal(78, rEmdash[0].MatchStartIndex);
            Assert.Equal(63, rEmdash[0].MatchLength);
            Assert.Equal(new[] { "CONT_TAIL" }, rEmdash[0].ContextAfter);

            var rLetter = await engine.SearchAsync(@"\bm+\nCONT", isRegex: true, caseSensitive: true, contextLines: 1, fileGlobs: ["wb_letter.txt"]);
            Assert.Empty(rLetter);

            var rSpaceCtx = await engine.SearchAsync(@"\bm+\nCONT", isRegex: true, caseSensitive: true, contextLines: 2, fileGlobs: ["wb_space_ctx.txt"]);
            Assert.Single(rSpaceCtx);
            Assert.Equal(3, rSpaceCtx[0].LineNumber);
            Assert.Equal(spaceLine, rSpaceCtx[0].MatchContent);
            Assert.Equal(80, rSpaceCtx[0].MatchStartIndex);
            Assert.Equal(new[] { "BEFORE_1", "BEFORE_2" }, rSpaceCtx[0].ContextBefore);
            Assert.Equal(new[] { "CONT_TAIL" }, rSpaceCtx[0].ContextAfter);

            // Also verify SearchLiteral multi-line match straddling chunk boundary when lineStart == 0 and chunkStartOffset > 0 is mid-line
            string litMultiFile = Path.Combine(_tempDir, "lit_multiline_midline.txt");
            string litLine = new string('a', 136) + "    mmm";
            File.WriteAllText(litMultiFile, "BEFORE_LIT\n" + litLine + "\nCONT_TAIL\n", new UTF8Encoding(false));

            var rLitMulti = await engine.SearchAsync("mmm\nCONT", isRegex: false, caseSensitive: true, contextLines: 1, fileGlobs: ["lit_multiline_midline.txt"]);
            Assert.Single(rLitMulti);
            Assert.Equal(2, rLitMulti[0].LineNumber);
            Assert.Equal(litLine, rLitMulti[0].MatchContent);
            Assert.Equal(140, rLitMulti[0].MatchStartIndex);
            Assert.Equal(3, rLitMulti[0].MatchLength);
            Assert.Equal(new[] { "BEFORE_LIT" }, rLitMulti[0].ContextBefore);
            Assert.Equal(new[] { "CONT_TAIL" }, rLitMulti[0].ContextAfter);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchInverted_CrlfEndAnchorParity_FormsExactPartitionWithForwardSearch()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        try
        {
            string crlfFile = Path.Combine(_tempDir, "crlf_partition.txt");
            const string content = "foo\nfoo\r\nbar\n";
            File.WriteAllText(crlfFile, content, new UTF8Encoding(false));

            // Also test a giant >70 KB CRLF line at small MaxChunkSize to exercise PartialLineBuffer path
            string giantCrlfFile = Path.Combine(_tempDir, "crlf_giant_partition.txt");
            string giantPrefix = new string('x', 72_000);
            string giantContent = giantPrefix + "foo\n" + giantPrefix + "foo\r\n" + giantPrefix + "bar\n";
            File.WriteAllText(giantCrlfFile, giantContent, new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);
            int[] allLines = [1, 2, 3];

            foreach (var (glob, chunkSize, isGiant) in new[] { ("crlf_partition.txt", 16 * 1024 * 1024, false), ("crlf_giant_partition.txt", 256, true) })
            {
                HybridIoDispatcher.MaxChunkSize = chunkSize;
                string[] patterns = isGiant
                    ? ["foo$", "^x.*foo$", "^X.*FOO$"]
                    : ["foo$", "^foo$", "^FOO$"];

                foreach (string pattern in patterns)
                {
                    foreach (bool caseSensitive in pattern == pattern.ToUpperInvariant() || pattern.Contains("FOO") ? new[] { false } : new[] { true, false })
                    {
                        var fwd = await engine.SearchAsync(pattern, isRegex: true, caseSensitive: caseSensitive, contextLines: 0, fileGlobs: [glob], invertMatch: false);
                        var inv = await engine.SearchAsync(pattern, isRegex: true, caseSensitive: caseSensitive, contextLines: 0, fileGlobs: [glob], invertMatch: true);

                        int[] fwdLines = fwd.Select(r => r.LineNumber).ToArray();
                        int[] invLines = inv.Select(r => r.LineNumber).ToArray();

                        // Only line 1 ends with "foo\n"; line 2 ends with "foo\r\n" so '$' does not match before '\r' in .NET RegexOptions.Multiline
                        Assert.Equal(new[] { 1 }, fwdLines);
                        Assert.Equal(new[] { 2, 3 }, invLines);
                        Assert.Empty(fwdLines.Intersect(invLines));
                        Assert.Equal(allLines, fwdLines.Union(invLines).OrderBy(x => x).ToArray());
                        Assert.DoesNotContain(inv, r => r.MatchContent.EndsWith('\r'));
                    }
                }
            }
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    [Fact]
    public async Task SearchMultiLineOverlap_WithContextLines_RecoversFullContextBeforeAcrossChunkBoundary()
    {
        int origMax = HybridIoDispatcher.MaxChunkSize;
        try
        {
            // Case 1 (Bug 5: lineStart == 0 && precedingByte == '\n', linesSkipped == 3):
            // 119 + 1 + 9 = 129 bytes before t4; t4 (40) + l5 (44) + l6 (44) = 128 bytes -> chunk 0 logicalEnd = 257,
            // chunk 1 chunkStartOffset = 257 - 128 = 129 (right after "B3\n").
            string t4 = "L4_" + new string('a', 36) + "\n";
            string l5 = "L5_" + new string('b', 40) + "\n";
            string l6 = "L6_" + new string('c', 40) + "\n";
            string case1Content = new string('z', 119) + "\nB1\nB2\nB3\n" + t4 + l5 + l6 + "NEXT_7\nTAIL\n";
            string case1File = Path.Combine(_tempDir, "ctx_overlap_preceding_nl.txt");
            File.WriteAllText(case1File, case1Content, new UTF8Encoding(false));

            // CRLF variant of Case 1 (exact chunk boundary at byte 257, chunkStartOffset = 129 right after "B3\r\n")
            string t4Crlf = "L4_" + new string('a', 35) + "\r\n";
            string l5Crlf = "L5_" + new string('b', 39) + "\r\n";
            string l6Crlf = "L6_" + new string('c', 39) + "\r\n";
            string case1CrlfContent = new string('z', 115) + "\r\nB1\r\nB2\r\nB3\r\n" + t4Crlf + l5Crlf + l6Crlf + "NEXT_7\r\nTAIL\r\n";
            string case1CrlfFile = Path.Combine(_tempDir, "ctx_overlap_preceding_nl_crlf.txt");
            File.WriteAllText(case1CrlfFile, case1CrlfContent, new UTF8Encoding(false));

            // Case 2 (Bug 5: 0 < lineStart < minLookbackOffset, linesSkipped == 1):
            // "B1\nB2\nB3\n" (9) + 190 'p' + "\n" (191) + lineA (57) = 257 -> chunk 1 starts mid-line inside p*190.
            string lineA = "LINE_A_" + new string('a', 49) + "\n";
            string p190 = new string('p', 190);
            string case2Content = "B1\nB2\nB3\n" + p190 + "\n" + lineA + "LINE_B\nTAIL\n";
            string case2File = Path.Combine(_tempDir, "ctx_midline_2line.txt");
            File.WriteAllText(case2File, case2Content, new UTF8Encoding(false));

            // CRLF variant of Case 2
            string case2CrlfContent = case2Content.Replace("\n", "\r\n");
            string case2CrlfFile = Path.Combine(_tempDir, "ctx_midline_2line_crlf.txt");
            File.WriteAllText(case2CrlfFile, case2CrlfContent, new UTF8Encoding(false));

            // Case 3: Fewer than contextLines preceding lines in file while lineStart < minLookbackOffset
            string shortPrefixContent = p190 + "\n" + lineA + "LINE_B\nTAIL\n";
            string shortPrefixFile = Path.Combine(_tempDir, "ctx_midline_short_prefix.txt");
            File.WriteAllText(shortPrefixFile, shortPrefixContent, new UTF8Encoding(false));

            var engine = new SearchEngine(_tempDir);

            // Compare MaxChunkSize = 256 against 16 MB oracle for both regex and literal modes
            string case1Pattern = t4 + l5 + l6 + "NEXT_7";
            string case2Pattern = lineA + "LINE_B";
            string case1CrlfPattern = t4Crlf + l5Crlf + l6Crlf + "NEXT_7";
            string case2CrlfPattern = case2Pattern.Replace("\n", "\r\n");

            foreach (bool isRegex in new[] { true, false })
            {
                HybridIoDispatcher.MaxChunkSize = 16 * 1024 * 1024;
                var oracle1 = await engine.SearchAsync(case1Pattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_overlap_preceding_nl.txt"]);
                var oracle2 = await engine.SearchAsync(case2Pattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_midline_2line.txt"]);
                var oracle1Crlf = await engine.SearchAsync(case1CrlfPattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_overlap_preceding_nl_crlf.txt"]);
                var oracle2Crlf = await engine.SearchAsync(case2CrlfPattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_midline_2line_crlf.txt"]);
                var oracleShort = await engine.SearchAsync(case2Pattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_midline_short_prefix.txt"]);

                HybridIoDispatcher.MaxChunkSize = 256;
                var small1 = await engine.SearchAsync(case1Pattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_overlap_preceding_nl.txt"]);
                var small2 = await engine.SearchAsync(case2Pattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_midline_2line.txt"]);
                var small1Crlf = await engine.SearchAsync(case1CrlfPattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_overlap_preceding_nl_crlf.txt"]);
                var small2Crlf = await engine.SearchAsync(case2CrlfPattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_midline_2line_crlf.txt"]);
                var smallShort = await engine.SearchAsync(case2Pattern, isRegex: isRegex, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_midline_short_prefix.txt"]);

                AssertSingleMatchParity(oracle1, small1, new[] { "B1", "B2", "B3" }, new[] { l5.TrimEnd('\n'), l6.TrimEnd('\n'), "NEXT_7" });
                AssertSingleMatchParity(oracle2, small2, new[] { "B2", "B3", p190 }, new[] { "LINE_B", "TAIL" });
                AssertSingleMatchParity(oracle1Crlf, small1Crlf, new[] { "B1", "B2", "B3" }, new[] { l5Crlf.TrimEnd('\r', '\n'), l6Crlf.TrimEnd('\r', '\n'), "NEXT_7" });
                AssertSingleMatchParity(oracle2Crlf, small2Crlf, new[] { "B2", "B3", p190 }, new[] { "LINE_B", "TAIL" });
                AssertSingleMatchParity(oracleShort, smallShort, new[] { p190 }, new[] { "LINE_B", "TAIL" });
            }

            // Also verify classified regex at '\n'-preceded chunkStartOffset with precedingByte forwarding
            HybridIoDispatcher.MaxChunkSize = 256;
            var classified = await engine.SearchAsync("^L4_" + new string('a', 36) + "$", isRegex: true, caseSensitive: true, contextLines: 3, fileGlobs: ["ctx_overlap_preceding_nl.txt"]);
            Assert.Single(classified);
            Assert.Equal(5, classified[0].LineNumber);
            Assert.Equal(new[] { "B1", "B2", "B3" }, classified[0].ContextBefore);
            Assert.Equal(new[] { l5.TrimEnd('\n'), l6.TrimEnd('\n'), "NEXT_7" }, classified[0].ContextAfter);
        }
        finally
        {
            HybridIoDispatcher.MaxChunkSize = origMax;
        }
    }

    private static void AssertSingleMatchParity(
        List<SearchResult> oracle,
        List<SearchResult> actual,
        string[] expectedContextBefore,
        string[] expectedContextAfter)
    {
        Assert.Single(oracle);
        Assert.Single(actual);
        Assert.Equal(oracle[0].LineNumber, actual[0].LineNumber);
        Assert.Equal(oracle[0].MatchStartIndex, actual[0].MatchStartIndex);
        Assert.Equal(oracle[0].MatchLength, actual[0].MatchLength);
        Assert.Equal(oracle[0].MatchContent, actual[0].MatchContent);
        Assert.Equal(expectedContextBefore, oracle[0].ContextBefore);
        Assert.Equal(expectedContextBefore, actual[0].ContextBefore);
        Assert.Equal(expectedContextAfter, oracle[0].ContextAfter);
        Assert.Equal(expectedContextAfter, actual[0].ContextAfter);
    }

    private static void AssertErrorCode(string jsonLine, int expectedCode)
    {
        using var doc = JsonDocument.Parse(jsonLine);
        int code = doc.RootElement.GetProperty("error").GetProperty("code").GetInt32();
        Assert.Equal(expectedCode, code);
        Assert.NotEqual(-32603, code);
    }
}
