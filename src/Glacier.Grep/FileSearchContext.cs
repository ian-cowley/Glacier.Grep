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
}
