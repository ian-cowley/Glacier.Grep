using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
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
    /// Pools MemoryMappedFile handles on medium files (1-8 MB) to eliminate OS section and view churn.
    /// </summary>
    public static class HybridIoDispatcher
    {
        public const long OneMegaByte = 1024 * 1024;
        public const long EightMegaBytes = 8 * 1024 * 1024;

        private static readonly ConcurrentQueue<PooledMmfHandle> s_mediumMmfPool = new();
        private static int s_pooledCount = 0;
        private static readonly int s_maxPooledHandles = Math.Max(4, Environment.ProcessorCount * 2);

        /// <summary>
        /// Represents a pooled 8MB anonymous MemoryMappedFile handle and view accessor,
        /// eliminating OS file mapping handle churn (CreateFileMapping / MapViewOfFile / UnmapViewOfFile)
        /// for medium files (1-8 MB).
        /// </summary>
        private sealed unsafe class PooledMmfHandle : IDisposable
        {
            public readonly MemoryMappedFile Mmf;
            public readonly MemoryMappedViewAccessor Accessor;
            public readonly byte* Pointer;

            public PooledMmfHandle(long capacity = EightMegaBytes)
            {
                Mmf = MemoryMappedFile.CreateNew(null, capacity, MemoryMappedFileAccess.ReadWrite);
                Accessor = Mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
                byte* ptr = null;
                Accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                Pointer = ptr + Accessor.PointerOffset;
            }

            public void Dispose()
            {
                try { Accessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                try { Accessor.Dispose(); } catch { }
                try { Mmf.Dispose(); } catch { }
            }
        }

        private static PooledMmfHandle RentMediumHandle()
        {
            if (s_mediumMmfPool.TryDequeue(out var handle))
            {
                Interlocked.Decrement(ref s_pooledCount);
                return handle;
            }
            return new PooledMmfHandle(EightMegaBytes);
        }

        private static void ReturnMediumHandle(PooledMmfHandle handle)
        {
            if (Interlocked.Increment(ref s_pooledCount) <= s_maxPooledHandles)
            {
                s_mediumMmfPool.Enqueue(handle);
            }
            else
            {
                Interlocked.Decrement(ref s_pooledCount);
                handle.Dispose();
            }
        }

        public static void ClearPool()
        {
            while (s_mediumMmfPool.TryDequeue(out var handle))
            {
                handle.Dispose();
            }
            s_pooledCount = 0;
        }

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
            else if (length <= EightMegaBytes)
            {
                ProcessMediumFilePooled(path, (int)length, processor);
            }
            else
            {
                ProcessMemoryMapped(path, length, processor);
            }
        }

        private static unsafe void ProcessMediumFilePooled(string path, int length, FileDataProcessor processor)
        {
            var pooledHandle = RentMediumHandle();
            try
            {
                using SafeFileHandle fileHandle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    FileOptions.SequentialScan);

                int totalRead = 0;
                while (totalRead < length)
                {
                    int read = RandomAccess.Read(fileHandle, new Span<byte>(pooledHandle.Pointer + totalRead, length - totalRead), totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }

                processor(new ReadOnlySpan<byte>(pooledHandle.Pointer, totalRead));
            }
            finally
            {
                ReturnMediumHandle(pooledHandle);
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
