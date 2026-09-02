using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class SearchHitCollectorTests
{
    [Fact]
    public void MergePreservesCountsAndGlobalRetentionLimit()
    {
        var destination = new SearchHitCollector(2);
        var source = new SearchHitCollector(2);
        destination.Add(HitScope.Definition, HitKind.Method, () => Hit("First"));
        source.Add(HitScope.Reference, HitKind.Property, () => Hit("Second"));
        source.Add(HitScope.Reference, HitKind.Property, () => Hit("Third"));

        destination.Merge(source);

        Assert.Equal(3, destination.TotalMatches);
        Assert.Equal(2, destination.Hits.Count);
        Assert.Equal(3, destination.Counts.Sum(count => count.Count));
    }

    private static SearchHit Hit(string symbol) => new(
        "test.dll",
        "Test",
        HitScope.Definition,
        HitKind.Method,
        symbol,
        null,
        null,
        null);
}
