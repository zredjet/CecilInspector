using CecilInspector.Cli;
using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class ParallelSearchTests
{
    [Theory]
    [InlineData(SearchScope.Definitions, 1000, 4)]
    [InlineData(SearchScope.All, 1000, 4)]
    [InlineData(SearchScope.All, 7, 2)]
    [InlineData(SearchScope.All, 7, 8)]
    [InlineData(SearchScope.References, 1, 2)]
    [InlineData(SearchScope.References, 1, 8)]
    public void ParallelScanReportsExactlyWhatASequentialScanDoes(SearchScope scope, int maxResults, int parallelism)
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < 6; index++)
        {
            temp.CopyAssembly($"Copy{index}.dll");
        }

        temp.WriteBrokenAssembly("Copy3-broken.dll");
        var options = new SearchOptions(
            temp.Path, "Estimate", SearchKinds.All, scope, MatchMode.Contains, IgnoreCase: true, Recursive: true, SymbolMode.Off,
            maxResults, null, [], Parallelism: 1);

        var sequential = new AssemblySearcher().Search(options);

        // The baseline itself must be meaningful, or the parity below proves nothing: every
        // copy matched, the broken file is the one error, and the limit actually truncated.
        Assert.True(sequential.TotalMatches > 6, $"TotalMatches: {sequential.TotalMatches}");
        Assert.Equal(6, sequential.FilesSucceeded);
        Assert.Single(sequential.Errors);
        Assert.Equal(Math.Min(maxResults, sequential.TotalMatches), sequential.Hits.Count);

        // Repeated because a race in the shared caches would show up only some of the time.
        for (var run = 0; run < 3; run++)
        {
            var parallel = new AssemblySearcher().Search(options with { Parallelism = parallelism });

            Assert.Equal(sequential.TotalMatches, parallel.TotalMatches);
            Assert.Equal(sequential.FilesSucceeded, parallel.FilesSucceeded);
            Assert.Equal(sequential.FilesWithSymbols, parallel.FilesWithSymbols);
            Assert.Equal(sequential.Counts, parallel.Counts);
            Assert.Equal(sequential.Errors.Select(error => (error.FilePath, error.Message)), parallel.Errors.Select(error => (error.FilePath, error.Message)));
            Assert.Equal(sequential.Warnings.Select(warning => warning.Message), parallel.Warnings.Select(warning => warning.Message));
            Assert.Equal(sequential.Hits, parallel.Hits);
        }
    }

    [Fact]
    public void EffectiveParallelismNeverExceedsTheFileCountOrEight()
    {
        var options = new SearchOptions("in", "x", SearchKinds.All, SearchScope.Definitions, MatchMode.Contains, IgnoreCase: true, Recursive: true, SymbolMode.Off, 10, null, []);

        Assert.Equal(1, AssemblySearcher.EffectiveParallelism(options, 1));
        Assert.Equal(Math.Min(Environment.ProcessorCount, 8), AssemblySearcher.EffectiveParallelism(options, 100));
        Assert.InRange(AssemblySearcher.EffectiveParallelism(options, 100), 1, 8);
        Assert.Equal(3, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 3 }, 100));
        Assert.Equal(16, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 16 }, 100));
        Assert.Equal(2, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 16 }, 2));
        Assert.Equal(1, AssemblySearcher.EffectiveParallelism(options with { Parallelism = 16 }, 0));
    }

    [Fact]
    public void CancellationBeforeTheScanIsObserved()
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < 4; index++)
        {
            temp.CopyAssembly($"Copy{index}.dll");
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new SearchOptions(
            temp.Path, "Estimate", SearchKinds.All, SearchScope.All, MatchMode.Contains, IgnoreCase: true, Recursive: true, SymbolMode.Off,
            10, null, [], Parallelism: 4);

        Assert.ThrowsAny<OperationCanceledException>(() => new AssemblySearcher().Search(
            options, AssemblyFiles.DiscoverDetailed(temp.Path, true, TestContext.Current.CancellationToken), cancellation.Token));
    }

    [Fact]
    public void CancellationDuringAScanStopsTheRemainingFiles()
    {
        using var temp = new TempDirectory();
        for (var index = 0; index < 8; index++)
        {
            temp.CopyAssembly($"Copy{index}.dll");
        }

        using var cancellation = new CancellationTokenSource();
        var started = 0;
        var searcher = new AssemblySearcher
        {
            // Cancel from inside the first scan: whatever the other worker had already passed
            // the check for may still start, but none of the six remaining files may.
            FileStarting = _ =>
            {
                Interlocked.Increment(ref started);
                cancellation.Cancel();
            },
        };
        var options = new SearchOptions(
            temp.Path, "Estimate", SearchKinds.All, SearchScope.All, MatchMode.Contains, IgnoreCase: true, Recursive: true, SymbolMode.Off,
            10, null, [], Parallelism: 2);

        Assert.ThrowsAny<OperationCanceledException>(() => searcher.Search(
            options, AssemblyFiles.DiscoverDetailed(temp.Path, true, TestContext.Current.CancellationToken), cancellation.Token));

        Assert.InRange(started, 1, 2);
    }
}
