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
                    Path.GetRelativePath(_rootDir, entry.ToFullPath()),
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
            if (!_searchHidden && fileName.StartsWith("."))
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

            // Perform full ignore check
            string fullPath = entry.ToFullPath();
            return !IsIgnored(fullPath, isDirectory: false);
        }

        private bool ShouldRecurseEntry(ref FileSystemEntry entry)
        {
            ReadOnlySpan<char> dirName = entry.FileName;

            if (!_searchHidden && dirName.StartsWith("."))
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

            string fullPath = entry.ToFullPath();

            // Evaluate recursion depth
            if (_maxDepth.HasValue)
            {
                string relPath = Path.GetRelativePath(_rootDir, fullPath);
                int depth = relPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (depth > _maxDepth.Value)
                    return false;
            }

            // Evaluate ignore rules for this directory
            if (IsIgnored(fullPath, isDirectory: true))
                return false;

            // Dynamically load nested ignore files in this directory
            LoadDirectoryIgnoreFiles(fullPath);

            return true;
        }

        private bool IsIgnored(string fullPath, bool isDirectory)
        {
            string normalizedPath = fullPath.Replace('\\', '/');

            // Evaluate ignore files from the item's directory up to root directory
            string? currentDir = isDirectory ? normalizedPath : Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/');

            while (currentDir != null)
            {
                if (_ignoreFileCache.TryGetValue(currentDir, out var list))
                {
                    foreach (var gitignore in list)
                    {
                        bool? ignored = gitignore.IsIgnored(normalizedPath, isDirectory);
                        if (ignored.HasValue)
                        {
                            return ignored.Value;
                        }
                    }
                }

                if (currentDir.Equals(_rootDir, StringComparison.OrdinalIgnoreCase))
                    break;

                currentDir = Path.GetDirectoryName(currentDir)?.Replace('\\', '/');
            }

            return false;
        }
    }
}
