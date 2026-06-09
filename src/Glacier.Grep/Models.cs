using System;
using System.Collections.Generic;

namespace Glacier.Grep
{
    /// <summary>
    /// Represents a zero-allocation reference to a match within a file slice.
    /// </summary>
    public readonly ref struct MatchRef
    {
        public ReadOnlySpan<byte> Line { get; }
        public int MatchStartIndex { get; }
        public int MatchLength { get; }
        public int LineNumber { get; }

        public MatchRef(ReadOnlySpan<byte> line, int matchStartIndex, int matchLength, int lineNumber)
        {
            Line = line;
            MatchStartIndex = matchStartIndex;
            MatchLength = matchLength;
            LineNumber = lineNumber;
        }
    }

    /// <summary>
    /// Represents a file path and length targeted for searching.
    /// </summary>
    public readonly struct FileSearchTask
    {
        public string FullPath { get; }
        public string RelativePath { get; }
        public long Length { get; }

        public FileSearchTask(string fullPath, string relativePath, long length)
        {
            FullPath = fullPath;
            RelativePath = relativePath;
            Length = length;
        }
    }

    /// <summary>
    /// Represents a search result containing path, line number, content, and surrounding context.
    /// Suitable for JSON serialization.
    /// </summary>
    public class SearchResult
    {
        public string FilePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string MatchContent { get; set; } = string.Empty;
        public int MatchStartIndex { get; set; }
        public int MatchLength { get; set; }
        public List<string> ContextBefore { get; set; } = new();
        public List<string> ContextAfter { get; set; } = new();
    }
}
