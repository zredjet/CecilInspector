using CecilInspector.Core;
using System.Text.RegularExpressions;
using Xunit;

namespace CecilInspector.Tests;

public sealed class SearchRegexTests
{
    [Fact]
    public void PrefersNonBacktrackingEngine()
    {
        var regex = SearchRegex.Create("^(.+)+Z$", ignoreCase: true);

        Assert.True(regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(regex.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.Equal(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
        Assert.DoesNotMatch(regex, new string('a', 40));
    }

    [Fact]
    public void FallsBackToBacktrackingWithTimeoutForUnsupportedConstructs()
    {
        var regex = SearchRegex.Create("^(?!get_)Save$", ignoreCase: false);

        Assert.False(regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.False(regex.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.Equal(SearchRegex.MatchTimeout, regex.MatchTimeout);
        Assert.Matches(regex, "Save");
        Assert.DoesNotMatch(regex, "get_Save");
    }

    [Fact]
    public void InvalidPatternIsAnArgumentError()
    {
        Assert.Throws<RegexParseException>(() => SearchRegex.Create("[", ignoreCase: true));
        Assert.IsAssignableFrom<ArgumentException>(
            Record.Exception(() => SearchRegex.Create("[", ignoreCase: true)));
    }
}
