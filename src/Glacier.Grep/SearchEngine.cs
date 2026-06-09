using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Glacier.Grep
{
    /// <summary>
    /// Orchestrates high-performance, concurrent directory searching.
    /// Uses Channels for a work-stealing producer-consumer pipeline and SIMD-accelerated matching.
    /// </summary>
    public class SearchEngine
    {
        private readonly string _rootDir;

        public SearchEngine(string rootDir)
        {
            _rootDir = Path.GetFullPath(rootDir);
        }

        public async Task<List<SearchResult>> SearchAsync(
            string query,
            bool isRegex = false,
            bool caseSensitive = false,
            int contextLines = 0,
            string[]? fileGlobs = null,
            bool searchHidden = false,
            bool searchBinary = false,
            bool invertMatch = false,
            int? maxDepth = null)
        {
            var results = new List<SearchResult>();

            // Setup a bounded channel to queue discovered files
            var channel = Channel.CreateBounded<FileSearchTask>(new BoundedChannelOptions(2048)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            // Start directory enumeration task (Producer)
            var traverser = new DirectoryTraverser(_rootDir, searchHidden, maxDepth);
            var producerTask = Task.Run(() =>
            {
                try
                {
                    foreach (var fileTask in traverser.EnumerateFiles())
                    {
                        // Optional filter by file glob patterns
                        if (fileGlobs != null && fileGlobs.Length > 0)
                        {
                            bool matchesGlob = false;
                            string fileName = Path.GetFileName(fileTask.FullPath);
                            foreach (var glob in fileGlobs)
                            {
                                if (FileSystemName.MatchesSimpleExpression(glob, fileName, ignoreCase: true))
                                {
                                    matchesGlob = true;
                                    break;
                                }
                            }
                            if (!matchesGlob) continue;
                        }

                        channel.Writer.WriteAsync(fileTask).AsTask().GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            });

            // Prepare search parameters
            byte[] patternBytes = Encoding.UTF8.GetBytes(query);
            Regex? regex = null;

            if (isRegex)
            {
                var options = RegexOptions.Multiline | RegexOptions.NonBacktracking;
                if (!caseSensitive)
                {
                    options |= RegexOptions.IgnoreCase;
                }
                regex = new Regex(query, options);
            }

            // Create SearchValues pre-filter for the first byte of the pattern
            SearchValues<byte>? searchBytes = null;
            if (!isRegex && patternBytes.Length > 0)
            {
                if (caseSensitive)
                {
                    searchBytes = SearchValues.Create(new[] { patternBytes[0] });
                }
                else
                {
                    byte b = patternBytes[0];
                    byte lower = b;
                    byte upper = b;
                    if (b >= 'a' && b <= 'z') upper = (byte)(b - 32);
                    else if (b >= 'A' && b <= 'Z') lower = (byte)(b + 32);

                    searchBytes = SearchValues.Create(new[] { lower, upper });
                }
            }

            // Start search consumers (Workers)
            int workerCount = Environment.ProcessorCount;
            var workerTasks = new Task[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                workerTasks[i] = Task.Run(async () =>
                {
                    var reader = channel.Reader;
                    while (await reader.WaitToReadAsync())
                    {
                        while (reader.TryRead(out var task))
                        {
                            try
                            {
                                SearchFile(task, patternBytes, regex, searchBytes, caseSensitive, contextLines, searchBinary, invertMatch, results);
                            }
                            catch
                            {
                                // Ignore errors for individual files (e.g. sharing violations)
                            }
                        }
                    }
                });
            }

            // Wait for workers and producer to complete
            await Task.WhenAll(workerTasks);
            await producerTask;

            return results;
        }

        private void SearchFile(
            FileSearchTask task,
            byte[] patternBytes,
            Regex? regex,
            SearchValues<byte>? searchBytes,
            bool caseSensitive,
            int contextLines,
            bool searchBinary,
            bool invertMatch,
            List<SearchResult> results)
        {
            HybridIoDispatcher.ProcessFile(task.FullPath, task.Length, (ReadOnlySpan<byte> fileData) =>
            {
                if (fileData.Length == 0) return;

                // Skip binary files unless searchBinary is true
                if (!searchBinary && IsBinaryFile(fileData)) return;

                if (invertMatch)
                {
                    SearchInverted(fileData, task, patternBytes, regex, caseSensitive, contextLines, results);
                }
                else if (regex != null)
                {
                    SearchRegex(fileData, task, regex, contextLines, results);
                }
                else
                {
                    SearchLiteral(fileData, task, patternBytes, searchBytes, caseSensitive, contextLines, results);
                }
            });
        }

        private static bool IsBinaryFile(ReadOnlySpan<byte> data)
        {
            int checkLen = Math.Min(data.Length, 1024);
            return data.Slice(0, checkLen).Contains((byte)0);
        }

        private void SearchLiteral(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            byte[] patternBytes,
            SearchValues<byte>? searchBytes,
            bool caseSensitive,
            int contextLines,
            List<SearchResult> results)
        {
            if (patternBytes.Length == 0) return;

            int offset = 0;
            while (offset < fileData.Length)
            {
                // SIMD-accelerated check for the first byte of search term
                int matchIndex = fileData.Slice(offset).IndexOfAny(searchBytes!);
                if (matchIndex < 0) break;

                offset += matchIndex;

                // Validate the complete pattern matches
                bool isMatch;
                if (caseSensitive)
                {
                    isMatch = fileData.Slice(offset).StartsWith(patternBytes);
                }
                else
                {
                    isMatch = EqualsIgnoreCaseAscii(fileData.Slice(offset), patternBytes);
                }

                if (isMatch)
                {
                    // Establish line start and line end offsets
                    int lineStart = fileData.Slice(0, offset).LastIndexOf((byte)'\n') + 1;
                    int lineEnd = fileData.Slice(offset).IndexOf((byte)'\n');
                    if (lineEnd < 0) lineEnd = fileData.Length;
                    else lineEnd += offset;

                    ReadOnlySpan<byte> lineSpan = fileData.Slice(lineStart, lineEnd - lineStart);
                    if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                        lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                    int lineNumber = CountNewlines(fileData.Slice(0, lineStart)) + 1;

                    // Extract context lines
                    var contextBefore = GetContextBefore(fileData, lineStart, contextLines);
                    var contextAfter = GetContextAfter(fileData, lineEnd, contextLines);

                    var result = new SearchResult
                    {
                        FilePath = task.RelativePath,
                        LineNumber = lineNumber,
                        MatchContent = Encoding.UTF8.GetString(lineSpan),
                        MatchStartIndex = offset - lineStart,
                        MatchLength = patternBytes.Length,
                        ContextBefore = contextBefore,
                        ContextAfter = contextAfter
                    };

                    lock (results)
                    {
                        results.Add(result);
                    }

                    offset += patternBytes.Length;
                }
                else
                {
                    offset += 1;
                }
            }
        }

        private void SearchRegex(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            Regex regex,
            int contextLines,
            List<SearchResult> results)
        {
            int offset = 0;
            int lineNumber = 1;
            int maxLineLength = 16384;
            char[] charBuffer = ArrayPool<char>.Shared.Rent(maxLineLength);

            try
            {
                while (offset < fileData.Length)
                {
                    int lineEnd = fileData.Slice(offset).IndexOf((byte)'\n');
                    int nextOffset;
                    int currentLineEnd;
                    if (lineEnd < 0)
                    {
                        currentLineEnd = fileData.Length;
                        nextOffset = fileData.Length;
                    }
                    else
                    {
                        currentLineEnd = offset + lineEnd;
                        nextOffset = currentLineEnd + 1;
                    }

                    ReadOnlySpan<byte> lineSpan = fileData.Slice(offset, currentLineEnd - offset);
                    if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                        lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                    int requiredChars = Encoding.UTF8.GetMaxCharCount(lineSpan.Length);
                    if (requiredChars > charBuffer.Length)
                    {
                        ArrayPool<char>.Shared.Return(charBuffer);
                        charBuffer = ArrayPool<char>.Shared.Rent(requiredChars);
                    }

                    int charCount = Encoding.UTF8.GetChars(lineSpan, charBuffer);
                    ReadOnlySpan<char> charSpan = charBuffer.AsSpan(0, charCount);

                    var enumerator = regex.EnumerateMatches(charSpan);
                    bool lineHasMatch = false;
                    int firstMatchIndex = 0;
                    int firstMatchLength = 0;

                    while (enumerator.MoveNext())
                    {
                        if (!lineHasMatch)
                        {
                            lineHasMatch = true;
                            firstMatchIndex = enumerator.Current.Index;
                            firstMatchLength = enumerator.Current.Length;
                        }
                    }

                    if (lineHasMatch)
                    {
                        var contextBefore = GetContextBefore(fileData, offset, contextLines);
                        var contextAfter = GetContextAfter(fileData, currentLineEnd, contextLines);

                        var result = new SearchResult
                        {
                            FilePath = task.RelativePath,
                            LineNumber = lineNumber,
                            MatchContent = new string(charSpan),
                            MatchStartIndex = firstMatchIndex,
                            MatchLength = firstMatchLength,
                            ContextBefore = contextBefore,
                            ContextAfter = contextAfter
                        };

                        lock (results)
                        {
                            results.Add(result);
                        }
                    }

                    lineNumber++;
                    offset = nextOffset;
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }

        private void SearchInverted(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            byte[] patternBytes,
            Regex? regex,
            bool caseSensitive,
            int contextLines,
            List<SearchResult> results)
        {
            int offset = 0;
            int lineNumber = 1;
            int maxLineLength = 16384;
            char[] charBuffer = ArrayPool<char>.Shared.Rent(maxLineLength);

            try
            {
                while (offset < fileData.Length)
                {
                    int lineEnd = fileData.Slice(offset).IndexOf((byte)'\n');
                    int nextOffset;
                    int currentLineEnd;
                    if (lineEnd < 0)
                    {
                        currentLineEnd = fileData.Length;
                        nextOffset = fileData.Length;
                    }
                    else
                    {
                        currentLineEnd = offset + lineEnd;
                        nextOffset = currentLineEnd + 1;
                    }

                    ReadOnlySpan<byte> lineSpan = fileData.Slice(offset, currentLineEnd - offset);
                    if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                        lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                    bool hasMatch = false;

                    if (regex != null)
                    {
                        int requiredChars = Encoding.UTF8.GetMaxCharCount(lineSpan.Length);
                        if (requiredChars > charBuffer.Length)
                        {
                            ArrayPool<char>.Shared.Return(charBuffer);
                            charBuffer = ArrayPool<char>.Shared.Rent(requiredChars);
                        }

                        int charCount = Encoding.UTF8.GetChars(lineSpan, charBuffer);
                        ReadOnlySpan<char> charSpan = charBuffer.AsSpan(0, charCount);

                        hasMatch = regex.IsMatch(charSpan);
                    }
                    else if (patternBytes.Length > 0)
                    {
                        if (caseSensitive)
                        {
                            hasMatch = lineSpan.IndexOf(patternBytes) >= 0;
                        }
                        else
                        {
                            hasMatch = IndexOfIgnoreCaseAscii(lineSpan, patternBytes) >= 0;
                        }
                    }

                    if (!hasMatch)
                    {
                        var contextBefore = GetContextBefore(fileData, offset, contextLines);
                        var contextAfter = GetContextAfter(fileData, currentLineEnd, contextLines);

                        var result = new SearchResult
                        {
                            FilePath = task.RelativePath,
                            LineNumber = lineNumber,
                            MatchContent = Encoding.UTF8.GetString(lineSpan),
                            MatchStartIndex = 0,
                            MatchLength = lineSpan.Length,
                            ContextBefore = contextBefore,
                            ContextAfter = contextAfter
                        };

                        lock (results)
                        {
                            results.Add(result);
                        }
                    }

                    lineNumber++;
                    offset = nextOffset;
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }

        private static bool EqualsIgnoreCaseAscii(ReadOnlySpan<byte> span, ReadOnlySpan<byte> pattern)
        {
            if (span.Length < pattern.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                byte b1 = span[i];
                byte b2 = pattern[i];
                if (b1 != b2)
                {
                    if (b1 >= 'A' && b1 <= 'Z') b1 = (byte)(b1 + 32);
                    if (b2 >= 'A' && b2 <= 'Z') b2 = (byte)(b2 + 32);
                    if (b1 != b2) return false;
                }
            }
            return true;
        }

        private static int IndexOfIgnoreCaseAscii(ReadOnlySpan<byte> span, ReadOnlySpan<byte> pattern)
        {
            if (pattern.Length == 0) return 0;
            if (span.Length < pattern.Length) return -1;

            int limit = span.Length - pattern.Length;
            for (int i = 0; i <= limit; i++)
            {
                if (EqualsIgnoreCaseAscii(span.Slice(i), pattern))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int CountNewlines(ReadOnlySpan<byte> span)
        {
            int count = 0;
            int index;
            while ((index = span.IndexOf((byte)'\n')) >= 0)
            {
                count++;
                span = span.Slice(index + 1);
            }
            return count;
        }

        private static List<string> GetContextBefore(ReadOnlySpan<byte> fileData, int lineStartOffset, int contextCount)
        {
            var contextLines = new List<string>();
            if (contextCount <= 0 || lineStartOffset <= 0) return contextLines;

            int currentOffset = lineStartOffset - 1; // Skip current line's preceding newline
            for (int i = 0; i < contextCount; i++)
            {
                if (currentOffset < 0) break;
                int prevNewline = fileData.Slice(0, currentOffset).LastIndexOf((byte)'\n');
                int start = prevNewline < 0 ? 0 : prevNewline + 1;
                int length = currentOffset - start;
                if (length > 0)
                {
                    var lineBytes = fileData.Slice(start, length);
                    if (lineBytes.Length > 0 && lineBytes[^1] == '\r')
                        lineBytes = lineBytes.Slice(0, lineBytes.Length - 1);
                    contextLines.Insert(0, Encoding.UTF8.GetString(lineBytes));
                }
                else
                {
                    contextLines.Insert(0, string.Empty);
                }
                currentOffset = prevNewline;
            }
            return contextLines;
        }

        private static List<string> GetContextAfter(ReadOnlySpan<byte> fileData, int lineEndOffset, int contextCount)
        {
            var contextLines = new List<string>();
            if (contextCount <= 0 || lineEndOffset >= fileData.Length) return contextLines;

            int currentOffset = lineEndOffset + 1; // Skip current line's trailing newline
            for (int i = 0; i < contextCount; i++)
            {
                if (currentOffset >= fileData.Length) break;
                int nextNewline = fileData.Slice(currentOffset).IndexOf((byte)'\n');
                int end = nextNewline < 0 ? fileData.Length : currentOffset + nextNewline;
                int length = end - currentOffset;
                if (length > 0)
                {
                    var lineBytes = fileData.Slice(currentOffset, length);
                    if (lineBytes.Length > 0 && lineBytes[^1] == '\r')
                        lineBytes = lineBytes.Slice(0, lineBytes.Length - 1);
                    contextLines.Add(Encoding.UTF8.GetString(lineBytes));
                }
                else
                {
                    contextLines.Add(string.Empty);
                }
                currentOffset = end + 1;
            }
            return contextLines;
        }
    }
}
