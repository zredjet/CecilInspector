using System.Text.RegularExpressions;

namespace CecilInspector.Core;

/// <summary>
/// Single place that turns a user pattern into a <see cref="Regex"/> for both CLI validation and
/// matching. The non-backtracking engine is preferred because it guarantees linear matching time
/// against the millions of metadata strings a search visits; patterns that need constructs it
/// does not support (lookarounds, backreferences, atomic groups, conditionals) fall back to the
/// backtracking engine guarded by <see cref="MatchTimeout"/>.
/// </summary>
internal static class SearchRegex
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static string TimeoutMessage =>
        $"正規表現の照合が{MatchTimeout.TotalMilliseconds:0}ミリ秒でタイムアウトしました。パターンを簡略化してください。";

    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public static Regex Create(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, options, MatchTimeout);
        }
    }
}
