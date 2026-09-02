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
            var regexOptions = RegexOptions.CultureInvariant;
            if (options.IgnoreCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            try
            {
                _regex = new Regex(options.Query, regexOptions, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"正規表現が不正です: {ex.Message}", ex);
            }
        }
    }

    public bool IsMatch(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            var comparison = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
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
            throw new SearchQueryException("正規表現の照合が250ミリ秒でタイムアウトしました。パターンを簡略化してください。", ex);
        }
    }
}
