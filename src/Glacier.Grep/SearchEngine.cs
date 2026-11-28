using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Glacier.Grep
{
    /// <summary>
    /// Orchestrates high-performance, concurrent directory searching.
    /// Uses bounded Channels for a concurrent producer-consumer pipeline and SIMD-accelerated UTF-8 matching.
    /// </summary>
    public class SearchEngine
    {
        private readonly string _rootDir;

        public SearchEngine(string rootDir)
        {
            _rootDir = Path.GetFullPath(rootDir);
        }

        public Task<List<SearchResult>> SearchAsync(
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
            return SearchAsync(
                query,
                isRegex,
                caseSensitive,
                contextLines,
                fileGlobs,
                searchHidden,
                searchBinary,
                invertMatch,
                maxDepth,
                CancellationToken.None);
        }

        public async Task<List<SearchResult>> SearchAsync(
            string query,
            bool isRegex,
            bool caseSensitive,
            int contextLines,
            string[]? fileGlobs,
            bool searchHidden,
            bool searchBinary,
            bool invertMatch,
            int? maxDepth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prepare search plan first so invalid regex patterns fail immediately without spawning tasks.
            SearchPlan plan = CreateSearchPlan(query, isRegex, caseSensitive, invertMatch);

            var results = new List<SearchResult>();
            int totalFilesCount = 0;
            long totalBytesScanned = 0;

            var channel = Channel.CreateBounded<FileSearchTask>(new BoundedChannelOptions(2048)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var traverser = new DirectoryTraverser(_rootDir, searchHidden, maxDepth);
            var producerTask = Task.Run(async () =>
            {
                try
                {
                    foreach (var fileTask in traverser.EnumerateFiles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref totalFilesCount);
                        Interlocked.Add(ref totalBytesScanned, fileTask.Length);

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

                        await channel.Writer.WriteAsync(fileTask, cancellationToken).ConfigureAwait(false);
                    }
                    channel.Writer.TryComplete();
                }
                catch (OperationCanceledException oce)
                {
                    channel.Writer.TryComplete(oce);
                    throw;
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                    throw;
                }
            }, cancellationToken);

            int workerCount = Environment.ProcessorCount;
            var workerTasks = new Task<List<SearchResult>>[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                workerTasks[i] = Task.Run(async () =>
                {
                    var localResults = new List<SearchResult>();
                    var reader = channel.Reader;
                    while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (reader.TryRead(out var task))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                SearchFile(task, plan, caseSensitive, contextLines, searchBinary, invertMatch, localResults);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                // Ignore errors for individual files (e.g. sharing violations)
                            }
                        }
                    }
                    return localResults;
                }, cancellationToken);
            }

            List<SearchResult>[] workerResults = await Task.WhenAll(workerTasks).ConfigureAwait(false);
            await producerTask.ConfigureAwait(false);

            for (int i = 0; i < workerResults.Length; i++)
            {
                results.AddRange(workerResults[i]);
            }

            return results;
        }

        private static SearchPlan CreateSearchPlan(string query, bool isRegex, bool caseSensitive, bool invertMatch)
        {
            if (!isRegex)
            {
                byte[] primary = Encoding.UTF8.GetBytes(query);
                AsciiIgnoreCaseMatcher? primaryMatcher = (!caseSensitive && primary.Length > 0)
                    ? new AsciiIgnoreCaseMatcher(primary)
                    : null;

                return new SearchPlan(
                    RegexPatternClass.Literal,
                    primary,
                    Array.Empty<byte>(),
                    primaryMatcher,
                    null,
                    null);
            }

            var (patternClass, literal, suffix) = RegexPatternClassifier.ClassifyPatternFull(query, caseSensitive, invertMatch);
            if (patternClass == RegexPatternClass.FullRegex)
            {
                var options = RegexOptions.Multiline | RegexOptions.NonBacktracking;
                if (!caseSensitive)
                {
                    options |= RegexOptions.IgnoreCase;
                }
                var regex = new Regex(query, options);
                return new SearchPlan(
                    RegexPatternClass.FullRegex,
                    Array.Empty<byte>(),
                    Array.Empty<byte>(),
                    null,
                    null,
                    regex);
            }

            byte[] p1Bytes = Encoding.UTF8.GetBytes(literal);
            byte[] p2Bytes = string.IsNullOrEmpty(suffix) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(suffix);
            AsciiIgnoreCaseMatcher? m1 = (!caseSensitive && p1Bytes.Length > 0) ? new AsciiIgnoreCaseMatcher(p1Bytes) : null;
            AsciiIgnoreCaseMatcher? m2 = (!caseSensitive && p2Bytes.Length > 0) ? new AsciiIgnoreCaseMatcher(p2Bytes) : null;

            return new SearchPlan(patternClass, p1Bytes, p2Bytes, m1, m2, null);
        }

        private void SearchFile(
            FileSearchTask task,
            in SearchPlan plan,
            bool caseSensitive,
            int contextLines,
            bool searchBinary,
            bool invertMatch,
            List<SearchResult> results)
        {
            var context = new FileSearchContext();
            int overlapMargin = 0;
            if (plan.Regex != null || plan.PatternClass != RegexPatternClass.Literal)
            {
                overlapMargin = Math.Max(4096, plan.PrimaryBytes.Length + plan.SecondaryBytes.Length);
            }
            else if (plan.PrimaryBytes.Length > 1)
            {
                overlapMargin = plan.PrimaryBytes.Length - 1;
            }

            SearchPlan localPlan = plan;
            HybridIoDispatcher.ProcessFileWithPrecedingByte(task.FullPath, task.Length, overlapMargin, (ReadOnlySpan<byte> chunkData, long chunkStartOffset, long logicalStartOffset, long logicalEndOffset, long totalLength, bool isLastChunk, byte precedingByte, Rune precedingRune) =>
            {
                if (!searchBinary)
                {
                    if (chunkStartOffset == 0 && chunkData.Length > 0)
                    {
                        context.IsBinary = IsBinaryFile(chunkData);
                    }
                    if (context.IsBinary) return;
                }

                if (chunkData.Length == 0) return;

                if (contextLines > 0 && logicalStartOffset > 0 && context.PendingContextAfter is { Count: > 0 })
                {
                    FillPendingContextAfter(chunkData, chunkStartOffset, logicalStartOffset, logicalEndOffset, isLastChunk, context);
                }

                if (invertMatch)
                {
                    SearchInverted(chunkData, task, in localPlan, caseSensitive, contextLines, results, context, chunkStartOffset, logicalStartOffset, logicalEndOffset, isLastChunk);
                }
                else if (localPlan.PatternClass == RegexPatternClass.FullRegex)
                {
                    SearchRegex(chunkData, task, localPlan.Regex!, contextLines, results, context, chunkStartOffset, logicalStartOffset, logicalEndOffset, isLastChunk, precedingByte, precedingRune);
                }
                else if (localPlan.PatternClass == RegexPatternClass.Literal)
                {
                    SearchLiteral(chunkData, task, localPlan.PrimaryBytes, localPlan.PrimaryMatcher, caseSensitive, contextLines, results, context, chunkStartOffset, logicalStartOffset, logicalEndOffset, isLastChunk, precedingByte);
                }
                else
                {
                    SearchClassifiedRegex(chunkData, task, in localPlan, caseSensitive, contextLines, results, context, chunkStartOffset, logicalStartOffset, logicalEndOffset, isLastChunk, precedingByte);
                }
            });
        }

        private static bool IsBinaryFile(ReadOnlySpan<byte> data)
        {
            int checkLen = Math.Min(data.Length, 1024);
            return data.Slice(0, checkLen).Contains((byte)0);
        }

        private string GetRelativePathCached(FileSearchContext context, FileSearchTask task)
        {
            return context.RelativePath ??= Path.GetRelativePath(_rootDir, task.FullPath);
        }

        private static int SaturateLineNumber(long lineNumber)
        {
            return lineNumber > int.MaxValue ? int.MaxValue : (int)lineNumber;
        }

        private void SearchLiteral(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            byte[] patternBytes,
            AsciiIgnoreCaseMatcher? matcher,
            bool caseSensitive,
            int contextLines,
            List<SearchResult> results,
            FileSearchContext context,
            long chunkStartOffset,
            long logicalStartOffset,
            long logicalEndOffset,
            bool isLastChunk,
            byte precedingByte)
        {
            if (patternBytes.Length == 0) return;

            int m = patternBytes.Length;
            int overlap = (int)(logicalStartOffset - chunkStartOffset);
            int logicalEndIndex = (int)(logicalEndOffset - chunkStartOffset);
            int minLookbackOffset = overlap;
            int lastNewlineOffset = overlap;
            long currentLineNumber = context.GlobalLineNumber;

            bool continuesOpenLine = logicalStartOffset > 0 && context.IsOpenLine;
            bool openLineClosesHere = false;
            int openLineEndInChunk = -1;
            int firstNlRel = -1;
            string? fullOpenLineContent = null;
            List<string>? openLineContextAfter = null;
            bool openLineAfterHitOpenLine = false;
            int prevOpenLineCharCount = context.OpenLineCharCount;
            long prevOpenLineNumber = context.OpenLineNumber;
            List<string>? prevOpenLineContextBefore = null;

            if (continuesOpenLine)
            {
                firstNlRel = fileData.Slice(overlap, logicalEndIndex - overlap).IndexOf((byte)'\n');
                openLineClosesHere = firstNlRel >= 0 || isLastChunk;
                openLineEndInChunk = firstNlRel >= 0 ? overlap + firstNlRel : logicalEndIndex;
                if (openLineClosesHere)
                {
                    FinalizeOpenLine(
                        context,
                        fileData,
                        overlap,
                        openLineEndInChunk,
                        logicalEndIndex,
                        contextLines,
                        isLastChunk,
                        firstNlRel >= 0,
                        results,
                        out fullOpenLineContent,
                        out openLineContextAfter,
                        out openLineAfterHitOpenLine,
                        out prevOpenLineCharCount,
                        out prevOpenLineNumber,
                        out prevOpenLineContextBefore);
                    minLookbackOffset = firstNlRel >= 0 ? openLineEndInChunk + 1 : logicalEndIndex;
                }
            }

            bool hasNewTrailingOpenLine = !isLastChunk &&
                                          logicalEndIndex > overlap &&
                                          fileData[logicalEndIndex - 1] != (byte)'\n' &&
                                          (!continuesOpenLine || openLineClosesHere);
            int newOpenLineStart = logicalEndIndex;
            List<string>? newOpenLineContextBefore = null;
            if (hasNewTrailingOpenLine)
            {
                int searchFrom = minLookbackOffset;
                int trailingNlRel = searchFrom < logicalEndIndex
                    ? fileData.Slice(searchFrom, logicalEndIndex - searchFrom).LastIndexOf((byte)'\n')
                    : -1;
                newOpenLineStart = trailingNlRel >= 0 ? searchFrom + trailingNlRel + 1 : searchFrom;
                if (contextLines > 0)
                {
                    newOpenLineContextBefore = GetContextBeforeWithCarry(
                        fileData,
                        newOpenLineStart,
                        minLookbackOffset,
                        contextLines,
                        context,
                        chunkStartOffset);
                }
            }

            int offset = 0;
            while (offset <= fileData.Length - m)
            {
                int rel = IndexOfLiteralBytes(fileData.Slice(offset), patternBytes, matcher, caseSensitive);
                if (rel < 0) break;

                offset += rel;

                // Boundary match deduplication:
                // If this is a subsequent chunk and the match ends at or before logicalStartOffset,
                // it was already emitted in the previous chunk.
                long matchFileOffset = chunkStartOffset + offset;
                if (logicalStartOffset > 0 && matchFileOffset + m <= logicalStartOffset)
                {
                    offset += m;
                    continue;
                }

                int lineStart = fileData.Slice(0, offset).LastIndexOf((byte)'\n') + 1;
                int lineEnd = fileData.Slice(offset).IndexOf((byte)'\n');
                if (lineEnd < 0) lineEnd = fileData.Length;
                else lineEnd += offset;

                long matchLineNumber;
                if (lineStart > lastNewlineOffset)
                {
                    currentLineNumber += fileData.Slice(lastNewlineOffset, lineStart - lastNewlineOffset).Count((byte)'\n');
                    lastNewlineOffset = lineStart;
                    matchLineNumber = currentLineNumber;
                }
                else if (lineStart < lastNewlineOffset)
                {
                    matchLineNumber = Math.Max(1L, currentLineNumber - fileData.Slice(lineStart, lastNewlineOffset - lineStart).Count((byte)'\n'));
                }
                else
                {
                    matchLineNumber = currentLineNumber;
                }

                if (continuesOpenLine && !openLineClosesHere)
                {
                    int lineCharOffset = prevOpenLineCharCount + (offset >= overlap
                        ? GetUtf16CharCount(fileData.Slice(overlap, offset - overlap))
                        : -GetUtf16CharCount(fileData.Slice(offset, overlap - offset)));
                    int matchCharLen = GetUtf16CharCount(fileData.Slice(offset, m));
                    var pendingResult = new SearchResult
                    {
                        FilePath = GetRelativePathCached(context, task),
                        LineNumber = SaturateLineNumber(context.OpenLineNumber),
                        LongLineNumber = context.OpenLineNumber,
                        MatchLength = matchCharLen,
                        ContextBefore = contextLines > 0 ? CloneList(context.OpenLineContextBefore) : new List<string>()
                    };
                    context.PendingOpenLineMatches ??= new List<(SearchResult, int, bool)>();
                    context.PendingOpenLineMatches.Add((pendingResult, lineCharOffset, contextLines > 0));
                }
                else if (openLineClosesHere && lineStart <= openLineEndInChunk)
                {
                    int lineCharOffset = prevOpenLineCharCount + (offset >= overlap
                        ? GetUtf16CharCount(fileData.Slice(overlap, offset - overlap))
                        : -GetUtf16CharCount(fileData.Slice(offset, overlap - offset)));
                    int matchCharLen = GetUtf16CharCount(fileData.Slice(offset, m));
                    int clampedStart = Math.Clamp(lineCharOffset, 0, fullOpenLineContent!.Length);
                    int clampedLen = Math.Clamp(matchCharLen, 0, fullOpenLineContent.Length - clampedStart);

                    var closedResult = new SearchResult
                    {
                        FilePath = GetRelativePathCached(context, task),
                        LineNumber = SaturateLineNumber(prevOpenLineNumber),
                        LongLineNumber = prevOpenLineNumber,
                        MatchContent = fullOpenLineContent,
                        MatchStartIndex = clampedStart,
                        MatchLength = clampedLen
                    };

                    if (contextLines > 0)
                    {
                        closedResult.ContextBefore = CloneList(prevOpenLineContextBefore);
                        var after = CloneList(openLineContextAfter);
                        closedResult.ContextAfter = after;
                        TrackPendingContextAfter(context, closedResult, after.Count, contextLines, isLastChunk, firstNlRel >= 0, openLineAfterHitOpenLine);
                    }

                    results.Add(closedResult);
                }
                else if (hasNewTrailingOpenLine && lineStart >= newOpenLineStart)
                {
                    int lineCharOffset = GetUtf16CharCount(fileData.Slice(newOpenLineStart, offset - newOpenLineStart));
                    int matchCharLen = GetUtf16CharCount(fileData.Slice(offset, m));
                    var pendingResult = new SearchResult
                    {
                        FilePath = GetRelativePathCached(context, task),
                        LineNumber = SaturateLineNumber(matchLineNumber),
                        LongLineNumber = matchLineNumber,
                        MatchLength = matchCharLen,
                        ContextBefore = contextLines > 0 ? CloneList(newOpenLineContextBefore) : new List<string>()
                    };
                    context.PendingOpenLineMatches ??= new List<(SearchResult, int, bool)>();
                    context.PendingOpenLineMatches.Add((pendingResult, lineCharOffset, contextLines > 0));
                }
                else
                {
                    ReadOnlySpan<byte> lineSpan = fileData.Slice(lineStart, lineEnd - lineStart);
                    if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                        lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                    EmitByteLineMatch(
                        fileData,
                        task,
                        context,
                        results,
                        lineSpan,
                        lineStart,
                        lineEnd,
                        offset,
                        offset + m,
                        matchLineNumber,
                        contextLines,
                        isLastChunk,
                        minLookbackOffset,
                        chunkStartOffset,
                        precedingByte);
                }

                offset += m;
            }

            if (logicalEndIndex > lastNewlineOffset)
            {
                currentLineNumber += fileData.Slice(lastNewlineOffset, logicalEndIndex - lastNewlineOffset).Count((byte)'\n');
            }

            if (!isLastChunk)
            {
                if (continuesOpenLine && !openLineClosesHere)
                {
                    context.OpenLineBuffer ??= new ArrayBufferWriter<byte>();
                    ReadOnlySpan<byte> seg = fileData.Slice(overlap, logicalEndIndex - overlap);
                    context.OpenLineBuffer.Write(seg);
                    context.OpenLineCharCount += GetUtf16CharCount(seg);
                }
                else if (hasNewTrailingOpenLine)
                {
                    if (contextLines > 0)
                    {
                        UpdateTrailingContextLines(fileData, minLookbackOffset, newOpenLineStart, contextLines, context);
                    }
                    StartOpenLine(context, fileData, newOpenLineStart, logicalEndIndex, chunkStartOffset, currentLineNumber, newOpenLineContextBefore);
                }
                else if (contextLines > 0)
                {
                    UpdateTrailingContextLines(fileData, minLookbackOffset, logicalEndIndex, contextLines, context);
                }
            }

            context.GlobalLineNumber = currentLineNumber;
            context.LastProcessedFileOffset = logicalEndOffset;
        }

        private void SearchClassifiedRegex(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            in SearchPlan plan,
            bool caseSensitive,
            int contextLines,
            List<SearchResult> results,
            FileSearchContext context,
            long chunkStartOffset,
            long logicalStartOffset,
            long logicalEndOffset,
            bool isLastChunk,
            byte precedingByte)
        {
            int overlap = (int)(logicalStartOffset - chunkStartOffset);
            int logicalEndIndex = (int)(logicalEndOffset - chunkStartOffset);
            int minLookbackOffset = overlap;
            int lastNewlineOffset = overlap;
            long currentLineNumber = context.GlobalLineNumber;

            byte[] p1 = plan.PrimaryBytes;
            byte[] p2 = plan.SecondaryBytes;
            AsciiIgnoreCaseMatcher? m1 = plan.PrimaryMatcher;
            AsciiIgnoreCaseMatcher? m2 = plan.SecondaryMatcher;

            bool continuesOpenLine = logicalStartOffset > 0 && context.IsOpenLine;
            int offset = 0;

            if (continuesOpenLine)
            {
                int firstNlRel = fileData.Slice(overlap, logicalEndIndex - overlap).IndexOf((byte)'\n');
                bool openLineClosesHere = firstNlRel >= 0 || isLastChunk;
                int openLineEndInChunk = firstNlRel >= 0 ? overlap + firstNlRel : logicalEndIndex;

                context.OpenLineBuffer ??= new ArrayBufferWriter<byte>();
                if (openLineEndInChunk > overlap)
                {
                    context.OpenLineBuffer.Write(fileData.Slice(overlap, openLineEndInChunk - overlap));
                }

                if (!openLineClosesHere)
                {
                    context.LastProcessedFileOffset = logicalEndOffset;
                    return;
                }

                ReadOnlySpan<byte> rawFullLine = context.OpenLineBuffer.WrittenSpan;
                bool matched = EvaluateClassifiedOpenLine(rawFullLine, in plan, caseSensitive, out int rawMatchStart, out int rawMatchEnd);
                string? fullLineContent = (matched || contextLines > 0) ? DecodeTrimmedLine(rawFullLine) : null;

                if (contextLines > 0 && context.PendingContextAfter is { Count: > 0 })
                {
                    FulfillPendingContextAfterWithOpenLine(context, fullLineContent!, fileData, openLineEndInChunk, logicalEndIndex, isLastChunk, firstNlRel >= 0);
                }

                if (matched)
                {
                    ReadOnlySpan<byte> cleanLine = rawFullLine;
                    if (cleanLine.Length > 0 && cleanLine[^1] == (byte)'\r')
                        cleanLine = cleanLine.Slice(0, cleanLine.Length - 1);

                    int safeByteStart = Math.Clamp(rawMatchStart, 0, cleanLine.Length);
                    int safeByteLen = Math.Clamp(Math.Max(0, rawMatchEnd - rawMatchStart), 0, cleanLine.Length - safeByteStart);
                    int matchStartIndex;
                    int matchLength;
                    if (Ascii.IsValid(cleanLine))
                    {
                        matchStartIndex = safeByteStart;
                        matchLength = safeByteLen;
                    }
                    else
                    {
                        matchStartIndex = Encoding.UTF8.GetCharCount(cleanLine.Slice(0, safeByteStart));
                        matchLength = Encoding.UTF8.GetCharCount(cleanLine.Slice(safeByteStart, safeByteLen));
                    }

                    var closedResult = new SearchResult
                    {
                        FilePath = GetRelativePathCached(context, task),
                        LineNumber = SaturateLineNumber(context.OpenLineNumber),
                        LongLineNumber = context.OpenLineNumber,
                        MatchContent = fullLineContent!,
                        MatchStartIndex = matchStartIndex,
                        MatchLength = matchLength
                    };

                    if (contextLines > 0)
                    {
                        closedResult.ContextBefore = CloneList(context.OpenLineContextBefore);
                        bool afterHitOpenLine = false;
                        var after = firstNlRel >= 0
                            ? GetContextAfter(fileData, openLineEndInChunk, contextLines, isLastChunk, out afterHitOpenLine)
                            : new List<string>();
                        closedResult.ContextAfter = after;
                        TrackPendingContextAfter(context, closedResult, after.Count, contextLines, isLastChunk, firstNlRel >= 0, afterHitOpenLine);
                    }

                    results.Add(closedResult);
                }

                if (contextLines > 0 && firstNlRel >= 0)
                {
                    PushTrailingContextLine(context, fullLineContent!, contextLines);
                }

                context.IsOpenLine = false;
                context.OpenLineFileStartOffset = 0;
                context.OpenLineNumber = 0;
                context.OpenLineCharCount = 0;
                context.OpenLineBuffer.Clear();
                context.OpenLineContextBefore = null;
                context.PendingOpenLineMatches?.Clear();

                minLookbackOffset = firstNlRel >= 0 ? openLineEndInChunk + 1 : logicalEndIndex;
                offset = minLookbackOffset;
            }

            bool hasNewTrailingOpenLine = !isLastChunk &&
                                          logicalEndIndex > overlap &&
                                          fileData[logicalEndIndex - 1] != (byte)'\n';

            switch (plan.PatternClass)
            {
                case RegexPatternClass.StartsWith:
                {
                    int m = p1.Length;
                    while (offset <= fileData.Length - m)
                    {
                        int rel = IndexOfLiteralBytes(fileData.Slice(offset), p1, m1, caseSensitive);
                        if (rel < 0) break;

                        int pos = offset + rel;
                        int matchEnd = pos + m;
                        if (!isLastChunk && matchEnd >= logicalEndIndex) break;

                        bool atLineStart = pos > 0 ? fileData[pos - 1] == (byte)'\n' : (chunkStartOffset == 0 || precedingByte == (byte)'\n');
                        if (!atLineStart)
                        {
                            offset = pos + 1;
                            continue;
                        }

                        int lineEndRel = fileData.Slice(pos).IndexOf((byte)'\n');
                        int lineEnd = lineEndRel < 0 ? fileData.Length : pos + lineEndRel;
                        if (hasNewTrailingOpenLine && lineEnd == logicalEndIndex) break;

                        if (overlap > 0 && matchEnd < overlap)
                        {
                            offset = lineEnd + 1;
                            continue;
                        }

                        ReadOnlySpan<byte> lineSpan = fileData.Slice(pos, lineEnd - pos);
                        if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                            lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                        long matchLineNumber = AdvanceOrAdjustLineNumber(fileData, pos, ref lastNewlineOffset, ref currentLineNumber);
                        EmitByteLineMatch(fileData, task, context, results, lineSpan, pos, lineEnd, pos, matchEnd, matchLineNumber, contextLines, isLastChunk, minLookbackOffset, chunkStartOffset, precedingByte);

                        offset = lineEnd + 1;
                    }
                    break;
                }

                case RegexPatternClass.EndsWith:
                {
                    int m = p1.Length;
                    while (offset <= fileData.Length - m)
                    {
                        int rel = IndexOfLiteralBytes(fileData.Slice(offset), p1, m1, caseSensitive);
                        if (rel < 0) break;

                        int pos = offset + rel;
                        int matchEnd = pos + m;
                        if (!isLastChunk && matchEnd >= logicalEndIndex) break;

                        bool atLineEnd = (matchEnd == fileData.Length && isLastChunk) ||
                                         (matchEnd < fileData.Length && fileData[matchEnd] == (byte)'\n');
                        if (!atLineEnd)
                        {
                            offset = pos + 1;
                            continue;
                        }

                        if (overlap > 0 && matchEnd < overlap)
                        {
                            offset = matchEnd + 1;
                            continue;
                        }

                        int lineStart = fileData.Slice(0, pos).LastIndexOf((byte)'\n') + 1;
                        ReadOnlySpan<byte> lineSpan = fileData.Slice(lineStart, matchEnd - lineStart);
                        if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                            lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                        long matchLineNumber = AdvanceOrAdjustLineNumber(fileData, lineStart, ref lastNewlineOffset, ref currentLineNumber);
                        EmitByteLineMatch(fileData, task, context, results, lineSpan, lineStart, matchEnd, pos, matchEnd, matchLineNumber, contextLines, isLastChunk, minLookbackOffset, chunkStartOffset, precedingByte);

                        offset = matchEnd + 1;
                    }
                    break;
                }

                case RegexPatternClass.Equals:
                {
                    int m = p1.Length;
                    while (offset <= fileData.Length - m)
                    {
                        int rel = IndexOfLiteralBytes(fileData.Slice(offset), p1, m1, caseSensitive);
                        if (rel < 0) break;

                        int pos = offset + rel;
                        int matchEnd = pos + m;
                        if (!isLastChunk && matchEnd >= logicalEndIndex) break;

                        bool atLineStart = pos > 0 ? fileData[pos - 1] == (byte)'\n' : (chunkStartOffset == 0 || precedingByte == (byte)'\n');
                        bool atLineEnd = (matchEnd == fileData.Length && isLastChunk) ||
                                         (matchEnd < fileData.Length && fileData[matchEnd] == (byte)'\n');
                        if (!atLineStart || !atLineEnd)
                        {
                            offset = pos + 1;
                            continue;
                        }

                        if (overlap > 0 && matchEnd < overlap)
                        {
                            offset = matchEnd + 1;
                            continue;
                        }

                        ReadOnlySpan<byte> lineSpan = fileData.Slice(pos, matchEnd - pos);
                        if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                            lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                        long matchLineNumber = AdvanceOrAdjustLineNumber(fileData, pos, ref lastNewlineOffset, ref currentLineNumber);
                        EmitByteLineMatch(fileData, task, context, results, lineSpan, pos, matchEnd, pos, matchEnd, matchLineNumber, contextLines, isLastChunk, minLookbackOffset, chunkStartOffset, precedingByte);

                        offset = matchEnd + 1;
                    }
                    break;
                }

                case RegexPatternClass.PrefixAndSuffix:
                {
                    int minLen = p1.Length + p2.Length;
                    while (offset <= fileData.Length - minLen)
                    {
                        int rel = IndexOfLiteralBytes(fileData.Slice(offset), p1, m1, caseSensitive);
                        if (rel < 0) break;

                        int pos = offset + rel;
                        bool atLineStart = pos > 0 ? fileData[pos - 1] == (byte)'\n' : (chunkStartOffset == 0 || precedingByte == (byte)'\n');
                        if (!atLineStart)
                        {
                            offset = pos + 1;
                            continue;
                        }

                        int lineEndRel = fileData.Slice(pos).IndexOf((byte)'\n');
                        int lineEnd = lineEndRel < 0 ? fileData.Length : pos + lineEndRel;
                        if (!isLastChunk && lineEnd >= logicalEndIndex) break;

                        if (overlap > 0 && lineEnd < overlap)
                        {
                            offset = lineEnd + 1;
                            continue;
                        }

                        ReadOnlySpan<byte> rawLine = fileData.Slice(pos, lineEnd - pos);
                        if (rawLine.Length >= minLen && EndsWithLiteralBytes(rawLine, p2, caseSensitive))
                        {
                            ReadOnlySpan<byte> lineSpan = rawLine;
                            if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                                lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                            long matchLineNumber = AdvanceOrAdjustLineNumber(fileData, pos, ref lastNewlineOffset, ref currentLineNumber);
                            EmitByteLineMatch(fileData, task, context, results, lineSpan, pos, lineEnd, pos, lineEnd, matchLineNumber, contextLines, isLastChunk, minLookbackOffset, chunkStartOffset, precedingByte);
                        }

                        offset = lineEnd + 1;
                    }
                    break;
                }

                case RegexPatternClass.ContainsBothOrdered:
                {
                    int minLen = p1.Length + p2.Length;
                    while (offset <= fileData.Length - minLen)
                    {
                        int rel1 = IndexOfLiteralBytes(fileData.Slice(offset), p1, m1, caseSensitive);
                        if (rel1 < 0) break;

                        int p1Pos = offset + rel1;
                        if (!isLastChunk && p1Pos + minLen >= logicalEndIndex) break;

                        int lineStart = fileData.Slice(0, p1Pos).LastIndexOf((byte)'\n') + 1;
                        int lineEndRel = fileData.Slice(p1Pos).IndexOf((byte)'\n');
                        int lineEnd = lineEndRel < 0 ? fileData.Length : p1Pos + lineEndRel;
                        if (hasNewTrailingOpenLine && lineEnd == logicalEndIndex) break;

                        int searchP2Start = p1Pos + p1.Length;
                        int searchP2Len = lineEnd - searchP2Start;
                        if (searchP2Len >= p2.Length)
                        {
                            int rel2 = LastIndexOfLiteralBytes(fileData.Slice(searchP2Start, searchP2Len), p2, m2, caseSensitive);
                            if (rel2 >= 0)
                            {
                                int matchEnd = searchP2Start + rel2 + p2.Length;
                                if (!isLastChunk && matchEnd >= logicalEndIndex) break;

                                if (overlap > 0 && matchEnd < overlap)
                                {
                                    offset = lineEnd + 1;
                                    continue;
                                }

                                ReadOnlySpan<byte> lineSpan = fileData.Slice(lineStart, lineEnd - lineStart);
                                if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                                    lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                                long matchLineNumber = AdvanceOrAdjustLineNumber(fileData, lineStart, ref lastNewlineOffset, ref currentLineNumber);
                                EmitByteLineMatch(fileData, task, context, results, lineSpan, lineStart, lineEnd, p1Pos, matchEnd, matchLineNumber, contextLines, isLastChunk, minLookbackOffset, chunkStartOffset, precedingByte);
                            }
                        }

                        offset = lineEnd + 1;
                    }
                    break;
                }
            }

            if (logicalEndIndex > lastNewlineOffset)
            {
                currentLineNumber += fileData.Slice(lastNewlineOffset, logicalEndIndex - lastNewlineOffset).Count((byte)'\n');
            }

            if (!isLastChunk)
            {
                if (hasNewTrailingOpenLine)
                {
                    int trailingNlRel = minLookbackOffset < logicalEndIndex
                        ? fileData.Slice(minLookbackOffset, logicalEndIndex - minLookbackOffset).LastIndexOf((byte)'\n')
                        : -1;
                    int newOpenLineStart = trailingNlRel >= 0 ? minLookbackOffset + trailingNlRel + 1 : minLookbackOffset;
                    List<string>? openLineCtx = null;
                    if (contextLines > 0)
                    {
                        UpdateTrailingContextLines(fileData, minLookbackOffset, newOpenLineStart, contextLines, context);
                        openLineCtx = CloneList(context.TrailingContextLines);
                    }
                    StartOpenLine(context, fileData, newOpenLineStart, logicalEndIndex, chunkStartOffset, currentLineNumber, openLineCtx);
                }
                else if (contextLines > 0)
                {
                    UpdateTrailingContextLines(fileData, minLookbackOffset, logicalEndIndex, contextLines, context);
                }
            }

            context.GlobalLineNumber = currentLineNumber;
            context.LastProcessedFileOffset = logicalEndOffset;
        }

        private static bool EvaluateClassifiedOpenLine(
            ReadOnlySpan<byte> rawLine,
            in SearchPlan plan,
            bool caseSensitive,
            out int matchByteStart,
            out int matchByteEnd)
        {
            byte[] p1 = plan.PrimaryBytes;
            byte[] p2 = plan.SecondaryBytes;
            matchByteStart = 0;
            matchByteEnd = 0;

            switch (plan.PatternClass)
            {
                case RegexPatternClass.StartsWith:
                    if (StartsWithLiteralBytes(rawLine, p1, caseSensitive))
                    {
                        matchByteStart = 0;
                        matchByteEnd = p1.Length;
                        return true;
                    }
                    return false;

                case RegexPatternClass.EndsWith:
                    if (EndsWithLiteralBytes(rawLine, p1, caseSensitive))
                    {
                        matchByteStart = rawLine.Length - p1.Length;
                        matchByteEnd = rawLine.Length;
                        return true;
                    }
                    return false;

                case RegexPatternClass.Equals:
                    if (rawLine.Length == p1.Length && StartsWithLiteralBytes(rawLine, p1, caseSensitive))
                    {
                        matchByteStart = 0;
                        matchByteEnd = p1.Length;
                        return true;
                    }
                    return false;

                case RegexPatternClass.PrefixAndSuffix:
                    if (rawLine.Length >= p1.Length + p2.Length &&
                        StartsWithLiteralBytes(rawLine, p1, caseSensitive) &&
                        EndsWithLiteralBytes(rawLine, p2, caseSensitive))
                    {
                        matchByteStart = 0;
                        matchByteEnd = rawLine.Length;
                        return true;
                    }
                    return false;

                case RegexPatternClass.ContainsBothOrdered:
                {
                    if (rawLine.Length < p1.Length + p2.Length) return false;
                    int idx1 = IndexOfLiteralBytes(rawLine, p1, plan.PrimaryMatcher, caseSensitive);
                    if (idx1 < 0) return false;
                    int searchP2Start = idx1 + p1.Length;
                    int rel2 = LastIndexOfLiteralBytes(rawLine.Slice(searchP2Start), p2, plan.SecondaryMatcher, caseSensitive);
                    if (rel2 < 0) return false;
                    matchByteStart = idx1;
                    matchByteEnd = searchP2Start + rel2 + p2.Length;
                    return true;
                }

                default:
                    return false;
            }
        }

        private static long AdvanceOrAdjustLineNumber(
            ReadOnlySpan<byte> fileData,
            int lineStart,
            ref int lastNewlineOffset,
            ref long currentLineNumber)
        {
            if (lineStart > lastNewlineOffset)
            {
                currentLineNumber += fileData.Slice(lastNewlineOffset, lineStart - lastNewlineOffset).Count((byte)'\n');
                lastNewlineOffset = lineStart;
                return currentLineNumber;
            }
            if (lineStart < lastNewlineOffset)
            {
                return Math.Max(1L, currentLineNumber - fileData.Slice(lineStart, lastNewlineOffset - lineStart).Count((byte)'\n'));
            }
            return currentLineNumber;
        }

        private void EmitByteLineMatch(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            FileSearchContext context,
            List<SearchResult> results,
            ReadOnlySpan<byte> lineSpan,
            int lineStart,
            int lineEnd,
            int matchByteStart,
            int matchByteEnd,
            long matchLineNumber,
            int contextLines,
            bool isLastChunk,
            int minLookbackOffset,
            long chunkStartOffset,
            byte precedingByte = 0)
        {
            string matchContent = Encoding.UTF8.GetString(lineSpan);
            int safeByteRelStart = Math.Clamp(matchByteStart - lineStart, 0, lineSpan.Length);
            int rawByteLen = Math.Max(0, matchByteEnd - matchByteStart);
            int safeByteLen = Math.Clamp(rawByteLen, 0, lineSpan.Length - safeByteRelStart);

            int matchStartIndex;
            int matchLength;
            if (Ascii.IsValid(lineSpan))
            {
                matchStartIndex = safeByteRelStart;
                matchLength = safeByteLen;
            }
            else
            {
                matchStartIndex = Encoding.UTF8.GetCharCount(lineSpan.Slice(0, safeByteRelStart));
                matchLength = Encoding.UTF8.GetCharCount(lineSpan.Slice(safeByteRelStart, safeByteLen));
            }

            List<string>? resolvedContextBefore = null;
            if (lineStart == 0 && chunkStartOffset > 0 && precedingByte != (byte)'\n')
            {
                (string prefixText, resolvedContextBefore) = ReadMissingLinePrefixAndContext(task.FullPath, chunkStartOffset, contextLines);
                if (prefixText.Length > 0)
                {
                    matchContent = string.Concat(prefixText, matchContent);
                    if (lineSpan.IsEmpty && matchContent[^1] == '\r')
                    {
                        matchContent = matchContent.Substring(0, matchContent.Length - 1);
                        matchStartIndex = Math.Min(matchStartIndex + prefixText.Length, matchContent.Length);
                    }
                    else
                    {
                        matchStartIndex += prefixText.Length;
                    }
                }
            }

            var result = new SearchResult
            {
                FilePath = GetRelativePathCached(context, task),
                LineNumber = SaturateLineNumber(matchLineNumber),
                LongLineNumber = matchLineNumber,
                MatchContent = matchContent,
                MatchStartIndex = matchStartIndex,
                MatchLength = matchLength
            };

            if (contextLines > 0)
            {
                if (resolvedContextBefore == null)
                {
                    resolvedContextBefore = GetContextBeforeWithCarry(fileData, lineStart, minLookbackOffset, contextLines, context, chunkStartOffset);
                    if (resolvedContextBefore.Count < contextLines && chunkStartOffset > 0 && lineStart < minLookbackOffset)
                    {
                        resolvedContextBefore = ReadContextBeforeFromFile(task.FullPath, chunkStartOffset + lineStart, contextLines);
                    }
                }
                result.ContextBefore = resolvedContextBefore;
                var after = GetContextAfter(fileData, lineEnd, contextLines, isLastChunk, out bool afterHitOpenLine);
                result.ContextAfter = after;
                TrackPendingContextAfter(context, result, after.Count, contextLines, isLastChunk, lineEnd < fileData.Length, afterHitOpenLine);
            }

            results.Add(result);
        }

        private void SearchRegex(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            Regex regex,
            int contextLines,
            List<SearchResult> results,
            FileSearchContext context,
            long chunkStartOffset,
            long logicalStartOffset,
            long logicalEndOffset,
            bool isLastChunk,
            byte precedingByte,
            Rune precedingRune)
        {
            // Rent a bounded char buffer (UTF-8 byte length is an upper bound on UTF-16 char count)
            int maxCharCount = Math.Max(1, fileData.Length);
            char[] charBuffer = ArrayPool<char>.Shared.Rent(maxCharCount);

            try
            {
                int overlap = (int)(logicalStartOffset - chunkStartOffset);
                int logicalEndIndex = (int)(logicalEndOffset - chunkStartOffset);

                // Single-pass UTF-8 decode across overlap, logical body, and any trailing slice
                int logicalStartChar = overlap == 0
                    ? 0
                    : Encoding.UTF8.GetChars(fileData.Slice(0, overlap), charBuffer);
                int logicalBodyChars = logicalEndIndex > overlap
                    ? Encoding.UTF8.GetChars(fileData.Slice(overlap, logicalEndIndex - overlap), charBuffer.AsSpan(logicalStartChar))
                    : 0;
                int logicalEndChar = logicalStartChar + logicalBodyChars;
                int tailChars = fileData.Length > logicalEndIndex
                    ? Encoding.UTF8.GetChars(fileData.Slice(logicalEndIndex), charBuffer.AsSpan(logicalEndChar))
                    : 0;
                int charCount = logicalEndChar + tailChars;

                ReadOnlySpan<char> fileChars = charBuffer.AsSpan(0, charCount);

                int minLookbackByteOffset = overlap;
                int minLookbackCharOffset = logicalStartChar;

                bool continuesOpenLine = logicalStartOffset > 0 && context.IsOpenLine;
                bool openLineClosesHere = false;
                int openLineEndInChunk = -1;
                int openLineEndChar = -1;
                int firstNlByteRel = -1;
                string? fullOpenLineContent = null;
                List<string>? openLineContextAfter = null;
                bool openLineAfterHitOpenLine = false;
                int prevOpenLineCharCount = context.OpenLineCharCount;
                long prevOpenLineNumber = context.OpenLineNumber;
                List<string>? prevOpenLineContextBefore = null;

                if (continuesOpenLine)
                {
                    firstNlByteRel = fileData.Slice(overlap, logicalEndIndex - overlap).IndexOf((byte)'\n');
                    openLineClosesHere = firstNlByteRel >= 0 || isLastChunk;
                    openLineEndInChunk = firstNlByteRel >= 0 ? overlap + firstNlByteRel : logicalEndIndex;
                    if (firstNlByteRel >= 0)
                    {
                        int nlCharRel = fileChars.Slice(logicalStartChar, logicalEndChar - logicalStartChar).IndexOf('\n');
                        openLineEndChar = nlCharRel >= 0 ? logicalStartChar + nlCharRel : logicalEndChar;
                    }
                    else
                    {
                        openLineEndChar = logicalEndChar;
                    }

                    if (openLineClosesHere)
                    {
                        FinalizeOpenLine(
                            context,
                            fileData,
                            overlap,
                            openLineEndInChunk,
                            logicalEndIndex,
                            contextLines,
                            isLastChunk,
                            firstNlByteRel >= 0,
                            results,
                            out fullOpenLineContent,
                            out openLineContextAfter,
                            out openLineAfterHitOpenLine,
                            out prevOpenLineCharCount,
                            out prevOpenLineNumber,
                            out prevOpenLineContextBefore);
                        minLookbackByteOffset = firstNlByteRel >= 0 ? openLineEndInChunk + 1 : logicalEndIndex;
                        minLookbackCharOffset = firstNlByteRel >= 0 ? openLineEndChar + 1 : logicalEndChar;
                    }
                }

                bool hasNewTrailingOpenLine = !isLastChunk &&
                                              logicalEndIndex > overlap &&
                                              fileData[logicalEndIndex - 1] != (byte)'\n' &&
                                              (!continuesOpenLine || openLineClosesHere);
                int newOpenLineStartByte = logicalEndIndex;
                int newOpenLineStartChar = logicalEndChar;
                List<string>? newOpenLineContextBefore = null;
                if (hasNewTrailingOpenLine)
                {
                    int searchFrom = minLookbackByteOffset;
                    int trailingNlRel = searchFrom < logicalEndIndex
                        ? fileData.Slice(searchFrom, logicalEndIndex - searchFrom).LastIndexOf((byte)'\n')
                        : -1;
                    newOpenLineStartByte = trailingNlRel >= 0 ? searchFrom + trailingNlRel + 1 : searchFrom;
                    if (trailingNlRel >= 0)
                    {
                        newOpenLineStartChar = fileChars.Slice(0, logicalEndChar).LastIndexOf('\n') + 1;
                    }
                    else
                    {
                        newOpenLineStartChar = openLineClosesHere ? openLineEndChar + 1 : logicalStartChar;
                    }

                    if (contextLines > 0)
                    {
                        newOpenLineContextBefore = GetContextBeforeCharsWithCarry(
                            fileChars,
                            newOpenLineStartChar,
                            minLookbackCharOffset,
                            contextLines,
                            context,
                            chunkStartOffset);
                    }
                }

                var enumerator = regex.EnumerateMatches(fileChars);
                int lastNewlineOffset = logicalStartChar;
                long currentLineNumber = context.GlobalLineNumber;

                while (enumerator.MoveNext())
                {
                    var match = enumerator.Current;
                    int matchStart = match.Index;
                    int matchLength = match.Length;
                    int matchEnd = matchStart + matchLength;

                    // Defer end-touching matches on non-final chunks so cross-boundary matches are owned cleanly by chunk k+1
                    if (!isLastChunk && matchEnd >= logicalEndChar)
                    {
                        continue;
                    }

                    // Skip matches completely owned by the previous chunk
                    if (logicalStartOffset > 0 && (matchLength > 0 ? matchEnd < logicalStartChar : matchStart < logicalStartChar))
                    {
                        continue;
                    }

                    // Suppress false ^ (when precedingByte != '\n'), \A (always when chunkStartOffset > 0), and \b/\B anchor matches at chunkStartOffset
                    if (matchStart == 0 && chunkStartOffset > 0)
                    {
                        if (!IsTrueMatchAtChunkStart(regex, fileChars, matchEnd, matchLength, precedingRune))
                        {
                            continue;
                        }
                    }

                    // Find line boundaries in char space
                    int lineStart = fileChars.Slice(0, matchStart).LastIndexOf('\n') + 1;
                    int lineEnd = fileChars.Slice(matchStart).IndexOf('\n');
                    if (lineEnd < 0) lineEnd = fileChars.Length;
                    else lineEnd += matchStart;

                    long matchLineNumber;
                    if (lineStart > lastNewlineOffset)
                    {
                        currentLineNumber += fileChars.Slice(lastNewlineOffset, lineStart - lastNewlineOffset).Count('\n');
                        lastNewlineOffset = lineStart;
                        matchLineNumber = currentLineNumber;
                    }
                    else if (lineStart < lastNewlineOffset)
                    {
                        matchLineNumber = Math.Max(1L, currentLineNumber - fileChars.Slice(lineStart, lastNewlineOffset - lineStart).Count('\n'));
                    }
                    else
                    {
                        matchLineNumber = currentLineNumber;
                    }

                    if (continuesOpenLine && !openLineClosesHere && lineEnd == logicalEndChar)
                    {
                        int lineCharOffset = prevOpenLineCharCount + (matchStart - logicalStartChar);
                        var pendingResult = new SearchResult
                        {
                            FilePath = GetRelativePathCached(context, task),
                            LineNumber = SaturateLineNumber(context.OpenLineNumber),
                            LongLineNumber = context.OpenLineNumber,
                            MatchLength = matchLength,
                            ContextBefore = contextLines > 0 ? CloneList(context.OpenLineContextBefore) : new List<string>()
                        };
                        context.PendingOpenLineMatches ??= new List<(SearchResult, int, bool)>();
                        context.PendingOpenLineMatches.Add((pendingResult, lineCharOffset, contextLines > 0));
                    }
                    else if (openLineClosesHere && lineStart <= openLineEndChar && lineEnd == openLineEndChar)
                    {
                        int lineCharOffset = prevOpenLineCharCount + (matchStart - logicalStartChar);
                        int relStart = Math.Clamp(lineCharOffset, 0, fullOpenLineContent!.Length);
                        int relLength = Math.Clamp(matchLength, 0, fullOpenLineContent.Length - relStart);

                        var closedResult = new SearchResult
                        {
                            FilePath = GetRelativePathCached(context, task),
                            LineNumber = SaturateLineNumber(prevOpenLineNumber),
                            LongLineNumber = prevOpenLineNumber,
                            MatchContent = fullOpenLineContent,
                            MatchStartIndex = relStart,
                            MatchLength = relLength
                        };

                        if (contextLines > 0)
                        {
                            closedResult.ContextBefore = CloneList(prevOpenLineContextBefore);
                            var after = CloneList(openLineContextAfter);
                            closedResult.ContextAfter = after;
                            TrackPendingContextAfter(context, closedResult, after.Count, contextLines, isLastChunk, firstNlByteRel >= 0, openLineAfterHitOpenLine);
                        }

                        results.Add(closedResult);
                    }
                    else if (hasNewTrailingOpenLine && lineEnd == logicalEndChar)
                    {
                        int lineCharOffset = matchStart - newOpenLineStartChar;
                        var pendingResult = new SearchResult
                        {
                            FilePath = GetRelativePathCached(context, task),
                            LineNumber = SaturateLineNumber(matchLineNumber),
                            LongLineNumber = matchLineNumber,
                            MatchLength = matchLength,
                            ContextBefore = contextLines > 0 ? CloneList(newOpenLineContextBefore) : new List<string>()
                        };
                        context.PendingOpenLineMatches ??= new List<(SearchResult, int, bool)>();
                        context.PendingOpenLineMatches.Add((pendingResult, lineCharOffset, contextLines > 0));
                    }
                    else
                    {
                        ReadOnlySpan<char> lineSpan = fileChars.Slice(lineStart, lineEnd - lineStart);
                        if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                            lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);

                        int relStart = Math.Clamp(matchStart - lineStart, 0, lineSpan.Length);
                        int relLength = Math.Clamp(matchLength, 0, lineSpan.Length - relStart);
                        string matchContent = new string(lineSpan);
                        List<string>? resolvedContextBefore = null;

                        if (lineStart == 0 && chunkStartOffset > 0 && precedingByte != (byte)'\n')
                        {
                            (string prefixText, resolvedContextBefore) = ReadMissingLinePrefixAndContext(task.FullPath, chunkStartOffset, contextLines);
                            if (prefixText.Length > 0)
                            {
                                matchContent = string.Concat(prefixText, matchContent);
                                if (lineSpan.IsEmpty && matchContent[^1] == '\r')
                                {
                                    matchContent = matchContent.Substring(0, matchContent.Length - 1);
                                    relStart = Math.Min(relStart + prefixText.Length, matchContent.Length);
                                }
                                else
                                {
                                    relStart += prefixText.Length;
                                }
                            }
                        }

                        var result = new SearchResult
                        {
                            FilePath = GetRelativePathCached(context, task),
                            LineNumber = SaturateLineNumber(matchLineNumber),
                            LongLineNumber = matchLineNumber,
                            MatchContent = matchContent,
                            MatchStartIndex = relStart,
                            MatchLength = relLength
                        };

                        if (contextLines > 0)
                        {
                            if (resolvedContextBefore == null)
                            {
                                resolvedContextBefore = GetContextBeforeCharsWithCarry(
                                    fileChars,
                                    lineStart,
                                    minLookbackCharOffset,
                                    contextLines,
                                    context,
                                    chunkStartOffset);
                                if (resolvedContextBefore.Count < contextLines && chunkStartOffset > 0 && lineStart < minLookbackCharOffset)
                                {
                                    long trueLineStartOffset = lineStart == 0
                                        ? chunkStartOffset
                                        : chunkStartOffset + Encoding.UTF8.GetByteCount(fileChars.Slice(0, lineStart));
                                    resolvedContextBefore = ReadContextBeforeFromFile(task.FullPath, trueLineStartOffset, contextLines);
                                }
                            }
                            result.ContextBefore = resolvedContextBefore;
                            var after = GetContextAfterChars(fileChars, lineEnd, contextLines, isLastChunk, out bool afterHitOpenLine);
                            result.ContextAfter = after;
                            TrackPendingContextAfter(context, result, after.Count, contextLines, isLastChunk, lineEnd < fileChars.Length, afterHitOpenLine);
                        }

                        results.Add(result);
                    }
                }

                if (logicalEndChar > lastNewlineOffset)
                {
                    currentLineNumber += fileChars.Slice(lastNewlineOffset, logicalEndChar - lastNewlineOffset).Count('\n');
                }

                if (!isLastChunk)
                {
                    if (continuesOpenLine && !openLineClosesHere)
                    {
                        context.OpenLineBuffer ??= new ArrayBufferWriter<byte>();
                        context.OpenLineBuffer.Write(fileData.Slice(overlap, logicalEndIndex - overlap));
                        context.OpenLineCharCount += (logicalEndChar - logicalStartChar);
                    }
                    else if (hasNewTrailingOpenLine)
                    {
                        if (contextLines > 0)
                        {
                            UpdateTrailingContextLines(fileData, minLookbackByteOffset, newOpenLineStartByte, contextLines, context);
                        }
                        StartOpenLine(context, fileData, newOpenLineStartByte, logicalEndIndex, chunkStartOffset, currentLineNumber, newOpenLineContextBefore);
                    }
                    else if (contextLines > 0)
                    {
                        UpdateTrailingContextLines(fileData, minLookbackByteOffset, logicalEndIndex, contextLines, context);
                    }
                }

                context.GlobalLineNumber = currentLineNumber;
                context.LastProcessedFileOffset = logicalEndOffset;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }

        private static bool IsTrueMatchAtChunkStart(Regex regex, ReadOnlySpan<char> fileChars, int matchEnd, int matchLength, Rune precedingRune)
        {
            Span<char> prefixBuf = stackalloc char[2];
            int prefixLen = precedingRune.EncodeToUtf16(prefixBuf);
            int sliceLen = Math.Min(fileChars.Length, matchEnd + 64);
            int totalLen = prefixLen + sliceLen;
            char[]? rented = null;
            Span<char> buf = totalLen <= 512
                ? stackalloc char[512]
                : (rented = ArrayPool<char>.Shared.Rent(totalLen));
            try
            {
                Span<char> testSpan = buf.Slice(0, totalLen);
                prefixBuf.Slice(0, prefixLen).CopyTo(testSpan);
                fileChars.Slice(0, sliceLen).CopyTo(testSpan.Slice(prefixLen));
                foreach (ValueMatch m in regex.EnumerateMatches(testSpan, prefixLen))
                {
                    if (m.Index == prefixLen && m.Length == matchLength)
                    {
                        return true;
                    }
                    if (m.Index > prefixLen)
                    {
                        break;
                    }
                }
                return false;
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }
        }

        private static (string PrefixText, List<string>? ContextBefore) ReadMissingLinePrefixAndContext(
            string fullPath,
            long chunkStartOffset,
            int contextLines)
        {
            using SafeFileHandle handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.RandomAccess);

            long prevNl = FindLastNewlineBefore(handle, chunkStartOffset);
            long trueLineStart = prevNl >= 0 ? prevNl + 1 : 0;
            string prefixText = ReadUtf8Slice(handle, trueLineStart, chunkStartOffset, trimTrailingCr: false);

            List<string>? contextBefore = contextLines > 0
                ? ReadContextBeforeCore(handle, prevNl, contextLines)
                : null;

            return (prefixText, contextBefore);
        }

        private static List<string> ReadContextBeforeFromFile(
            string fullPath,
            long trueLineStartOffset,
            int contextLines)
        {
            if (trueLineStartOffset <= 0 || contextLines <= 0)
            {
                return new List<string>();
            }

            using SafeFileHandle handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.RandomAccess);

            long prevNl = FindLastNewlineBefore(handle, trueLineStartOffset);
            if (prevNl < 0)
            {
                return new List<string>();
            }

            return ReadContextBeforeCore(handle, prevNl, contextLines);
        }

        private static List<string> ReadContextBeforeCore(SafeFileHandle handle, long prevNl, int contextLines)
        {
            var contextBefore = new List<string>(contextLines);
            long lineEnd = prevNl;
            for (int i = 0; i < contextLines && lineEnd >= 0; i++)
            {
                long earlierNl = FindLastNewlineBefore(handle, lineEnd);
                long lineStart = earlierNl >= 0 ? earlierNl + 1 : 0;
                contextBefore.Add(ReadUtf8Slice(handle, lineStart, lineEnd, trimTrailingCr: true));
                lineEnd = earlierNl;
            }
            contextBefore.Reverse();
            return contextBefore;
        }

        private static long FindLastNewlineBefore(SafeFileHandle handle, long beforeOffset)
        {
            if (beforeOffset <= 0) return -1;
            Span<byte> buf = stackalloc byte[4096];
            long pos = beforeOffset;
            while (pos > 0)
            {
                int toRead = (int)Math.Min(4096L, pos);
                long readStart = pos - toRead;
                int bytesRead = RandomAccess.Read(handle, buf.Slice(0, toRead), readStart);
                if (bytesRead <= 0) break;
                int nl = buf.Slice(0, bytesRead).LastIndexOf((byte)'\n');
                if (nl >= 0) return readStart + nl;
                pos = readStart;
            }
            return -1;
        }

        private static string ReadUtf8Slice(SafeFileHandle handle, long start, long end, bool trimTrailingCr)
        {
            long len64 = end - start;
            if (len64 <= 0) return string.Empty;
            int len = checked((int)len64);
            byte[]? rented = null;
            Span<byte> span = len <= 4096
                ? stackalloc byte[4096]
                : (rented = ArrayPool<byte>.Shared.Rent(len));
            try
            {
                Span<byte> dest = span.Slice(0, len);
                int totalRead = 0;
                while (totalRead < len)
                {
                    int read = RandomAccess.Read(handle, dest.Slice(totalRead), start + totalRead);
                    if (read <= 0) break;
                    totalRead += read;
                }
                ReadOnlySpan<byte> bytes = dest.Slice(0, totalRead);
                if (trimTrailingCr && bytes.Length > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes = bytes.Slice(0, bytes.Length - 1);
                }
                return bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        private void SearchInverted(
            ReadOnlySpan<byte> fileData,
            FileSearchTask task,
            in SearchPlan plan,
            bool caseSensitive,
            int contextLines,
            List<SearchResult> results,
            FileSearchContext context,
            long chunkStartOffset,
            long logicalStartOffset,
            long logicalEndOffset,
            bool isLastChunk)
        {
            int overlap = (int)(logicalStartOffset - chunkStartOffset);
            int logicalEndIndex = (int)(logicalEndOffset - chunkStartOffset);
            ReadOnlySpan<byte> logicalData = fileData.Slice(0, logicalEndIndex);

            int offset = 0;
            int minLookbackOffset = overlap;
            long lineNumber = context.GlobalLineNumber;
            char[]? charBuffer = plan.Regex != null ? ArrayPool<char>.Shared.Rent(16384) : null;
            const int MaxContextPartialLineBytes = 16 * 1024 * 1024;

            try
            {
                // Continue an open partial line that started in a prior chunk (> MaxChunkSize + lookahead)
                if (logicalStartOffset > 0 && context.IsAccumulatingPartialLine)
                {
                    int nlRel = logicalData.Slice(overlap).IndexOf((byte)'\n');
                    bool foundNewline = nlRel >= 0;
                    int segmentEnd = foundNewline ? overlap + nlRel : logicalEndIndex;
                    ReadOnlySpan<byte> segment = logicalData.Slice(overlap, segmentEnd - overlap);

                    if (!context.PartialLineMatched)
                    {
                        if (plan.PatternClass == RegexPatternClass.Literal && plan.PrimaryBytes.Length > 0)
                        {
                            int m = plan.PrimaryBytes.Length;
                            if (overlap >= m - 1)
                            {
                                int checkStart = Math.Max(0, overlap - (m - 1));
                                ReadOnlySpan<byte> checkSpan = logicalData.Slice(checkStart, segmentEnd - checkStart);
                                if (IndexOfLiteralBytes(checkSpan, plan.PrimaryBytes, plan.PrimaryMatcher, caseSensitive) >= 0)
                                {
                                    context.PartialLineMatched = true;
                                }
                            }
                            else
                            {
                                // Overlap is smaller than pattern length - 1; check segment and boundary window against PartialLineBuffer tail
                                if (IndexOfLiteralBytes(segment, plan.PrimaryBytes, plan.PrimaryMatcher, caseSensitive) >= 0)
                                {
                                    context.PartialLineMatched = true;
                                }
                                else if (context.PartialLineBuffer != null && context.PartialLineBuffer.WrittenCount > 0)
                                {
                                    ReadOnlySpan<byte> prev = context.PartialLineBuffer.WrittenSpan;
                                    int prevTailLen = Math.Min(prev.Length, m - 1);
                                    int segHeadLen = Math.Min(segment.Length, m - 1);
                                    int boundaryLen = prevTailLen + segHeadLen;
                                    if (boundaryLen >= m)
                                    {
                                        byte[]? rentedBoundary = null;
                                        Span<byte> boundarySpan = boundaryLen <= 512
                                            ? stackalloc byte[512]
                                            : (rentedBoundary = ArrayPool<byte>.Shared.Rent(boundaryLen));
                                        try
                                        {
                                            Span<byte> window = boundarySpan.Slice(0, boundaryLen);
                                            prev.Slice(prev.Length - prevTailLen).CopyTo(window);
                                            segment.Slice(0, segHeadLen).CopyTo(window.Slice(prevTailLen));
                                            if (IndexOfLiteralBytes(window, plan.PrimaryBytes, plan.PrimaryMatcher, caseSensitive) >= 0)
                                            {
                                                context.PartialLineMatched = true;
                                            }
                                        }
                                        finally
                                        {
                                            if (rentedBoundary != null)
                                            {
                                                ArrayPool<byte>.Shared.Return(rentedBoundary);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (!context.PartialLineMatched ||
                            (contextLines > 0 && (context.PartialLineBuffer?.WrittenCount ?? 0) + segment.Length <= MaxContextPartialLineBytes))
                        {
                            context.PartialLineBuffer ??= new ArrayBufferWriter<byte>();
                            context.PartialLineBuffer.Write(segment);
                            if (context.PartialLineMatched)
                            {
                                context.PendingContextBefore = null;
                            }
                        }
                        else
                        {
                            context.PartialLineBuffer?.Clear();
                            context.PendingContextBefore = null;
                        }
                    }
                    else if (contextLines > 0 &&
                             context.PartialLineBuffer != null &&
                             context.PartialLineBuffer.WrittenCount + segment.Length <= MaxContextPartialLineBytes)
                    {
                        context.PartialLineBuffer.Write(segment);
                    }
                    else if (context.PartialLineBuffer != null && context.PartialLineBuffer.WrittenCount > 0)
                    {
                        context.PartialLineBuffer.Clear();
                    }

                    if (!foundNewline && !isLastChunk)
                    {
                        context.GlobalLineNumber = lineNumber;
                        context.LastProcessedFileOffset = logicalEndOffset;
                        return;
                    }

                    ReadOnlySpan<byte> rawFullLine = context.PartialLineBuffer != null
                        ? context.PartialLineBuffer.WrittenSpan
                        : ReadOnlySpan<byte>.Empty;
                    ReadOnlySpan<byte> cleanFullLine = (rawFullLine.Length > 0 && rawFullLine[^1] == '\r')
                        ? rawFullLine.Slice(0, rawFullLine.Length - 1)
                        : rawFullLine;

                    string? closedLineContent = null;
                    if (!context.PartialLineMatched)
                    {
                        bool hasMatch = EvaluateInvertedLineMatch(rawFullLine, in plan, caseSensitive, ref charBuffer);
                        if (!hasMatch)
                        {
                            closedLineContent = Encoding.UTF8.GetString(cleanFullLine);
                            var result = new SearchResult
                            {
                                FilePath = GetRelativePathCached(context, task),
                                LineNumber = SaturateLineNumber(lineNumber),
                                LongLineNumber = lineNumber,
                                MatchContent = closedLineContent,
                                MatchStartIndex = 0,
                                MatchLength = closedLineContent.Length
                            };

                            if (contextLines > 0)
                            {
                                result.ContextBefore = CloneList(context.PendingContextBefore);
                                bool afterHitOpenLine = false;
                                var after = foundNewline
                                    ? GetContextAfter(fileData, segmentEnd, contextLines, isLastChunk, out afterHitOpenLine)
                                    : new List<string>();
                                result.ContextAfter = after;
                                TrackPendingContextAfter(context, result, after.Count, contextLines, isLastChunk, foundNewline, afterHitOpenLine);
                            }

                            results.Add(result);
                        }
                    }

                    if (contextLines > 0)
                    {
                        closedLineContent ??= Encoding.UTF8.GetString(cleanFullLine);
                        if (context.PendingContextAfter is { Count: > 0 })
                        {
                            FulfillPendingContextAfterWithOpenLine(context, closedLineContent, fileData, segmentEnd, logicalEndIndex, isLastChunk, foundNewline);
                        }
                        if (foundNewline)
                        {
                            PushTrailingContextLine(context, closedLineContent, contextLines);
                        }
                    }

                    context.IsAccumulatingPartialLine = false;
                    context.PartialLineMatched = false;
                    context.PartialLineBuffer?.Clear();
                    context.PendingContextBefore = null;
                    lineNumber++;
                    offset = foundNewline ? segmentEnd + 1 : logicalEndIndex;
                    minLookbackOffset = offset;
                }
                else if (overlap > 0)
                {
                    // Skip overlap prefix already covered by previous chunk
                    offset = overlap;
                }

                while (offset < logicalEndIndex)
                {
                    int lineEndRel = logicalData.Slice(offset).IndexOf((byte)'\n');
                    if (lineEndRel < 0 && !isLastChunk)
                    {
                        // Line spans beyond this chunk; begin partial-line accumulation
                        ReadOnlySpan<byte> prefix = logicalData.Slice(offset);
                        context.IsAccumulatingPartialLine = true;
                        context.PartialLineMatched = false;

                        if (plan.PatternClass == RegexPatternClass.Literal && plan.PrimaryBytes.Length > 0)
                        {
                            context.PartialLineMatched = IndexOfLiteralBytes(prefix, plan.PrimaryBytes, plan.PrimaryMatcher, caseSensitive) >= 0;
                        }

                        if (!context.PartialLineMatched)
                        {
                            context.PartialLineBuffer ??= new ArrayBufferWriter<byte>();
                            context.PartialLineBuffer.Clear();
                            context.PartialLineBuffer.Write(prefix);
                            context.PendingContextBefore = contextLines > 0
                                ? GetContextBeforeWithCarry(fileData, offset, minLookbackOffset, contextLines, context, chunkStartOffset)
                                : null;
                        }
                        else if (contextLines > 0 && prefix.Length <= MaxContextPartialLineBytes)
                        {
                            context.PartialLineBuffer ??= new ArrayBufferWriter<byte>();
                            context.PartialLineBuffer.Clear();
                            context.PartialLineBuffer.Write(prefix);
                            context.PendingContextBefore = null;
                        }
                        else
                        {
                            context.PartialLineBuffer?.Clear();
                            context.PendingContextBefore = null;
                        }

                        if (contextLines > 0)
                        {
                            UpdateTrailingContextLines(fileData, minLookbackOffset, offset, contextLines, context);
                        }

                        context.GlobalLineNumber = lineNumber;
                        context.LastProcessedFileOffset = logicalEndOffset;
                        return;
                    }

                    int currentLineEnd = lineEndRel < 0 ? logicalEndIndex : offset + lineEndRel;
                    int nextOffset = lineEndRel < 0 ? logicalEndIndex : currentLineEnd + 1;

                    ReadOnlySpan<byte> rawLineSpan = logicalData.Slice(offset, currentLineEnd - offset);
                    bool hasMatch = EvaluateInvertedLineMatch(rawLineSpan, in plan, caseSensitive, ref charBuffer);

                    if (!hasMatch)
                    {
                        ReadOnlySpan<byte> cleanLineSpan = (rawLineSpan.Length > 0 && rawLineSpan[^1] == '\r')
                            ? rawLineSpan.Slice(0, rawLineSpan.Length - 1)
                            : rawLineSpan;
                        string matchContent = Encoding.UTF8.GetString(cleanLineSpan);

                        var result = new SearchResult
                        {
                            FilePath = GetRelativePathCached(context, task),
                            LineNumber = SaturateLineNumber(lineNumber),
                            LongLineNumber = lineNumber,
                            MatchContent = matchContent,
                            MatchStartIndex = 0,
                            MatchLength = matchContent.Length
                        };

                        if (contextLines > 0)
                        {
                            result.ContextBefore = GetContextBeforeWithCarry(fileData, offset, minLookbackOffset, contextLines, context, chunkStartOffset);
                            var after = GetContextAfter(fileData, currentLineEnd, contextLines, isLastChunk, out bool afterHitOpenLine);
                            result.ContextAfter = after;
                            TrackPendingContextAfter(context, result, after.Count, contextLines, isLastChunk, currentLineEnd < logicalEndIndex && logicalData[currentLineEnd] == (byte)'\n', afterHitOpenLine);
                        }

                        results.Add(result);
                    }

                    lineNumber++;
                    offset = nextOffset;
                }

                if (!isLastChunk && contextLines > 0)
                {
                    UpdateTrailingContextLines(fileData, minLookbackOffset, logicalEndIndex, contextLines, context);
                }

                context.GlobalLineNumber = lineNumber;
                context.LastProcessedFileOffset = logicalEndOffset;
            }
            finally
            {
                if (charBuffer != null)
                {
                    ArrayPool<char>.Shared.Return(charBuffer);
                }
            }
        }

        private static bool EvaluateInvertedLineMatch(
            ReadOnlySpan<byte> lineSpan,
            in SearchPlan plan,
            bool caseSensitive,
            ref char[]? charBuffer)
        {
            switch (plan.PatternClass)
            {
                case RegexPatternClass.FullRegex:
                {
                    Regex regex = plan.Regex!;
                    int requiredChars = Encoding.UTF8.GetMaxCharCount(lineSpan.Length);
                    if (charBuffer == null || requiredChars > charBuffer.Length)
                    {
                        if (charBuffer != null) ArrayPool<char>.Shared.Return(charBuffer);
                        charBuffer = ArrayPool<char>.Shared.Rent(Math.Max(1, requiredChars));
                    }

                    int charCount = Encoding.UTF8.GetChars(lineSpan, charBuffer);
                    return regex.IsMatch(charBuffer.AsSpan(0, charCount));
                }

                case RegexPatternClass.Literal:
                    if (plan.PrimaryBytes.Length == 0) return false;
                    return IndexOfLiteralBytes(lineSpan, plan.PrimaryBytes, plan.PrimaryMatcher, caseSensitive) >= 0;

                case RegexPatternClass.StartsWith:
                    return StartsWithLiteralBytes(lineSpan, plan.PrimaryBytes, caseSensitive);

                case RegexPatternClass.EndsWith:
                    return EndsWithLiteralBytes(lineSpan, plan.PrimaryBytes, caseSensitive);

                case RegexPatternClass.Equals:
                    return lineSpan.Length == plan.PrimaryBytes.Length &&
                           StartsWithLiteralBytes(lineSpan, plan.PrimaryBytes, caseSensitive);

                case RegexPatternClass.PrefixAndSuffix:
                    return lineSpan.Length >= plan.PrimaryBytes.Length + plan.SecondaryBytes.Length &&
                           StartsWithLiteralBytes(lineSpan, plan.PrimaryBytes, caseSensitive) &&
                           EndsWithLiteralBytes(lineSpan, plan.SecondaryBytes, caseSensitive);

                case RegexPatternClass.ContainsBothOrdered:
                {
                    if (lineSpan.Length < plan.PrimaryBytes.Length + plan.SecondaryBytes.Length) return false;
                    int idx1 = IndexOfLiteralBytes(lineSpan, plan.PrimaryBytes, plan.PrimaryMatcher, caseSensitive);
                    if (idx1 < 0) return false;
                    return IndexOfLiteralBytes(
                        lineSpan.Slice(idx1 + plan.PrimaryBytes.Length),
                        plan.SecondaryBytes,
                        plan.SecondaryMatcher,
                        caseSensitive) >= 0;
                }

                default:
                    return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IndexOfLiteralBytes(
            ReadOnlySpan<byte> haystack,
            byte[] pattern,
            AsciiIgnoreCaseMatcher? matcher,
            bool caseSensitive)
        {
            if (caseSensitive)
            {
                return haystack.IndexOf(pattern);
            }
            return matcher!.IndexOf(haystack);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int LastIndexOfLiteralBytes(
            ReadOnlySpan<byte> haystack,
            byte[] pattern,
            AsciiIgnoreCaseMatcher? matcher,
            bool caseSensitive)
        {
            if (caseSensitive)
            {
                return haystack.LastIndexOf(pattern);
            }
            return matcher!.LastIndexOf(haystack);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool StartsWithLiteralBytes(ReadOnlySpan<byte> line, byte[] prefix, bool caseSensitive)
        {
            if (line.Length < prefix.Length) return false;
            return caseSensitive
                ? line.Slice(0, prefix.Length).SequenceEqual(prefix)
                : EqualsIgnoreCaseAscii(line.Slice(0, prefix.Length), prefix);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EndsWithLiteralBytes(ReadOnlySpan<byte> line, byte[] suffix, bool caseSensitive)
        {
            if (line.Length < suffix.Length) return false;
            ReadOnlySpan<byte> tail = line.Slice(line.Length - suffix.Length);
            return caseSensitive
                ? tail.SequenceEqual(suffix)
                : EqualsIgnoreCaseAscii(tail, suffix);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EqualsIgnoreCaseAscii(ReadOnlySpan<byte> span, ReadOnlySpan<byte> pattern)
        {
            if (span.Length < pattern.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                byte b1 = span[i];
                byte b2 = pattern[i];
                if (b1 != b2)
                {
                    int lower1 = b1 | 0x20;
                    int lower2 = b2 | 0x20;
                    if (lower1 != lower2 || (uint)(lower1 - 'a') > (uint)('z' - 'a'))
                        return false;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetUtf16CharCount(ReadOnlySpan<byte> utf8Bytes)
        {
            if (utf8Bytes.IsEmpty) return 0;
            return Ascii.IsValid(utf8Bytes) ? utf8Bytes.Length : Encoding.UTF8.GetCharCount(utf8Bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string DecodeTrimmedLine(ReadOnlySpan<byte> lineBytes)
        {
            if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
                lineBytes = lineBytes.Slice(0, lineBytes.Length - 1);
            return lineBytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(lineBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static List<string> CloneList(List<string>? source)
        {
            return source is { Count: > 0 } ? new List<string>(source) : new List<string>();
        }

        private static void StartOpenLine(
            FileSearchContext context,
            ReadOnlySpan<byte> fileData,
            int newOpenLineStart,
            int logicalEndIndex,
            long chunkStartOffset,
            long lineNumber,
            List<string>? contextBefore)
        {
            context.IsOpenLine = true;
            context.OpenLineFileStartOffset = chunkStartOffset + newOpenLineStart;
            context.OpenLineNumber = lineNumber;
            context.OpenLineBuffer ??= new ArrayBufferWriter<byte>();
            context.OpenLineBuffer.Clear();
            ReadOnlySpan<byte> tail = fileData.Slice(newOpenLineStart, logicalEndIndex - newOpenLineStart);
            context.OpenLineBuffer.Write(tail);
            context.OpenLineCharCount = GetUtf16CharCount(tail);
            context.OpenLineContextBefore = contextBefore;
        }

        private static void FinalizeOpenLine(
            FileSearchContext context,
            ReadOnlySpan<byte> fileData,
            int overlap,
            int openLineEndInChunk,
            int logicalEndIndex,
            int contextLines,
            bool isLastChunk,
            bool foundNewline,
            List<SearchResult> results,
            out string fullLineContent,
            out List<string> openLineAfter,
            out bool openLineAfterHitOpenLine,
            out int prevCharCount,
            out long prevLineNumber,
            out List<string>? prevContextBefore)
        {
            context.OpenLineBuffer ??= new ArrayBufferWriter<byte>();
            if (openLineEndInChunk > overlap)
            {
                context.OpenLineBuffer.Write(fileData.Slice(overlap, openLineEndInChunk - overlap));
            }

            fullLineContent = DecodeTrimmedLine(context.OpenLineBuffer.WrittenSpan);
            openLineAfterHitOpenLine = false;
            openLineAfter = (contextLines > 0 && foundNewline)
                ? GetContextAfter(fileData, openLineEndInChunk, contextLines, isLastChunk, out openLineAfterHitOpenLine)
                : new List<string>();
            prevCharCount = context.OpenLineCharCount;
            prevLineNumber = context.OpenLineNumber;
            prevContextBefore = context.OpenLineContextBefore;

            if (contextLines > 0 && context.PendingContextAfter is { Count: > 0 })
            {
                FulfillPendingContextAfterWithOpenLine(
                    context,
                    fullLineContent,
                    fileData,
                    openLineEndInChunk,
                    logicalEndIndex,
                    isLastChunk,
                    foundNewline);
            }

            if (context.PendingOpenLineMatches is { Count: > 0 } pending)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    var (result, lineCharOffset, needsAfter) = pending[i];
                    int clampedStart = Math.Clamp(lineCharOffset, 0, fullLineContent.Length);
                    int clampedLen = Math.Clamp(result.MatchLength, 0, fullLineContent.Length - clampedStart);
                    result.MatchContent = fullLineContent;
                    result.MatchStartIndex = clampedStart;
                    result.MatchLength = clampedLen;
                    if (needsAfter)
                    {
                        var afterCopy = CloneList(openLineAfter);
                        result.ContextAfter = afterCopy;
                        TrackPendingContextAfter(context, result, afterCopy.Count, contextLines, isLastChunk, foundNewline, openLineAfterHitOpenLine);
                    }
                    results.Add(result);
                }
            }

            if (contextLines > 0 && foundNewline)
            {
                PushTrailingContextLine(context, fullLineContent, contextLines);
            }

            context.IsOpenLine = false;
            context.OpenLineFileStartOffset = 0;
            context.OpenLineNumber = 0;
            context.OpenLineCharCount = 0;
            context.OpenLineBuffer.Clear();
            context.OpenLineContextBefore = null;
            context.PendingOpenLineMatches?.Clear();
        }

        private static void PushTrailingContextLine(FileSearchContext context, string line, int contextCount)
        {
            if (contextCount <= 0) return;
            context.TrailingContextLines ??= new List<string>(contextCount);
            if (context.TrailingContextLines.Count >= contextCount)
            {
                context.TrailingContextLines.RemoveAt(0);
            }
            context.TrailingContextLines.Add(line);
        }

        private static void UpdateTrailingContextLines(
            ReadOnlySpan<byte> fileData,
            int regionStart,
            int regionEnd,
            int contextCount,
            FileSearchContext context)
        {
            if (contextCount <= 0 || regionEnd <= regionStart) return;

            ReadOnlySpan<byte> region = fileData.Slice(regionStart, regionEnd - regionStart);
            int lastNl = region.LastIndexOf((byte)'\n');
            if (lastNl < 0) return;

            var collected = new List<string>(Math.Min(contextCount, 8));
            int lineEnd = lastNl;
            for (int i = 0; i < contextCount && lineEnd >= 0; i++)
            {
                int prevNl = lineEnd > 0 ? region.Slice(0, lineEnd).LastIndexOf((byte)'\n') : -1;
                int start = prevNl + 1;
                int len = lineEnd - start;
                collected.Add(len > 0 ? DecodeTrimmedLine(region.Slice(start, len)) : string.Empty);
                lineEnd = prevNl;
            }

            collected.Reverse();
            if (collected.Count >= contextCount)
            {
                context.TrailingContextLines = collected;
                return;
            }

            context.TrailingContextLines ??= new List<string>(contextCount);
            int keep = Math.Min(context.TrailingContextLines.Count, contextCount - collected.Count);
            if (keep < context.TrailingContextLines.Count)
            {
                context.TrailingContextLines.RemoveRange(0, context.TrailingContextLines.Count - keep);
            }
            context.TrailingContextLines.AddRange(collected);
        }

        private static void TrackPendingContextAfter(
            FileSearchContext context,
            SearchResult result,
            int collectedCount,
            int requestedCount,
            bool isLastChunk,
            bool lineEndedWithNewline,
            bool waitingForOpenLine = false)
        {
            if (isLastChunk || collectedCount >= requestedCount || !lineEndedWithNewline)
            {
                return;
            }

            context.PendingContextAfter ??= new List<(SearchResult Result, int Remaining, bool WaitingForOpenLine)>();
            context.PendingContextAfter.Add((result, requestedCount - collectedCount, waitingForOpenLine));
        }

        private static void FillPendingContextAfter(
            ReadOnlySpan<byte> fileData,
            long chunkStartOffset,
            long logicalStartOffset,
            long logicalEndOffset,
            bool isLastChunk,
            FileSearchContext context)
        {
            var pending = context.PendingContextAfter;
            if (pending == null || pending.Count == 0) return;

            int overlap = (int)(logicalStartOffset - chunkStartOffset);
            int logicalEndIndex = (int)(logicalEndOffset - chunkStartOffset);
            if (overlap >= logicalEndIndex) return;

            for (int idx = pending.Count - 1; idx >= 0; idx--)
            {
                var (result, remaining, waitingForOpenLine) = pending[idx];
                if (waitingForOpenLine) continue;

                var list = result.ContextAfter as List<string>;
                if (list == null)
                {
                    list = new List<string>(result.ContextAfter);
                    result.ContextAfter = list;
                }

                int pos = overlap;
                bool nowWaiting = false;
                while (remaining > 0 && pos < logicalEndIndex)
                {
                    int nlRel = fileData.Slice(pos, logicalEndIndex - pos).IndexOf((byte)'\n');
                    if (nlRel < 0 && !isLastChunk)
                    {
                        nowWaiting = true;
                        break;
                    }

                    int lineEnd = nlRel < 0 ? logicalEndIndex : pos + nlRel;
                    ReadOnlySpan<byte> lineBytes = fileData.Slice(pos, lineEnd - pos);
                    list.Add(DecodeTrimmedLine(lineBytes));
                    remaining--;

                    if (nlRel < 0)
                    {
                        pos = logicalEndIndex;
                        break;
                    }
                    pos = lineEnd + 1;
                }

                if (remaining == 0 || (isLastChunk && !nowWaiting))
                {
                    pending.RemoveAt(idx);
                }
                else
                {
                    pending[idx] = (result, remaining, nowWaiting);
                }
            }
        }

        private static void FulfillPendingContextAfterWithOpenLine(
            FileSearchContext context,
            string fullOpenLineContent,
            ReadOnlySpan<byte> fileData,
            int openLineEndInChunk,
            int logicalEndIndex,
            bool isLastChunk,
            bool foundNewline)
        {
            var pending = context.PendingContextAfter;
            if (pending == null || pending.Count == 0) return;

            for (int idx = pending.Count - 1; idx >= 0; idx--)
            {
                var (result, remaining, waitingForOpenLine) = pending[idx];
                if (!waitingForOpenLine) continue;

                var list = result.ContextAfter as List<string>;
                if (list == null)
                {
                    list = new List<string>(result.ContextAfter);
                    result.ContextAfter = list;
                }

                list.Add(fullOpenLineContent);
                remaining--;

                bool stillWaiting = false;
                if (remaining > 0 && foundNewline)
                {
                    int pos = openLineEndInChunk + 1;
                    while (remaining > 0 && pos < logicalEndIndex)
                    {
                        int nlRel = fileData.Slice(pos, logicalEndIndex - pos).IndexOf((byte)'\n');
                        if (nlRel < 0 && !isLastChunk)
                        {
                            stillWaiting = true;
                            break;
                        }

                        int lineEnd = nlRel < 0 ? logicalEndIndex : pos + nlRel;
                        list.Add(DecodeTrimmedLine(fileData.Slice(pos, lineEnd - pos)));
                        remaining--;

                        if (nlRel < 0)
                        {
                            break;
                        }
                        pos = lineEnd + 1;
                    }
                }

                if (remaining == 0 || (isLastChunk && !stillWaiting))
                {
                    pending.RemoveAt(idx);
                }
                else
                {
                    pending[idx] = (result, remaining, stillWaiting);
                }
            }
        }

        private static List<string> GetContextBeforeWithCarry(
            ReadOnlySpan<byte> fileData,
            int lineStartOffset,
            int minLookbackOffset,
            int contextCount,
            FileSearchContext context,
            long chunkStartOffset)
        {
            var contextLines = new List<string>();
            if (contextCount <= 0) return contextLines;

            if (lineStartOffset >= minLookbackOffset)
            {
                int currentOffset = lineStartOffset - 1;
                for (int i = 0; i < contextCount && currentOffset >= minLookbackOffset; i++)
                {
                    int prevNewline = fileData.Slice(minLookbackOffset, currentOffset - minLookbackOffset).LastIndexOf((byte)'\n');
                    int start = prevNewline < 0 ? minLookbackOffset : minLookbackOffset + prevNewline + 1;
                    int length = currentOffset - start;
                    contextLines.Add(length > 0 ? DecodeTrimmedLine(fileData.Slice(start, length)) : string.Empty);
                    currentOffset = prevNewline < 0 ? -1 : minLookbackOffset + prevNewline;
                }

                contextLines.Reverse();

                if (contextLines.Count < contextCount && context.TrailingContextLines is { Count: > 0 } trailing)
                {
                    int needed = Math.Min(contextCount - contextLines.Count, trailing.Count);
                    int startIdx = trailing.Count - needed;
                    var combined = new List<string>(needed + contextLines.Count);
                    for (int i = startIdx; i < trailing.Count; i++)
                    {
                        combined.Add(trailing[i]);
                    }
                    combined.AddRange(contextLines);
                    return combined;
                }

                return contextLines;
            }
            else
            {
                // Match straddled overlap boundary backwards into [0 .. minLookbackOffset)
                int linesSkipped = fileData.Slice(lineStartOffset, minLookbackOffset - lineStartOffset).Count((byte)'\n');
                int currentOffset = lineStartOffset - 1;
                for (int i = 0; i < contextCount && currentOffset >= 0; i++)
                {
                    int prevNewline = fileData.Slice(0, currentOffset).LastIndexOf((byte)'\n');
                    if (prevNewline < 0 && chunkStartOffset > 0)
                    {
                        // Do not emit a partial line fragment from mid-line chunkStartOffset > 0
                        break;
                    }
                    int start = prevNewline < 0 ? 0 : prevNewline + 1;
                    int length = currentOffset - start;
                    contextLines.Add(length > 0 ? DecodeTrimmedLine(fileData.Slice(start, length)) : string.Empty);
                    currentOffset = prevNewline;
                }

                contextLines.Reverse();

                if (contextLines.Count < contextCount && context.TrailingContextLines is { Count: > 0 } trailing)
                {
                    int availableEnd = trailing.Count - linesSkipped - contextLines.Count;
                    if (availableEnd > 0)
                    {
                        int needed = Math.Min(contextCount - contextLines.Count, availableEnd);
                        int startIdx = availableEnd - needed;
                        var combined = new List<string>(needed + contextLines.Count);
                        for (int i = startIdx; i < availableEnd; i++)
                        {
                            combined.Add(trailing[i]);
                        }
                        combined.AddRange(contextLines);
                        return combined;
                    }
                }

                return contextLines;
            }
        }

        private static List<string> GetContextAfter(
            ReadOnlySpan<byte> fileData,
            int lineEndOffset,
            int contextCount,
            bool isLastChunk,
            out bool hitOpenLine)
        {
            hitOpenLine = false;
            var contextLines = new List<string>();
            if (contextCount <= 0 || lineEndOffset >= fileData.Length) return contextLines;

            int currentOffset = lineEndOffset + 1;
            for (int i = 0; i < contextCount; i++)
            {
                if (currentOffset >= fileData.Length) break;
                int nextNewline = fileData.Slice(currentOffset).IndexOf((byte)'\n');
                if (nextNewline < 0 && !isLastChunk)
                {
                    hitOpenLine = true;
                    break;
                }
                int end = nextNewline < 0 ? fileData.Length : currentOffset + nextNewline;
                int length = end - currentOffset;
                contextLines.Add(length > 0 ? DecodeTrimmedLine(fileData.Slice(currentOffset, length)) : string.Empty);
                currentOffset = end + 1;
            }
            return contextLines;
        }

        private static List<string> GetContextBeforeCharsWithCarry(
            ReadOnlySpan<char> fileChars,
            int lineStartOffset,
            int minLookbackCharOffset,
            int contextCount,
            FileSearchContext context,
            long chunkStartOffset)
        {
            var contextLines = new List<string>();
            if (contextCount <= 0) return contextLines;

            if (lineStartOffset >= minLookbackCharOffset)
            {
                int currentOffset = lineStartOffset - 1;
                for (int i = 0; i < contextCount && currentOffset >= minLookbackCharOffset; i++)
                {
                    int prevNewline = fileChars.Slice(minLookbackCharOffset, currentOffset - minLookbackCharOffset).LastIndexOf('\n');
                    int start = prevNewline < 0 ? minLookbackCharOffset : minLookbackCharOffset + prevNewline + 1;
                    int length = currentOffset - start;
                    if (length > 0)
                    {
                        var lineSpan = fileChars.Slice(start, length);
                        if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                            lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                        contextLines.Add(new string(lineSpan));
                    }
                    else
                    {
                        contextLines.Add(string.Empty);
                    }
                    currentOffset = prevNewline < 0 ? -1 : minLookbackCharOffset + prevNewline;
                }

                contextLines.Reverse();

                if (contextLines.Count < contextCount && context.TrailingContextLines is { Count: > 0 } trailing)
                {
                    int needed = Math.Min(contextCount - contextLines.Count, trailing.Count);
                    int startIdx = trailing.Count - needed;
                    var combined = new List<string>(needed + contextLines.Count);
                    for (int i = startIdx; i < trailing.Count; i++)
                    {
                        combined.Add(trailing[i]);
                    }
                    combined.AddRange(contextLines);
                    return combined;
                }

                return contextLines;
            }
            else
            {
                int linesSkipped = fileChars.Slice(lineStartOffset, minLookbackCharOffset - lineStartOffset).Count('\n');
                int currentOffset = lineStartOffset - 1;
                for (int i = 0; i < contextCount && currentOffset >= 0; i++)
                {
                    int prevNewline = fileChars.Slice(0, currentOffset).LastIndexOf('\n');
                    if (prevNewline < 0 && chunkStartOffset > 0)
                    {
                        break;
                    }
                    int start = prevNewline < 0 ? 0 : prevNewline + 1;
                    int length = currentOffset - start;
                    if (length > 0)
                    {
                        var lineSpan = fileChars.Slice(start, length);
                        if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                            lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                        contextLines.Add(new string(lineSpan));
                    }
                    else
                    {
                        contextLines.Add(string.Empty);
                    }
                    currentOffset = prevNewline;
                }

                contextLines.Reverse();

                if (contextLines.Count < contextCount && context.TrailingContextLines is { Count: > 0 } trailing)
                {
                    int availableEnd = trailing.Count - linesSkipped - contextLines.Count;
                    if (availableEnd > 0)
                    {
                        int needed = Math.Min(contextCount - contextLines.Count, availableEnd);
                        int startIdx = availableEnd - needed;
                        var combined = new List<string>(needed + contextLines.Count);
                        for (int i = startIdx; i < availableEnd; i++)
                        {
                            combined.Add(trailing[i]);
                        }
                        combined.AddRange(contextLines);
                        return combined;
                    }
                }

                return contextLines;
            }
        }

        private static List<string> GetContextAfterChars(
            ReadOnlySpan<char> fileChars,
            int lineEndOffset,
            int contextCount,
            bool isLastChunk,
            out bool hitOpenLine)
        {
            hitOpenLine = false;
            var contextLines = new List<string>();
            if (contextCount <= 0 || lineEndOffset >= fileChars.Length) return contextLines;

            int currentOffset = lineEndOffset + 1;
            for (int i = 0; i < contextCount; i++)
            {
                if (currentOffset >= fileChars.Length) break;
                int nextNewline = fileChars.Slice(currentOffset).IndexOf('\n');
                if (nextNewline < 0 && !isLastChunk)
                {
                    hitOpenLine = true;
                    break;
                }
                int end = nextNewline < 0 ? fileChars.Length : currentOffset + nextNewline;
                int length = end - currentOffset;
                if (length > 0)
                {
                    var lineSpan = fileChars.Slice(currentOffset, length);
                    if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                        lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                    contextLines.Add(new string(lineSpan));
                }
                else
                {
                    contextLines.Add(string.Empty);
                }
                currentOffset = end + 1;
            }
            return contextLines;
        }

        private readonly struct SearchPlan
        {
            public RegexPatternClass PatternClass { get; }
            public byte[] PrimaryBytes { get; }
            public byte[] SecondaryBytes { get; }
            public AsciiIgnoreCaseMatcher? PrimaryMatcher { get; }
            public AsciiIgnoreCaseMatcher? SecondaryMatcher { get; }
            public Regex? Regex { get; }

            public SearchPlan(
                RegexPatternClass patternClass,
                byte[] primaryBytes,
                byte[] secondaryBytes,
                AsciiIgnoreCaseMatcher? primaryMatcher,
                AsciiIgnoreCaseMatcher? secondaryMatcher,
                Regex? regex)
            {
                PatternClass = patternClass;
                PrimaryBytes = primaryBytes;
                SecondaryBytes = secondaryBytes;
                PrimaryMatcher = primaryMatcher;
                SecondaryMatcher = secondaryMatcher;
                Regex = regex;
            }
        }

        /// <summary>
        /// Unified SIMD (Vector512/Vector256/Vector128) 2-anchor + rarest-byte SearchValues&lt;byte&gt;
        /// ASCII case-insensitive matcher for multi-byte and single-byte UTF-8 patterns.
        /// </summary>
        private sealed class AsciiIgnoreCaseMatcher
        {
            private readonly byte[] _pattern;
            private readonly SearchValues<byte> _rareAnchorValues;
            private readonly int _anchor0;
            private readonly int _anchor1;
            private readonly bool _anchor0IsLetter;
            private readonly bool _anchor1IsLetter;
            private readonly byte _anchor0Lower;
            private readonly byte _anchor1Lower;
            private readonly byte _anchor0Exact;
            private readonly byte _anchor1Exact;

            public AsciiIgnoreCaseMatcher(byte[] pattern)
            {
                _pattern = pattern;
                int m = pattern.Length;

                int best0 = 0;
                int bestRank0 = GetRarityRank(pattern[0]);
                for (int i = 1; i < m; i++)
                {
                    int rank = GetRarityRank(pattern[i]);
                    if (rank > bestRank0)
                    {
                        bestRank0 = rank;
                        best0 = i;
                    }
                }
                _anchor0 = best0;

                if (m >= 2)
                {
                    int best1 = best0 == 0 ? m - 1 : 0;
                    int bestRank1 = GetRarityRank(pattern[best1]);
                    for (int i = 0; i < m; i++)
                    {
                        if (i == best0) continue;
                        int rank = GetRarityRank(pattern[i]);
                        if (rank > bestRank1)
                        {
                            bestRank1 = rank;
                            best1 = i;
                        }
                    }
                    _anchor1 = best1;
                }
                else
                {
                    _anchor1 = 0;
                }

                byte b0 = pattern[_anchor0];
                _anchor0Exact = b0;
                _anchor0IsLetter = IsAsciiLetter(b0);
                _anchor0Lower = (byte)(b0 | 0x20);

                if (_anchor0IsLetter)
                {
                    _rareAnchorValues = SearchValues.Create((byte)(b0 & ~0x20), _anchor0Lower);
                }
                else
                {
                    _rareAnchorValues = SearchValues.Create(b0);
                }

                byte b1 = pattern[_anchor1];
                _anchor1Exact = b1;
                _anchor1IsLetter = IsAsciiLetter(b1);
                _anchor1Lower = (byte)(b1 | 0x20);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool IsAsciiLetter(byte b) => (uint)((b | 0x20) - 'a') <= (uint)('z' - 'a');

            private static int GetRarityRank(byte b)
            {
                if (b == ' ') return 0;
                if (b == '\t' || b == '\n' || b == '\r') return 1;
                int lower = b | 0x20;
                return lower switch
                {
                    'e' => 2,
                    't' => 3,
                    'a' => 4,
                    'o' => 5,
                    'i' => 6,
                    'n' => 7,
                    's' => 8,
                    'r' => 9,
                    'h' => 10,
                    'l' => 11,
                    'd' => 12,
                    'c' => 13,
                    'u' => 14,
                    'm' => 15,
                    'f' => 16,
                    'p' => 17,
                    'g' => 18,
                    'w' => 19,
                    'y' => 20,
                    'b' => 21,
                    'v' => 22,
                    'k' => 23,
                    'x' => 24,
                    'j' => 25,
                    'q' => 26,
                    'z' => 27,
                    _ => b >= 0x80 ? 35 : 30
                };
            }

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public int IndexOf(ReadOnlySpan<byte> haystack)
            {
                int m = _pattern.Length;
                if (m == 0) return 0;
                if (haystack.Length < m) return -1;
                if (m == 1) return haystack.IndexOfAny(_rareAnchorValues);

                int i = 0;
                int a0 = _anchor0;
                int a1 = _anchor1;
                ref byte hayRef = ref MemoryMarshal.GetReference(haystack);

                if (Vector512.IsHardwareAccelerated && haystack.Length >= Vector512<byte>.Count + m - 1)
                {
                    Vector512<byte> caseBit = Vector512.Create((byte)0x20);
                    Vector512<byte> target0 = Vector512.Create(_anchor0IsLetter ? _anchor0Lower : _anchor0Exact);
                    Vector512<byte> target1 = Vector512.Create(_anchor1IsLetter ? _anchor1Lower : _anchor1Exact);

                    int vecLimit = haystack.Length - (Vector512<byte>.Count + m - 1);
                    while (i <= vecLimit)
                    {
                        Vector512<byte> v0 = Vector512.LoadUnsafe(ref Unsafe.Add(ref hayRef, i + a0));
                        Vector512<byte> v1 = Vector512.LoadUnsafe(ref Unsafe.Add(ref hayRef, i + a1));
                        Vector512<byte> eq0 = _anchor0IsLetter ? Vector512.Equals(v0 | caseBit, target0) : Vector512.Equals(v0, target0);
                        Vector512<byte> eq1 = _anchor1IsLetter ? Vector512.Equals(v1 | caseBit, target1) : Vector512.Equals(v1, target1);

                        ulong mask = (eq0 & eq1).ExtractMostSignificantBits();
                        while (mask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(mask);
                            int candidate = i + bit;
                            if (EqualsIgnoreCaseAscii(haystack.Slice(candidate, m), _pattern))
                            {
                                return candidate;
                            }
                            mask &= mask - 1;
                        }

                        i += Vector512<byte>.Count;
                    }
                }

                if (Vector256.IsHardwareAccelerated && haystack.Length - i >= Vector256<byte>.Count + m - 1)
                {
                    Vector256<byte> caseBit = Vector256.Create((byte)0x20);
                    Vector256<byte> target0 = Vector256.Create(_anchor0IsLetter ? _anchor0Lower : _anchor0Exact);
                    Vector256<byte> target1 = Vector256.Create(_anchor1IsLetter ? _anchor1Lower : _anchor1Exact);

                    int vecLimit = haystack.Length - (Vector256<byte>.Count + m - 1);
                    while (i <= vecLimit)
                    {
                        Vector256<byte> v0 = Vector256.LoadUnsafe(ref Unsafe.Add(ref hayRef, i + a0));
                        Vector256<byte> v1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref hayRef, i + a1));
                        Vector256<byte> eq0 = _anchor0IsLetter ? Vector256.Equals(v0 | caseBit, target0) : Vector256.Equals(v0, target0);
                        Vector256<byte> eq1 = _anchor1IsLetter ? Vector256.Equals(v1 | caseBit, target1) : Vector256.Equals(v1, target1);

                        uint mask = (eq0 & eq1).ExtractMostSignificantBits();
                        while (mask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(mask);
                            int candidate = i + bit;
                            if (EqualsIgnoreCaseAscii(haystack.Slice(candidate, m), _pattern))
                            {
                                return candidate;
                            }
                            mask &= mask - 1;
                        }

                        i += Vector256<byte>.Count;
                    }
                }

                if (Vector128.IsHardwareAccelerated && haystack.Length - i >= Vector128<byte>.Count + m - 1)
                {
                    Vector128<byte> caseBit = Vector128.Create((byte)0x20);
                    Vector128<byte> target0 = Vector128.Create(_anchor0IsLetter ? _anchor0Lower : _anchor0Exact);
                    Vector128<byte> target1 = Vector128.Create(_anchor1IsLetter ? _anchor1Lower : _anchor1Exact);

                    int vecLimit = haystack.Length - (Vector128<byte>.Count + m - 1);
                    while (i <= vecLimit)
                    {
                        Vector128<byte> v0 = Vector128.LoadUnsafe(ref Unsafe.Add(ref hayRef, i + a0));
                        Vector128<byte> v1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref hayRef, i + a1));
                        Vector128<byte> eq0 = _anchor0IsLetter ? Vector128.Equals(v0 | caseBit, target0) : Vector128.Equals(v0, target0);
                        Vector128<byte> eq1 = _anchor1IsLetter ? Vector128.Equals(v1 | caseBit, target1) : Vector128.Equals(v1, target1);

                        uint mask = (eq0 & eq1).ExtractMostSignificantBits();
                        while (mask != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount(mask);
                            int candidate = i + bit;
                            if (EqualsIgnoreCaseAscii(haystack.Slice(candidate, m), _pattern))
                            {
                                return candidate;
                            }
                            mask &= mask - 1;
                        }

                        i += Vector128<byte>.Count;
                    }
                }

                int pos = i + a0;
                int tailLimit = haystack.Length - (m - a0);
                while (pos <= tailLimit)
                {
                    int searchLen = tailLimit - pos + 1;
                    int rel = haystack.Slice(pos, searchLen).IndexOfAny(_rareAnchorValues);
                    if (rel < 0) break;

                    pos += rel;
                    int start = pos - a0;
                    if (EqualsIgnoreCaseAscii(haystack.Slice(start, m), _pattern))
                    {
                        return start;
                    }
                    pos++;
                }

                return -1;
            }

            public int LastIndexOf(ReadOnlySpan<byte> haystack)
            {
                int m = _pattern.Length;
                if (m == 0) return haystack.Length;
                if (haystack.Length < m) return -1;
                if (m == 1) return haystack.LastIndexOfAny(_rareAnchorValues);

                int lastMatch = -1;
                int pos = 0;
                while (pos <= haystack.Length - m)
                {
                    int rel = IndexOf(haystack.Slice(pos));
                    if (rel < 0) break;
                    lastMatch = pos + rel;
                    pos = lastMatch + 1;
                }
                return lastMatch;
            }
        }
    }
}
