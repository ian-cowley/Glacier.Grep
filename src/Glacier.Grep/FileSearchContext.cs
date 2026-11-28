using System.Buffers;
using System.Collections.Generic;

namespace Glacier.Grep;

/// <summary>
/// Maintains search state across memory-mapped chunk boundaries, ensuring monotonic
/// global line numbering and boundary match deduplication across chunk boundaries.
/// </summary>
public sealed class FileSearchContext
{
    /// <summary>
    /// The current 1-based global line number across chunks.
    /// </summary>
    public long GlobalLineNumber { get; set; } = 1;

    /// <summary>
    /// The last file byte offset that was processed.
    /// </summary>
    public long LastProcessedFileOffset { get; set; } = 0;

    /// <summary>
    /// Cached relative file path computed lazily once per matching file.
    /// </summary>
    internal string? RelativePath { get; set; }

    /// <summary>
    /// Tracks whether chunk 0 detected a binary file so subsequent chunks skip immediately.
    /// </summary>
    internal bool IsBinary { get; set; }

    /// <summary>
    /// Rolling buffer of up to <c>contextLines</c> complete lines preceding the current chunk or open line.
    /// </summary>
    internal List<string>? TrailingContextLines { get; set; }

    /// <summary>
    /// True while <see cref="SearchEngine"/> is accumulating a single line spanning chunk boundaries in forward modes.
    /// </summary>
    internal bool IsOpenLine { get; set; }

    /// <summary>
    /// File byte offset where the current open line started.
    /// </summary>
    internal long OpenLineFileStartOffset { get; set; }

    /// <summary>
    /// 1-based global line number of the current open line.
    /// </summary>
    internal long OpenLineNumber { get; set; }

    /// <summary>
    /// Cumulative UTF-16 char count of <see cref="OpenLineBuffer"/> written so far.
    /// </summary>
    internal int OpenLineCharCount { get; set; }

    /// <summary>
    /// Lazily allocated byte buffer for an open line exceeding a chunk boundary in forward modes.
    /// </summary>
    internal ArrayBufferWriter<byte>? OpenLineBuffer { get; set; }

    /// <summary>
    /// Captured before-context lines for an open line in forward modes.
    /// </summary>
    internal List<string>? OpenLineContextBefore { get; set; }

    /// <summary>
    /// Matches emitted on an open line awaiting line completion to populate full <see cref="SearchResult.MatchContent"/> and <see cref="SearchResult.MatchStartIndex"/>.
    /// </summary>
    internal List<(SearchResult Result, int LineCharOffset, bool NeedsContextAfter)>? PendingOpenLineMatches { get; set; }

    /// <summary>
    /// True while <see cref="SearchEngine"/> is accumulating a single line that spans chunk boundaries in inverted mode.
    /// </summary>
    internal bool IsAccumulatingPartialLine { get; set; }

    /// <summary>
    /// True if the currently accumulated partial line has already matched a literal query in an earlier chunk.
    /// </summary>
    internal bool PartialLineMatched { get; set; }

    /// <summary>
    /// Lazily allocated byte buffer for lines exceeding a chunk boundary in inverted search mode.
    /// </summary>
    internal ArrayBufferWriter<byte>? PartialLineBuffer { get; set; }

    /// <summary>
    /// Captured before-context lines for an open partial line in inverted search mode.
    /// </summary>
    internal List<string>? PendingContextBefore { get; set; }

    /// <summary>
    /// Matches emitted near the end of a prior chunk that still need trailing context lines from subsequent chunks.
    /// </summary>
    internal List<(SearchResult Result, int Remaining, bool WaitingForOpenLine)>? PendingContextAfter { get; set; }
}
