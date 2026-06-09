using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace Glacier.Grep
{
    /// <summary>
    /// Custom high-performance directory traverser utilizing FileSystemEnumerable
    /// to achieve stack-allocated, zero-allocation directory pruning.
    /// Supports .gitignore, .ignore, and .rgignore files, recursion depth, and hidden file inclusion.
    /// </summary>
    public class DirectoryTraverser
    {
        private readonly string _rootDir;
        private readonly bool _searchHidden;
        private readonly int? _maxDepth;
        private readonly Dictionary<string, List<GitIgnoreFile>> _ignoreFileCache = new(StringComparer.OrdinalIgnoreCase);
        private string? _cachedDir;
        private List<GitIgnoreFile>? _cachedIgnoreFiles;

        public DirectoryTraverser(string rootDir, bool searchHidden = false, int? maxDepth = null)
        {
            _rootDir = Path.GetFullPath(rootDir).Replace('\\', '/');
            _searchHidden = searchHidden;
            _maxDepth = maxDepth;
            LoadDirectoryIgnoreFiles(_rootDir);
        }

        private void LoadDirectoryIgnoreFiles(string dirPath)
        {
            var list = new List<GitIgnoreFile>();
            string[] ignoreNames = { ".gitignore", ".ignore", ".rgignore" };
            foreach (var name in ignoreNames)
            {
                string path = Path.Combine(dirPath, name);
                if (File.Exists(path))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(path);
                        list.Add(new GitIgnoreFile(dirPath, lines));
                    }
                    catch { /* Ignore read errors */ }
                }
            }
            if (list.Count > 0)
            {
                _ignoreFileCache[dirPath] = list;
            }
        }

        /// <summary>
        /// Enumerates the directory searching for files that match gitignore criteria.
        /// </summary>
        public IEnumerable<FileSearchTask> EnumerateFiles()
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = _searchHidden ? FileAttributes.System : (FileAttributes.System | FileAttributes.Hidden),
                MatchType = MatchType.Simple,
                ReturnSpecialDirectories = false
            };

            var enumerable = new FileSystemEnumerable<FileSearchTask>(
                _rootDir,
                (ref FileSystemEntry entry) => new FileSearchTask(
                    entry.ToFullPath(),
                    entry.Length
                ),
                options
            );

            enumerable.ShouldIncludePredicate = ShouldIncludeEntry;
            enumerable.ShouldRecursePredicate = ShouldRecurseEntry;

            return enumerable;
        }

        private bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory)
                return false;

            ReadOnlySpan<char> fileName = entry.FileName;

            // Fast stack-allocated filters for common noisy folders/files
            if (!_searchHidden && fileName.StartsWith('.'))
            {
                return false;
            }

            if (fileName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("release", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Normalize path using stack-allocated span
            ReadOnlySpan<char> dir = entry.Directory;
            int totalLen = dir.Length + 1 + fileName.Length;
            char[]? rented = null;
            Span<char> pathBuffer = totalLen <= 1024 ? stackalloc char[1024] : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(totalLen));
            Span<char> normalizedPath = pathBuffer.Slice(0, totalLen);

            dir.CopyTo(normalizedPath);
            normalizedPath[dir.Length] = '/';
            fileName.CopyTo(normalizedPath.Slice(dir.Length + 1));

            for (int i = 0; i < dir.Length; i++)
            {
                if (normalizedPath[i] == '\\')
                {
                    normalizedPath[i] = '/';
                }
            }

            bool ignored = IsIgnored(normalizedPath, isDirectory: false);

            if (rented != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }

            return !ignored;
        }

        private bool ShouldRecurseEntry(ref FileSystemEntry entry)
        {
            ReadOnlySpan<char> dirName = entry.FileName;

            if (!_searchHidden && dirName.StartsWith('.'))
            {
                return false;
            }
            else
            {
                // Always skip control and cache folders to prevent scanning huge databases
                if (dirName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
                    dirName.Equals(".vscode", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Always skip build output/noisy folders
            if (dirName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("release", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Normalize path using stack-allocated span
            ReadOnlySpan<char> dir = entry.Directory;
            int totalLen = dir.Length + 1 + dirName.Length;
            char[]? rented = null;
            Span<char> pathBuffer = totalLen <= 1024 ? stackalloc char[1024] : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(totalLen));
            Span<char> normalizedPath = pathBuffer.Slice(0, totalLen);

            dir.CopyTo(normalizedPath);
            normalizedPath[dir.Length] = '/';
            dirName.CopyTo(normalizedPath.Slice(dir.Length + 1));

            for (int i = 0; i < dir.Length; i++)
            {
                if (normalizedPath[i] == '\\')
                {
                    normalizedPath[i] = '/';
                }
            }

            // Evaluate recursion depth
            if (_maxDepth.HasValue)
            {
                int relStart = _rootDir.Length;
                if (relStart < normalizedPath.Length && normalizedPath[relStart] == '/')
                    relStart++;
                
                ReadOnlySpan<char> relPath = normalizedPath.Slice(relStart);
                int depth = 0;
                if (!relPath.IsEmpty)
                {
                    depth = 1;
                    for (int i = 0; i < relPath.Length; i++)
                    {
                        if (relPath[i] == '/')
                            depth++;
                    }
                }

                if (depth > _maxDepth.Value)
                {
                    if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                    return false;
                }
            }

            // Evaluate ignore rules for this directory
            bool ignored = IsIgnored(normalizedPath, isDirectory: true);
            if (ignored)
            {
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                return false;
            }

            // Dynamically load nested ignore files in this directory
            string fullPath = normalizedPath.ToString();
            LoadDirectoryIgnoreFiles(fullPath);

            if (rented != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }

            return true;
        }

        private static ReadOnlySpan<char> GetDirectoryPart(ReadOnlySpan<char> path)
        {
            int lastSlash = path.LastIndexOf('/');
            return lastSlash < 0 ? ReadOnlySpan<char>.Empty : path.Slice(0, lastSlash);
        }

        private List<GitIgnoreFile> GetIgnoreFilesForDirectory(ReadOnlySpan<char> dirSpan)
        {
            if (_cachedDir != null && dirSpan.Equals(_cachedDir, StringComparison.OrdinalIgnoreCase))
            {
                return _cachedIgnoreFiles!;
            }

            var list = new List<GitIgnoreFile>();
            var lookup = _ignoreFileCache.GetAlternateLookup<ReadOnlySpan<char>>();
            ReadOnlySpan<char> currentDir = dirSpan;

            while (!currentDir.IsEmpty)
            {
                if (lookup.TryGetValue(currentDir, out var dirIgnoreFiles))
                {
                    list.AddRange(dirIgnoreFiles);
                }

                if (currentDir.Equals(_rootDir, StringComparison.OrdinalIgnoreCase))
                    break;

                currentDir = GetDirectoryPart(currentDir);
            }

            _cachedDir = dirSpan.ToString();
            _cachedIgnoreFiles = list;
            return list;
        }

        private bool IsIgnored(ReadOnlySpan<char> normalizedPath, bool isDirectory)
        {
            ReadOnlySpan<char> dirSpan = isDirectory ? normalizedPath : GetDirectoryPart(normalizedPath);
            var ignoreFiles = GetIgnoreFilesForDirectory(dirSpan);

            foreach (var gitignore in ignoreFiles)
            {
                bool? ignored = gitignore.IsIgnoredNormalized(normalizedPath, isDirectory);
                if (ignored.HasValue)
                {
                    return ignored.Value;
                }
            }

            return false;
        }
    }
}
