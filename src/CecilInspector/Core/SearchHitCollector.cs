namespace CecilInspector.Core;

internal sealed class SearchHitCollector
{
    private readonly int _retentionLimit;
    private readonly List<SearchHit> _hits;
    private readonly Dictionary<(HitScope Scope, HitKind Kind), int> _counts = [];

    public SearchHitCollector(int retentionLimit)
    {
        _retentionLimit = retentionLimit;
        _hits = new List<SearchHit>(Math.Min(retentionLimit, 1024));
    }

    public int TotalMatches { get; private set; }

    public void Add(HitScope scope, HitKind kind, Func<SearchHit> createHit)
    {
        TotalMatches = checked(TotalMatches + 1);
        var key = (scope, kind);
        _counts[key] = checked(_counts.GetValueOrDefault(key) + 1);

        if (_hits.Count < _retentionLimit)
        {
            _hits.Add(createHit());
        }
    }

    public void Merge(SearchHitCollector source)
    {
        TotalMatches = checked(TotalMatches + source.TotalMatches);
        foreach (var count in source._counts)
        {
            _counts[count.Key] = checked(_counts.GetValueOrDefault(count.Key) + count.Value);
        }

        var remaining = _retentionLimit - _hits.Count;
        if (remaining > 0)
        {
            _hits.AddRange(source._hits.Take(remaining));
        }
    }

    public IReadOnlyList<SearchHit> Hits => _hits;

    public IReadOnlyList<HitCount> Counts => _counts
        .OrderBy(item => item.Key.Scope)
        .ThenBy(item => item.Key.Kind)
        .Select(item => new HitCount(item.Key.Scope, item.Key.Kind, item.Value))
        .ToArray();
}
