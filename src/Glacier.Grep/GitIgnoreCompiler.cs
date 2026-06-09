using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;

namespace Glacier.Grep
{
    /// <summary>
    /// Represents a single rule compiled from a .gitignore line.
    /// </summary>
    public class GitIgnoreRule
    {
        public string Pattern { get; }
        public bool IsDirectoryOnly { get; }
        public bool IsNegation { get; }
        public bool IsRooted { get; }
        public string CleanPattern { get; }

        public GitIgnoreRule(string rawPattern)
        {
            Pattern = rawPattern;
            string pattern = rawPattern.Trim();

            // Negation prefix
            if (pattern.StartsWith('!'))
            {
                IsNegation = true;
                pattern = pattern.Substring(1).Trim();
            }
            else
            {
                IsNegation = false;
            }

            // Directory-only suffix
            if (pattern.EndsWith('/'))
            {
                IsDirectoryOnly = true;
                pattern = pattern.Substring(0, pattern.Length - 1);
            }
            else
            {
                IsDirectoryOnly = false;
            }

            // Rooted prefix or presence of internal slashes
            if (pattern.StartsWith('/'))
            {
                IsRooted = true;
                pattern = pattern.Substring(1);
            }
            else if (pattern.Contains('/'))
            {
                IsRooted = true;
            }
            else
            {
                IsRooted = false;
            }

            CleanPattern = pattern;
        }

        public bool Matches(ReadOnlySpan<char> relativePath, bool isDirectory)
        {
            // If the rule matches the path itself directly
            if (MatchesPath(relativePath))
            {
                if (!IsDirectoryOnly || isDirectory)
                    return true;
            }

            // If the rule is directory-only, it can match any parent directory of the path
            int lastSlash;
            ReadOnlySpan<char> parentPath = relativePath;
            while ((lastSlash = parentPath.LastIndexOf('/')) >= 0)
            {
                parentPath = parentPath.Slice(0, lastSlash);
                if (MatchesPath(parentPath))
                {
                    return true;
                }
            }

            return false;
        }

        private bool MatchesPath(ReadOnlySpan<char> path)
        {
            if (IsRooted)
            {
                // Must match the prefix or full path exactly
                if (FileSystemName.MatchesSimpleExpression(CleanPattern, path, ignoreCase: true))
                    return true;

                if (path.StartsWith(CleanPattern, StringComparison.OrdinalIgnoreCase))
                {
                    if (path.Length > CleanPattern.Length && path[CleanPattern.Length] == '/')
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Can match any segment of the relative path
                int slashIdx;
                ReadOnlySpan<char> remaining = path;
                while ((slashIdx = remaining.IndexOf('/')) >= 0)
                {
                    ReadOnlySpan<char> segment = remaining.Slice(0, slashIdx);
                    if (FileSystemName.MatchesSimpleExpression(CleanPattern, segment, ignoreCase: true))
                        return true;
                    remaining = remaining.Slice(slashIdx + 1);
                }
                if (FileSystemName.MatchesSimpleExpression(CleanPattern, remaining, ignoreCase: true))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Represents a compiled .gitignore file.
    /// </summary>
    public class GitIgnoreFile
    {
        public string DirectoryPath { get; }
        public List<GitIgnoreRule> Rules { get; } = new();

        public GitIgnoreFile(string directoryPath, string[] lines)
        {
            DirectoryPath = directoryPath.Replace('\\', '/').TrimEnd('/');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;
                Rules.Add(new GitIgnoreRule(trimmed));
            }
        }

        /// <summary>
        /// Evaluates if a given path is ignored by this .gitignore file.
        /// Returns true if ignored, false if negated (re-included), and null if no rule matches.
        /// </summary>
        public bool? IsIgnored(string fullPath, bool isDirectory)
        {
            string normalizedFullPath = fullPath.Replace('\\', '/');
            if (!normalizedFullPath.StartsWith(DirectoryPath, StringComparison.OrdinalIgnoreCase))
                return null;

            int relStart = DirectoryPath.Length;
            if (relStart < normalizedFullPath.Length && normalizedFullPath[relStart] == '/')
                relStart++;

            string relativePath = normalizedFullPath.Substring(relStart);
            if (string.IsNullOrEmpty(relativePath))
                return null;

            bool? result = null;
            // Later rules take precedence over earlier ones
            foreach (var rule in Rules)
            {
                if (rule.Matches(relativePath, isDirectory))
                {
                    result = !rule.IsNegation;
                }
            }
            return result;
        }
    }
}
