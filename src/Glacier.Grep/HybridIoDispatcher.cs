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
    /// Hybrid I/O Dispatcher that routes I/O patterns based on file size
    /// to bypass stream buffer overhead and utilize zero-copy memory mapping or pooled array blocks.
    /// </summary>
    public static class HybridIoDispatcher
    {
        private const long OneMegaByte = 1024 * 1024;

        public static void ProcessFile(string path, long length, FileDataProcessor processor)
        {
            if (length <= 0)
            {
                processor(ReadOnlySpan<byte>.Empty);
                return;
            }

            if (length < OneMegaByte)
            {
                ProcessRentedArray(path, (int)length, processor);
            }
            else
            {
                ProcessMemoryMapped(path, length, processor);
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
                    FileShare.Read, 
                    FileOptions.SequentialScan
                );

                int bytesRead = RandomAccess.Read(handle, rented.AsSpan(0, length), 0);
                processor(rented.AsSpan(0, bytesRead));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static void ProcessMemoryMapped(string path, long length, FileDataProcessor processor)
        {
            using var mmf = MemoryMappedFile.CreateFromFile(
                path, 
                FileMode.Open, 
                null, 
                0, 
                MemoryMappedFileAccess.Read
            );
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            
            unsafe
            {
                byte* pointer = null;
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                try
                {
                    byte* start = pointer + accessor.PointerOffset;
                    long capacity = accessor.Capacity;

                    // ReadOnlySpan length is limited to int.MaxValue (2GB).
                    // If the capacity exceeds this, we process the file in 1GB blocks.
                    if (capacity <= int.MaxValue)
                    {
                        var fileData = new ReadOnlySpan<byte>(start, (int)capacity);
                        processor(fileData);
                    }
                    else
                    {
                        const int maxSpanChunk = 1024 * 1024 * 1024; // 1 GB chunks
                        long remaining = capacity;
                        long offset = 0;
                        while (remaining > 0)
                        {
                            int currentChunkSize = (int)Math.Min(remaining, maxSpanChunk);
                            var fileData = new ReadOnlySpan<byte>(start + offset, currentChunkSize);
                            processor(fileData);
                            offset += currentChunkSize;
                            remaining -= currentChunkSize;
                        }
                    }
                }
                finally
                {
                    accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
    }
}
