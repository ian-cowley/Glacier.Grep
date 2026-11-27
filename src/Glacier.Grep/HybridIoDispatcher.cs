using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
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
    /// Hybrid I/O Dispatcher that routes small files (&lt;1 MB) through pooled <see cref="RandomAccess"/> buffers
    /// and medium/large files (&gt;=1 MB or &gt;<see cref="MaxChunkSize"/>) through genuine zero-copy OS file-backed
    /// <see cref="MemoryMappedFile"/> views with UTF-8 codepoint-safe chunk boundaries.
    /// </summary>
    public static class HybridIoDispatcher
    {
        public const long OneMegaByte = 1024 * 1024;
        public const long EightMegaBytes = 8 * 1024 * 1024;

        /// <summary>
        /// Maximum chunk size for memory-mapped file processing. Defaults to 16 MB to bound per-worker decode buffers.
        /// Configurable for testing boundary conditions with small chunks.
        /// </summary>
        public static int MaxChunkSize { get; set; } = 16 * 1024 * 1024;

        public static void ClearPool()
        {
            // Retained for public API compatibility; file-backed MemoryMappedFile views release deterministically per call.
        }

        public static void ProcessFile(string path, long length, FileDataProcessor processor)
        {
            ProcessFile(path, length, 0, (chunk, _, _, _, _, _) => processor(chunk));
        }

        public static void ProcessFile(string path, long length, int overlapMargin, FileChunkProcessor processor)
        {
            if (length <= 0)
            {
                processor(ReadOnlySpan<byte>.Empty, 0, 0, 0, 0, true);
                return;
            }

            int maxChunk = Math.Max(64, MaxChunkSize);
            if (length < OneMegaByte && length <= maxChunk)
            {
                ProcessRentedArray(path, (int)length, span => processor(span, 0, 0, span.Length, span.Length, true));
            }
            else
            {
                ProcessMemoryMapped(path, length, overlapMargin, maxChunk, processor);
            }
        }

        private static void ProcessRentedArray(string path, int length, FileDataProcessor processor)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                using SafeFileHandle handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    FileOptions.SequentialScan);

                int bytesRead = RandomAccess.Read(handle, rented.AsSpan(0, length), 0);
                processor(rented.AsSpan(0, bytesRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static unsafe void ProcessMemoryMapped(
            string path,
            long length,
            int overlapMargin,
            int maxChunk,
            FileChunkProcessor processor)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.SequentialScan);
            long actualLength = fs.Length;
            if (actualLength <= 0)
            {
                processor(ReadOnlySpan<byte>.Empty, 0, 0, 0, 0, true);
                return;
            }

            using var mmf = MemoryMappedFile.CreateFromFile(
                fs,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);

            using var accessor = mmf.CreateViewAccessor(0, actualLength, MemoryMappedFileAccess.Read);

            byte* pointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            try
            {
                byte* start = pointer + accessor.PointerOffset;
                long capacity = actualLength;

                if (capacity <= maxChunk)
                {
                    var fileData = new ReadOnlySpan<byte>(start, (int)capacity);
                    processor(fileData, 0, 0, capacity, capacity, true);
                }
                else
                {
                    int overlap = Math.Max(0, overlapMargin);
                    if (overlap > maxChunk / 2)
                    {
                        overlap = maxChunk / 2;
                    }

                    long logicalOffset = 0;
                    while (logicalOffset < capacity)
                    {
                        long logicalEnd = Math.Min(logicalOffset + maxChunk, capacity);

                        // Align logicalEnd to the next newline boundary (within 64 KB) so complete lines stay in one chunk.
                        // Otherwise backtrack past any split multi-byte UTF-8 continuation bytes.
                        if (logicalEnd < capacity)
                        {
                            long scanLimit = Math.Min(capacity, logicalEnd + 65536);
                            long scan = logicalEnd;
                            bool foundNewline = false;
                            while (scan < scanLimit)
                            {
                                if (start[scan] == (byte)'\n')
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
                                while (backtrack < 3 && logicalEnd - backtrack > logicalOffset + 1 && IsUtf8ContinuationByte(start[logicalEnd - backtrack]))
                                {
                                    backtrack++;
                                }
                                logicalEnd -= backtrack;
                            }
                        }

                        long chunkStart = (logicalOffset == 0) ? 0 : Math.Max(0, logicalOffset - overlap);
                        // Advance chunkStart past any leading UTF-8 continuation bytes in the overlap prefix
                        if (chunkStart > 0)
                        {
                            while (chunkStart < logicalOffset && IsUtf8ContinuationByte(start[chunkStart]))
                            {
                                chunkStart++;
                            }
                        }

                        int chunkSize = (int)(logicalEnd - chunkStart);
                        bool isLast = (logicalEnd >= capacity);
                        var fileData = new ReadOnlySpan<byte>(start + chunkStart, chunkSize);

                        processor(fileData, chunkStart, logicalOffset, logicalEnd, capacity, isLast);

                        logicalOffset = logicalEnd;
                    }
                }
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        private static bool IsUtf8ContinuationByte(byte b) => (b & 0xC0) == 0x80;
    }
}
