namespace CecilInspector.Cli;

[Flags]
public enum SearchKinds
{
    None = 0,
    Namespace = 1 << 0,
    Type = 1 << 1,
    Method = 1 << 2,
    Property = 1 << 3,
    Field = 1 << 4,
    Event = 1 << 5,
    All = Namespace | Type | Method | Property | Field | Event,
}

public enum SearchScope
{
    Definitions,
    References,
    All,
}

public enum MatchMode
{
    Contains,
    Exact,
    Regex,
}

public enum SymbolMode
{
    Auto,
    Off,
    Required,
}

/// <summary>
/// Search report layout. <see cref="MsBuild"/> prints one hit per line in the
/// <c>path(line,col): info CODE: message</c> form that Visual Studio's Output window and
/// VS Code's <c>$msCompile</c> problem matcher turn into clickable locations.
/// </summary>
public enum ReportFormat
{
    Text,
    MsBuild,
}

/// <summary>When the console report is colored. The --output file is never colored.</summary>
public enum ColorMode
{
    /// <summary>Color when stdout is a terminal, NO_COLOR is unset and TERM is not "dumb".</summary>
    Auto,
    Always,
    Never,
}

/// <param name="Quiet">
/// Suppress the per-file 警告/情報 lines on stderr (one summary line remains). The exit code and
/// the report's error count are unaffected, so automation still sees an incomplete result.
/// </param>
/// <param name="Color">Whether the console report gets ANSI colors.</param>
public abstract record AppOptions(
    string InputPath,
    bool Recursive,
    string? OutputPath,
    IReadOnlyList<string> ReferencePaths,
    bool Quiet = false,
    ColorMode Color = ColorMode.Auto);

public sealed record SearchOptions(
    string InputPath,
    string Query,
    SearchKinds Kinds,
    SearchScope Scope,
    MatchMode MatchMode,
    bool IgnoreCase,
    bool Recursive,
    SymbolMode SymbolMode,
    int MaxResults,
    string? OutputPath,
    IReadOnlyList<string> ReferencePaths,
    ReportFormat Format = ReportFormat.Text,
    bool Quiet = false,
    ColorMode Color = ColorMode.Auto) : AppOptions(InputPath, Recursive, OutputPath, ReferencePaths, Quiet, Color);

public sealed record DumpOptions(
    string InputPath,
    bool Recursive,
    bool IncludeIl,
    SymbolMode SymbolMode,
    string? OutputPath,
    IReadOnlyList<string> ReferencePaths,
    bool Quiet = false,
    ColorMode Color = ColorMode.Auto) : AppOptions(InputPath, Recursive, OutputPath, ReferencePaths, Quiet, Color);

/// <param name="Options">Parsed command, or null for help/version/errors.</param>
/// <param name="Error">Diagnostic for an invalid invocation; null means help or version was requested.</param>
/// <param name="ShowVersion">True when the user asked for the version string instead of help.</param>
public sealed record ParseResult(AppOptions? Options, string? Error, bool ShowVersion = false)
{
    public bool IsSuccess => Options is not null;

    public static ParseResult Success(AppOptions options) => new(options, null);
    /// <summary>True when help was requested (or is implied), so the caller prints usage and exits 0.</summary>
    public bool IsHelp => Options is null && Error is null && !ShowVersion;

    public static ParseResult Failure(string? error) => new(null, error);
    public static ParseResult Help() => new(null, null);
    public static ParseResult Version() => new(null, null, true);
}
