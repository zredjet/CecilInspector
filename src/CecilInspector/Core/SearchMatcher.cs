using CecilInspector.Cli;
using System.Text.RegularExpressions;

namespace CecilInspector.Core;

internal sealed class SearchMatcher
{
    /// <summary>
    /// Characters whose rendering differs between Cecil's FullName and the tool's canonical
    /// symbol: nesting ('+' vs '/'), parameter separators (", " vs ","), generic parameters
    /// ("!0" vs "T"), generic arity, scope suffixes and the " : return" tail.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> FormattingSensitiveCharacters =
        System.Buffers.SearchValues.Create("+/, !@`");

    private readonly SearchOptions _options;
    private readonly Regex? _regex;

    public SearchMatcher(SearchOptions options)
    {
        _options = options;
        RequiresFormattedCandidates =
            options.MatchMode == MatchMode.Regex ||
            options.Query.AsSpan().ContainsAny(FormattingSensitiveCharacters);
        if (options.MatchMode == MatchMode.Regex)
        {
            try
            {
                _regex = SearchRegex.Create(options.Query, options.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                // The same exit code (1) whether the pattern is rejected by the CLI up front or
                // by a library caller that skipped that validation.
                throw new SearchQueryException($"正規表現が不正です: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// False when the query can only match through the names Cecil already holds (name,
    /// logical name, FullName, qualified names), so the canonical symbol need not be built for
    /// members that do not match; true for regex queries and queries containing characters
    /// that only the canonical form renders (see <see cref="FormattingSensitiveCharacters"/>).
    /// </summary>
    public bool RequiresFormattedCandidates { get; }

    /// <summary>
    /// Matches a member definition in two stages: the cheap names first, then the formatted
    /// symbol only when the query can depend on it. Building the symbol for every method of
    /// every type dominated definition searches (its collision check formats each type's
    /// methods twice), and for a plain query it can never match when the names do not.
    /// </summary>
    public bool IsMemberMatch(
        string name,
        string logicalName,
        string fullName,
        string declaringType,
        Func<string> symbol)
    {
        if (IsMatch(name, logicalName, fullName, CecilFormatting.MemberName(declaringType, name)))
        {
            return true;
        }

        if (!string.Equals(name, logicalName, StringComparison.Ordinal) &&
            IsMatch(CecilFormatting.MemberName(declaringType, logicalName)))
        {
            return true;
        }

        return RequiresFormattedCandidates && IsMatch(symbol());
    }

    public bool IsMatch(params ReadOnlySpan<string?> candidates)
    {
        var comparison = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            var matches = _options.MatchMode switch
            {
                MatchMode.Contains => candidate.Contains(_options.Query, comparison),
                MatchMode.Exact => candidate.Equals(_options.Query, comparison),
                MatchMode.Regex => IsRegexMatch(candidate),
                _ => false,
            };

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRegexMatch(string candidate)
    {
        try
        {
            return _regex!.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new SearchQueryException(SearchRegex.TimeoutMessage, ex);
        }
    }
}
