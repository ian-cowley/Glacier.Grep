using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Glacier.Grep
{
    /// <summary>
    /// Delegate for zero-allocation processing of raw file data spans.
    /// </summary>
    public delegate void FileDataProcessor(ReadOnlySpan<byte> data);

    /// <summary>
    /// Delegate for processing a memory chunk of file data with physical and logical offset boundaries.
    /// </summary>
    public delegate void FileChunkProcessor(
        ReadOnlySpan<byte> chunkData,
        long chunkStartOffset,
        long logicalStartOffset,
        long logicalEndOffset,
        long totalLength,
        bool isLastChunk);

    /// <summary>
    /// Internal delegate for chunk processing that also supplies the byte and decoded Unicode scalar immediately preceding <paramref name="chunkStartOffset"/>.
    /// </summary>
    internal delegate void FileChunkProcessorWithPrev(
        ReadOnlySpan<byte> chunkData,
        long chunkStartOffset,
        long logicalStartOffset,
        long logicalEndOffset,
        long totalLength,
        bool isLastChunk,
        byte precedingByte,
        Rune precedingRune);

    /// <summary>
    /// Hybrid I/O Dispatcher that routes small files (&lt;1 MB) through pooled <see cref="RandomAccess"/> buffers
    /// and medium/large files (&gt;=1 MB or &gt;<see cref="MaxChunkSize"/>) through per-chunk sliding OS file-backed
    /// <see cref="MemoryMappedFile"/> views with UTF-8 codepoint-safe chunk boundaries.
    /// </summary>
    public static class HybridIoDispatcher
    {
        public const long OneMegaByte = 1024 * 1024;
        public const long EightMegaBytes = 8 * 1024 * 1024;
        private const int LookaheadBytes = 65536;
        private const long ViewAlignmentMask = ~0xFFFFL; // 64 KB OS allocation granularity

        /// <summary>
        /// Maximum chunk size for memory-mapped file processing. Defaults to 16 MB to bound per-worker decode buffers.
        /// Configurable for testing boundary conditions with small chunks.
        /// </summary>
        public static int MaxChunkSize { get; set; } = 16 * 1024 * 1024;

        public static void ClearPool()
        {
            // Retained for public API compatibility; file-backed MemoryMappedFile views release deterministically per chunk.
        }

        public static void ProcessFile(string path, long length, FileDataProcessor processor)
        {
            ProcessFile(path, length, 0, (chunk, _, _, _, _, _) => processor(chunk));
        }

        public static void ProcessFile(string path, long length, int overlapMargin, FileChunkProcessor processor)
        {
            ProcessFileWithPrecedingByte(
                path,
                length,
                overlapMargin,
                (chunkData, chunkStartOffset, logicalStartOffset, logicalEndOffset, totalLength, isLastChunk, _, _) =>
                    processor(chunkData, chunkStartOffset, logicalStartOffset, logicalEndOffset, totalLength, isLastChunk));
        }

        internal static void ProcessFileWithPrecedingByte(string path, long length, int overlapMargin, FileChunkProcessorWithPrev processor)
        {
            _ = length; // Directory entry lengths may be stale (growing/shrinking files) or 0 on symlinks; always inspect handle length.
            int maxChunk = Math.Clamp(MaxChunkSize, 64, 512 * 1024 * 1024);

            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.SequentialScan);

            long actualLength = RandomAccess.GetLength(handle);
            if (actualLength <= 0)
            {
                processor(ReadOnlySpan<byte>.Empty, 0, 0, 0, 0, true, 0, default);
                return;
            }

            if (actualLength < OneMegaByte && actualLength <= maxChunk)
            {
                ProcessRentedArray(handle, (int)actualLength, overlapMargin, maxChunk, processor);
            }
            else
            {
                ProcessMemoryMapped(handle, actualLength, overlapMargin, maxChunk, processor);
            }
        }

        private static void ProcessRentedArray(
            SafeFileHandle handle,
            int initialLength,
            int overlapMargin,
            int maxChunk,
            FileChunkProcessorWithPrev processor)
        {
            int targetCap = Math.Max(1, initialLength);
            byte[] rented = ArrayPool<byte>.Shared.Rent(targetCap);
            int totalRead = 0;
            bool promoteToMmf = false;
            Span<byte> probe = stackalloc byte[1];

            try
            {
                while (true)
                {
                    while (totalRead < targetCap)
                    {
                        int bytesRead = RandomAccess.Read(handle, rented.AsSpan(totalRead, targetCap - totalRead), totalRead);
                        if (bytesRead == 0)
                        {
                            ReadOnlySpan<byte> shrankOrExact = rented.AsSpan(0, totalRead);
                            processor(shrankOrExact, 0, 0, totalRead, totalRead, true, 0, default);
                            return;
                        }
                        totalRead += bytesRead;
                    }

                    // Buffer is full; probe 1 byte to verify EOF in case the file grew during reading.
                    int probeRead = RandomAccess.Read(handle, probe, totalRead);
                    if (probeRead == 0)
                    {
                        ReadOnlySpan<byte> exact = rented.AsSpan(0, totalRead);
                        processor(exact, 0, 0, totalRead, totalRead, true, 0, default);
                        return;
                    }

                    long currentLength = Math.Max(RandomAccess.GetLength(handle), totalRead + 1L);
                    if (currentLength >= OneMegaByte || currentLength > maxChunk)
                    {
                        promoteToMmf = true;
                        break;
                    }

                    int maxRentedLimit = (int)Math.Min(OneMegaByte - 1, maxChunk);
                    int newCap = (int)Math.Min(maxRentedLimit, Math.Max(currentLength, (long)targetCap * 2));
                    if (newCap <= targetCap)
                    {
                        promoteToMmf = true;
                        break;
                    }

                    if (newCap > rented.Length)
                    {
                        byte[] newRented = ArrayPool<byte>.Shared.Rent(newCap);
                        rented.AsSpan(0, totalRead).CopyTo(newRented);
                        ArrayPool<byte>.Shared.Return(rented);
                        rented = newRented;
                    }

                    rented[totalRead] = probe[0];
                    totalRead += 1;
                    targetCap = newCap;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }

            if (promoteToMmf)
            {
                long latestLength = RandomAccess.GetLength(handle);
                ProcessMemoryMapped(handle, latestLength, overlapMargin, maxChunk, processor);
            }
        }

        private static unsafe void ProcessMemoryMapped(
            SafeFileHandle handle,
            long actualLength,
            int overlapMargin,
            int maxChunk,
            FileChunkProcessorWithPrev processor)
        {
            if (actualLength <= 0)
            {
                processor(ReadOnlySpan<byte>.Empty, 0, 0, 0, 0, true, 0, default);
                return;
            }

            using var mmf = MemoryMappedFile.CreateFromFile(
                handle,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);

            if (actualLength <= maxChunk)
            {
                using var accessor = mmf.CreateViewAccessor(0, actualLength, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    byte* start = pointer + accessor.PointerOffset;
                    var fileData = new ReadOnlySpan<byte>(start, (int)actualLength);
                    processor(fileData, 0, 0, actualLength, actualLength, true, 0, default);
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                return;
            }

            int overlap = Math.Max(0, overlapMargin);
            if (overlap > maxChunk / 2)
            {
                overlap = maxChunk / 2;
            }

            long logicalOffset = 0;
            while (logicalOffset < actualLength)
            {
                long requestedViewStart = (logicalOffset == 0) ? 0 : Math.Max(0, logicalOffset - overlap);
                long alignedViewStart = requestedViewStart >= 4
                    ? (requestedViewStart - 4) & ViewAlignmentMask
                    : 0;
                long nominalLogicalEnd = Math.Min(logicalOffset + maxChunk, actualLength);
                long maxViewEnd = Math.Min(actualLength, nominalLogicalEnd + LookaheadBytes);
                long viewSize = maxViewEnd - alignedViewStart;

                using var accessor = mmf.CreateViewAccessor(alignedViewStart, viewSize, MemoryMappedFileAccess.Read);
                byte* pointer = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    byte* viewBase = pointer + accessor.PointerOffset;
                    long logicalEnd = nominalLogicalEnd;

                    // Align logicalEnd to the next newline boundary (within 64 KB) so complete lines stay in one chunk.
                    // Otherwise backtrack past any split multi-byte UTF-8 continuation bytes.
                    if (logicalEnd < actualLength)
                    {
                        long scanLimit = Math.Min(actualLength, logicalEnd + LookaheadBytes);
                        long scan = logicalEnd;
                        bool foundNewline = false;
                        while (scan < scanLimit)
                        {
                            if (viewBase[scan - alignedViewStart] == (byte)'\n')
                            {
                                logicalEnd = scan + 1;
                                foundNewline = true;
                                break;
                            }
                            scan++;
                        }

                        if (!foundNewline)
                        {
                            int backtrack = 0;
                            while (backtrack < 3 &&
                                   logicalEnd - backtrack > logicalOffset + 1 &&
                                   IsUtf8ContinuationByte(viewBase[logicalEnd - backtrack - alignedViewStart]))
                            {
                                backtrack++;
                            }
                            logicalEnd -= backtrack;
                        }
                    }

                    long chunkStart = requestedViewStart;
                    // Advance chunkStart past any leading UTF-8 continuation bytes in the overlap prefix.
                    if (chunkStart > 0)
                    {
                        while (chunkStart < logicalOffset && IsUtf8ContinuationByte(viewBase[chunkStart - alignedViewStart]))
                        {
                            chunkStart++;
                        }
                    }

                    byte prevByte = 0;
                    Rune prevRune = default;
                    if (chunkStart > 0)
                    {
                        long prevOffsetInView = chunkStart - alignedViewStart;
                        prevByte = viewBase[prevOffsetInView - 1];
                        if (prevByte < 0x80)
                        {
                            prevRune = new Rune(prevByte);
                        }
                        else
                        {
                            int avail = (int)Math.Min(4L, prevOffsetInView);
                            var tail = new ReadOnlySpan<byte>(viewBase + (prevOffsetInView - avail), avail);
                            Rune.DecodeLastFromUtf8(tail, out prevRune, out _);
                        }
                    }

                    int chunkSize = checked((int)(logicalEnd - chunkStart));
                    bool isLast = (logicalEnd >= actualLength);
                    var fileData = new ReadOnlySpan<byte>(viewBase + (chunkStart - alignedViewStart), chunkSize);

                    processor(fileData, chunkStart, logicalOffset, logicalEnd, actualLength, isLast, prevByte, prevRune);

                    logicalOffset = logicalEnd;
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }

        private static bool IsUtf8ContinuationByte(byte b) => (b & 0xC0) == 0x80;
    }
}
