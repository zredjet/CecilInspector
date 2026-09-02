using System.Text.RegularExpressions;
using CecilInspector.Cli;

namespace CecilInspector.Core;

internal sealed class SearchMatcher
{
    private readonly SearchOptions _options;
    private readonly Regex? _regex;

    public SearchMatcher(SearchOptions options)
    {
        _options = options;
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
