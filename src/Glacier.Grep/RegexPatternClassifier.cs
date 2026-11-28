using System;
using System.Text;

namespace Glacier.Grep;

internal enum RegexPatternClass
{
    FullRegex,
    Literal,
    StartsWith,
    EndsWith,
    Equals,
    PrefixAndSuffix,
    ContainsBothOrdered
}

/// <summary>
/// Self-contained port of Opt #12 (<c>ClassifyPatternFull</c>) tailored for <see cref="SearchEngine"/>'s
/// exact <c>RegexOptions.Multiline | RegexOptions.NonBacktracking</c> and inverted line-matching semantics.
/// </summary>
internal static class RegexPatternClassifier
{
    private static bool HasRegexMetaChars(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' || c == '*' || c == '+' || c == '?' || c == '|' ||
                c == '{' || c == '}' || c == '[' || c == ']' || c == '(' || c == ')' ||
                c == '.' || c == '#' || c == '^' || c == '$')
            {
                return true;
            }
        }
        return false;
    }

    public static (RegexPatternClass Class, string Literal, string Suffix) ClassifyPatternFull(
        string pattern,
        bool caseSensitive,
        bool invertMatch)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return (RegexPatternClass.FullRegex, pattern ?? string.Empty, string.Empty);
        }

        // Control characters / newlines in literal patterns can affect Multiline matching edge cases; keep general Regex for embedded CR/LF.
        if (pattern.IndexOfAny('\r', '\n') >= 0)
        {
            return (RegexPatternClass.FullRegex, pattern, string.Empty);
        }

        bool startsWithAnchor = pattern.StartsWith('^');
        bool endsWithAnchor = pattern.EndsWith('$');

        RegexPatternClass cls = RegexPatternClass.FullRegex;
        string literal = pattern;
        string suffix = string.Empty;

        if (startsWithAnchor && endsWithAnchor)
        {
            if (pattern.Length <= 2)
            {
                return (RegexPatternClass.FullRegex, pattern, string.Empty);
            }

            string inner = pattern.Substring(1, pattern.Length - 2);
            int dotStar = inner.IndexOf(".*", StringComparison.Ordinal);
            if (dotStar >= 0 && inner.IndexOf(".*", dotStar + 2, StringComparison.Ordinal) < 0)
            {
                string p1 = inner.Substring(0, dotStar);
                string p2 = inner.Substring(dotStar + 2);
                if (p1.Length > 0 && p2.Length > 0 && !HasRegexMetaChars(p1) && !HasRegexMetaChars(p2))
                {
                    cls = RegexPatternClass.PrefixAndSuffix;
                    literal = p1;
                    suffix = p2;
                }
            }
            else if (inner.Length > 0 && !HasRegexMetaChars(inner))
            {
                cls = RegexPatternClass.Equals;
                literal = inner;
            }
        }
        else if (startsWithAnchor)
        {
            string inner = pattern.Substring(1);
            if (invertMatch && inner.EndsWith(".*", StringComparison.Ordinal))
            {
                string prefix = inner.Substring(0, inner.Length - 2);
                if (prefix.Length > 0 && !HasRegexMetaChars(prefix))
                {
                    cls = RegexPatternClass.StartsWith;
                    literal = prefix;
                }
            }
            else if (inner.Length > 0 && !HasRegexMetaChars(inner))
            {
                cls = RegexPatternClass.StartsWith;
                literal = inner;
            }
        }
        else if (endsWithAnchor)
        {
            string inner = pattern.Substring(0, pattern.Length - 1);
            if (invertMatch && inner.StartsWith(".*", StringComparison.Ordinal))
            {
                string endPart = inner.Substring(2);
                if (endPart.Length > 0 && !HasRegexMetaChars(endPart))
                {
                    cls = RegexPatternClass.EndsWith;
                    literal = endPart;
                }
            }
            else if (inner.Length > 0 && !HasRegexMetaChars(inner))
            {
                cls = RegexPatternClass.EndsWith;
                literal = inner;
            }
        }
        else
        {
            if (invertMatch && pattern.StartsWith(".*", StringComparison.Ordinal) && pattern.EndsWith(".*", StringComparison.Ordinal) && pattern.Length >= 4)
            {
                string inner = pattern.Substring(2, pattern.Length - 4);
                if (inner.Length > 0 && !HasRegexMetaChars(inner))
                {
                    cls = RegexPatternClass.Literal;
                    literal = inner;
                }
            }
            else
            {
                int dotStar = pattern.IndexOf(".*", StringComparison.Ordinal);
                if (dotStar >= 0 && pattern.IndexOf(".*", dotStar + 2, StringComparison.Ordinal) < 0)
                {
                    string p1 = pattern.Substring(0, dotStar);
                    string p2 = pattern.Substring(dotStar + 2);
                    if (!HasRegexMetaChars(p1) && !HasRegexMetaChars(p2))
                    {
                        if (p1.Length > 0 && p2.Length > 0)
                        {
                            cls = RegexPatternClass.ContainsBothOrdered;
                            literal = p1;
                            suffix = p2;
                        }
                        else if (invertMatch && p1.Length == 0 && p2.Length > 0)
                        {
                            cls = RegexPatternClass.Literal;
                            literal = p2;
                        }
                        else if (invertMatch && p2.Length == 0 && p1.Length > 0)
                        {
                            cls = RegexPatternClass.Literal;
                            literal = p1;
                        }
                    }
                }
                else if (!HasRegexMetaChars(pattern))
                {
                    cls = RegexPatternClass.Literal;
                    literal = pattern;
                }
            }
        }

        if (cls == RegexPatternClass.FullRegex)
        {
            return (RegexPatternClass.FullRegex, pattern, string.Empty);
        }

        // Case-insensitive UTF-8 byte lowering is restricted to ASCII literals so Unicode case-folding stays exact on Regex.
        if (!caseSensitive && (!Ascii.IsValid(literal) || (!string.IsNullOrEmpty(suffix) && !Ascii.IsValid(suffix))))
        {
            return (RegexPatternClass.FullRegex, pattern, string.Empty);
        }

        return (cls, literal, suffix);
    }
}
