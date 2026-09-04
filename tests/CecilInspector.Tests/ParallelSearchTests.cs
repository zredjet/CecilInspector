using CecilInspector.Cli;
using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class ParallelSearchTests
{
    private static readonly string ThisAssembly = typeof(ParallelSearchTests).Assembly.Location;

    [Theory]
    [InlineData(SearchScope.Definitions, 1000)]
    [InlineData(SearchScope.All, 1000)]
    [InlineData(SearchScope.All, 7)]
    [InlineData(SearchScope.References, 1)]
    public void ParallelScanReportsExactlyWhatASequentialScanDoes(SearchScope scope, int maxResults)
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < 6; index++)
        {
            File.Copy(ThisAssembly, temp.File($"Copy{index}.dll"));
        }

        File.WriteAllText(temp.File("Copy3-broken.dll"), "not an assembly");
        var options = new SearchOptions(
            temp.Path, "Estimate", SearchKinds.All, scope, MatchMode.Contains, true, true, SymbolMode.Off,
            maxResults, null, [], Parallelism: 1);

        var sequential = new AssemblySearcher().Search(options);
        var parallel = new AssemblySearcher().Search(options with { Parallelism = 4 });

        Assert.Equal(sequential.TotalMatches, parallel.TotalMatches);
        Assert.Equal(sequential.FilesSucceeded, parallel.FilesSucceeded);
        Assert.Equal(sequential.FilesWithSymbols, parallel.FilesWithSymbols);
        Assert.Equal(sequential.Counts, parallel.Counts);
        Assert.Equal(sequential.Errors.Select(error => (error.FilePath, error.Message)), parallel.Errors.Select(error => (error.FilePath, error.Message)));
        Assert.Equal(sequential.Warnings.Select(warning => warning.Message), parallel.Warnings.Select(warning => warning.Message));
        Assert.Equal(sequential.Hits, parallel.Hits);
        Assert.Equal(Math.Min(maxResults, sequential.TotalMatches), parallel.Hits.Count);
    }

    [Fact]
    public void EffectiveParallelismNeverExceedsTheFileCountOrEight()
    {
        var options = new SearchOptions("in", "x", SearchKinds.All, SearchScope.Definitions, MatchMode.Contains, true, true, SymbolMode.Off, 10, null, []);

        Assert.Equal(1, AssemblySearcher.EffectiveParallelism(options, 1));
        Assert.Equal(Math.Min(Environment.ProcessorCount, 8), AssemblySearcher.EffectiveParallelism(options, 100));
        Assert.Equal(3, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 3 }, 100));
        Assert.Equal(2, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 16 }, 2));
        Assert.Equal(1, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 16 }, 0));
    }

    [Fact]
    public void CancellationStopsAParallelScan()
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < 4; index++)
        {
            File.Copy(ThisAssembly, temp.File($"Copy{index}.dll"));
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new SearchOptions(
            temp.Path, "Estimate", SearchKinds.All, SearchScope.All, MatchMode.Contains, true, true, SymbolMode.Off,
            10, null, [], Parallelism: 4);

        Assert.ThrowsAny<OperationCanceledException>(() => new AssemblySearcher().Search(options, AssemblyFiles.DiscoverDetailed(temp.Path, true, TestContext.Current.CancellationToken), cancellation.Token));
    }
}
