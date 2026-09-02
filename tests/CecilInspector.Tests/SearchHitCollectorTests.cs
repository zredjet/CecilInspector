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

    [Fact]
    public void RemainingCapacityShrinksAndStopsMaterializingHits()
    {
        var collector = new SearchHitCollector(2);
        Assert.Equal(2, collector.RemainingCapacity);

        collector.Add(HitScope.Definition, HitKind.Method, () => Hit("First"));
        Assert.Equal(1, collector.RemainingCapacity);

        collector.Add(HitScope.Definition, HitKind.Method, () => Hit("Second"));
        Assert.Equal(0, collector.RemainingCapacity);

        var materialized = false;
        collector.Add(HitScope.Definition, HitKind.Method, () =>
        {
            materialized = true;
            return Hit("Third");
        });

        Assert.False(materialized);
        Assert.Equal(3, collector.TotalMatches);
        Assert.Equal(0, collector.RemainingCapacity);
    }

    [Fact]
    public void ZeroCapacityCollectorCountsWithoutMaterializing()
    {
        var collector = new SearchHitCollector(0);
        var materialized = false;

        collector.Add(HitScope.Reference, HitKind.Type, () =>
        {
            materialized = true;
            return Hit("Only");
        });

        Assert.False(materialized);
        Assert.Equal(1, collector.TotalMatches);
        Assert.Empty(collector.Hits);
        Assert.Equal(1, Assert.Single(collector.Counts).Count);
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
