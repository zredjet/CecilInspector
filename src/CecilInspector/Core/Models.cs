using CecilInspector.Cli;

namespace CecilInspector.Core;

public enum HitScope
{
    Definition,
    Reference,
}

public enum HitKind
{
    Namespace,
    Type,
    Method,
    Property,
    Field,
    Event,
}

public sealed record SourceLocation(string Document, int Line, int Column)
{
    public override string ToString() => Column > 0 ? $"{Document}:{Line}:{Column}" : $"{Document}:{Line}";
}

public sealed record SearchHit(
    string AssemblyPath,
    string AssemblyName,
    HitScope Scope,
    HitKind Kind,
    string Symbol,
    string? Container,
    SourceLocation? Location,
    int? IlOffset);

public sealed record ScanError(string FilePath, string Message);

public sealed record HitCount(HitScope Scope, HitKind Kind, int Count);

public sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    int TotalMatches,
    IReadOnlyList<HitCount> Counts,
    IReadOnlyList<ScanError> Errors,
    int FilesDiscovered,
    int FilesSucceeded,
    int FilesWithSymbols);

public sealed record DumpResult(
    IReadOnlyList<ScanError> Errors,
    int FilesDiscovered,
    int FilesSucceeded);

internal static class KindMapping
{
    public static bool Includes(this SearchKinds kinds, HitKind kind) => kind switch
    {
        HitKind.Namespace => kinds.HasFlag(SearchKinds.Namespace),
        HitKind.Type => kinds.HasFlag(SearchKinds.Type),
        HitKind.Method => kinds.HasFlag(SearchKinds.Method),
        HitKind.Property => kinds.HasFlag(SearchKinds.Property),
        HitKind.Field => kinds.HasFlag(SearchKinds.Field),
        HitKind.Event => kinds.HasFlag(SearchKinds.Event),
        _ => false,
    };
}
