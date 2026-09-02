using CecilInspector.Core;
using System.Globalization;
using System.Reflection;

namespace CecilInspector.Cli;

public static class CommandLine
{
    public const string HelpText = """
        CecilInspector - .NETアセンブリのメタデータ検索ツール

        使用方法:
          cecil-inspector search <assembly-or-directory> <query> [options]
          cecil-inspector dump   <assembly-or-directory> [options]
          cecil-inspector --version | -v
          cecil-inspector --help | -h

        オプションは位置引数の前後どちらにも置けます。値は '--kind method' と '--kind=method' の
        どちらの形式でも指定できます。'--' 以降は、下記のオプション名そのものを除いて '-' で始まる
        文言も位置引数として扱うので、'-' で始まる検索文言は '--' の後に指定してください。
        --reference-path 以外の値付きオプションは1回だけ指定できます（--kind は和集合になります）。

        search options:
          --kind <list>          namespace,type,method,property,field,event,all
                                 (既定: all、カンマ区切り、複数回指定時は和集合)
          --scope <value>        definitions | references | all (既定: definitions)
          --match <value>        contains | exact | regex (既定: contains)
          --format <value>       text | msbuild (既定: text)
                                 msbuildはエディターが解釈できる path(line,col): 形式で出力する
                                 (--symbols off とは併用不可)
          --case-sensitive       大文字・小文字を区別する (既定: 区別しない)
          --symbols <value>      auto | off | required (既定: auto)
          --max-results <number> 保持して表示する最大件数 (総件数は別途集計、既定: 1000)
          --no-recursive         フォルダのサブディレクトリを検索しない
          --output, -o <file>    コンソールと同じ内容を新規UTF-8ファイルへ保存する
          --reference-path <dir> 依存アセンブリの検索フォルダ (複数回指定可)

        dump options:
          --include-il           メソッド本体のIL命令も出力する
          --symbols <value>      auto | off | required (既定: auto)
          --no-recursive         フォルダのサブディレクトリを走査しない
          --output, -o <file>    コンソールと同じ内容を新規UTF-8ファイルへ逐次保存する
          --reference-path <dir> 依存アセンブリの検索フォルダ (複数回指定可)

        例:
          cecil-inspector search ./bin CustomerService --kind type,method
          cecil-inspector search app.dll Save --kind method --scope all --match exact
          cecil-inspector search app.dll -- -Prefixed --match exact
          cecil-inspector search ./bin Save --scope all --format msbuild
          cecil-inspector dump app.dll --include-il --output metadata.txt
        """;

    private static readonly HashSet<string> SearchOptionNames = new(StringComparer.Ordinal)
    {
        "--kind", "--kinds", "--scope", "--match", "--symbols", "--format", "--case-sensitive",
        "--no-recursive", "--max-results", "--output", "-o", "--reference-path",
    };

    private static readonly HashSet<string> DumpOptionNames = new(StringComparer.Ordinal)
    {
        "--include-il", "--no-recursive", "--symbols", "--output", "-o", "--reference-path",
    };

    /// <summary>Product version without the source-revision suffix (the csproj Version, e.g. "1.2.3").</summary>
    public static string VersionText
    {
        get
        {
            var informational = typeof(CommandLine).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = informational ?? typeof(CommandLine).Assembly.GetName().Version?.ToString() ?? "unknown";
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? version[..plus] : version;
        }
    }

    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return ParseResult.Failure("コマンド (search または dump) を指定してください。");
        }

        if (IsHelpToken(args[0]))
        {
            return ParseResult.Help();
        }

        if (args.Length == 1 && IsVersionToken(args[0]))
        {
            return ParseResult.Version();
        }

        return args[0].ToLowerInvariant() switch
        {
            "search" => IsSubcommandHelp(args) ? ParseResult.Help() : ParseSearch(args),
            "dump" => IsSubcommandHelp(args) ? ParseResult.Help() : ParseDump(args),
            _ => ParseResult.Failure($"不明なコマンド '{args[0]}' です。"),
        };
    }

    private static ParseResult ParseSearch(string[] args)
    {
        var kinds = SearchKinds.None;
        var scope = SearchScope.Definitions;
        var matchMode = MatchMode.Contains;
        var format = ReportFormat.Text;
        var ignoreCase = true;
        var recursive = true;
        var symbolMode = SymbolMode.Auto;
        var maxResults = 1000;
        string? output = null;
        var referencePaths = new List<string>();

        var reader = new ArgumentReader(args, 1, SearchOptionNames);
        while (reader.TryNextOption(out var option))
        {
            if (reader.IsRepeated(option, out var repeatedError))
            {
                return ParseResult.Failure(repeatedError);
            }

            switch (option)
            {
                case ArgumentReader.HelpOption:
                    return ParseResult.Help();
                case "--kind" or "--kinds":
                    if (!reader.TryTakeValue(out var kindText) || !TryParseKinds(kindText, out var parsedKinds))
                    {
                        return ParseResult.Failure("--kindには namespace,type,method,property,field,event,all を指定してください。");
                    }

                    kinds |= parsedKinds;
                    break;
                case "--scope":
                    if (!reader.TryTakeEnum(out scope))
                    {
                        return ParseResult.Failure("--scopeには definitions, references, all を指定してください。");
                    }

                    break;
                case "--match":
                    if (!reader.TryTakeEnum(out matchMode))
                    {
                        return ParseResult.Failure("--matchには contains, exact, regex を指定してください。");
                    }

                    break;
                case "--symbols":
                    if (!reader.TryTakeEnum(out symbolMode))
                    {
                        return ParseResult.Failure("--symbolsには auto, off, required を指定してください。");
                    }

                    break;
                case "--format":
                    if (!reader.TryTakeEnum(out format))
                    {
                        return ParseResult.Failure("--formatには text, msbuild を指定してください。");
                    }

                    break;
                case "--case-sensitive":
                    if (reader.HasInlineValue)
                    {
                        return ValueNotAllowed(option);
                    }

                    ignoreCase = false;
                    break;
                case "--no-recursive":
                    if (reader.HasInlineValue)
                    {
                        return ValueNotAllowed(option);
                    }

                    recursive = false;
                    break;
                case "--max-results":
                    if (!reader.TryTakeValue(out var maxText))
                    {
                        return ParseResult.Failure("--max-resultsには1以上の整数を指定してください。");
                    }

                    if (!int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out maxResults))
                    {
                        return ParseResult.Failure(maxText.Length > 0 && maxText.All(char.IsAsciiDigit)
                            ? $"--max-resultsの値が大きすぎます (最大 {int.MaxValue})。"
                            : "--max-resultsには1以上の整数を指定してください。");
                    }

                    if (maxResults < 1)
                    {
                        return ParseResult.Failure("--max-resultsには1以上の整数を指定してください。");
                    }

                    break;
                case "--output" or "-o":
                    if (!reader.TryTakeValue(out output) || string.IsNullOrWhiteSpace(output))
                    {
                        return ParseResult.Failure("--outputにはファイルパスが必要です。");
                    }

                    break;
                case "--reference-path":
                    if (!reader.TryTakeValue(out var referencePath) || string.IsNullOrWhiteSpace(referencePath))
                    {
                        return ParseResult.Failure("--reference-pathにはフォルダパスが必要です。");
                    }

                    referencePaths.Add(referencePath);
                    break;
                default:
                    return ParseResult.Failure(UnknownOptionMessage(reader.CurrentToken, hintSeparator: true));
            }
        }

        if (reader.Positionals.Count < 2)
        {
            return ParseResult.Failure("searchには入力パスと検索文言が必要です。");
        }

        if (reader.Positionals.Count > 2)
        {
            return ParseResult.Failure($"余分な引数 '{reader.Positionals[2]}' です。");
        }

        var input = reader.Positionals[0];
        var query = reader.Positionals[1];
        if (string.IsNullOrEmpty(query))
        {
            return ParseResult.Failure("検索文言を空にはできません。");
        }

        if (format == ReportFormat.MsBuild && symbolMode == SymbolMode.Off)
        {
            return ParseResult.Failure(
                "--format msbuild は --symbols off と併用できません。PDBを読まないと path(line,col): 付きの行を出力できません。");
        }

        if (matchMode == MatchMode.Regex)
        {
            try
            {
                _ = SearchRegex.Create(query, ignoreCase);
            }
            catch (ArgumentException ex)
            {
                return ParseResult.Failure($"正規表現が不正です: {ex.Message}");
            }
        }

        return ParseResult.Success(new SearchOptions(
            input,
            query,
            kinds == SearchKinds.None ? SearchKinds.All : kinds,
            scope,
            matchMode,
            ignoreCase,
            recursive,
            symbolMode,
            maxResults,
            output,
            referencePaths,
            format));
    }

    private static ParseResult ParseDump(string[] args)
    {
        var recursive = true;
        var includeIl = false;
        var symbolMode = SymbolMode.Auto;
        string? output = null;
        var referencePaths = new List<string>();

        var reader = new ArgumentReader(args, 1, DumpOptionNames);
        while (reader.TryNextOption(out var option))
        {
            if (reader.IsRepeated(option, out var repeatedError))
            {
                return ParseResult.Failure(repeatedError);
            }

            switch (option)
            {
                case ArgumentReader.HelpOption:
                    return ParseResult.Help();
                case "--include-il":
                    if (reader.HasInlineValue)
                    {
                        return ValueNotAllowed(option);
                    }

                    includeIl = true;
                    break;
                case "--no-recursive":
                    if (reader.HasInlineValue)
                    {
                        return ValueNotAllowed(option);
                    }

                    recursive = false;
                    break;
                case "--symbols":
                    if (!reader.TryTakeEnum(out symbolMode))
                    {
                        return ParseResult.Failure("--symbolsには auto, off, required を指定してください。");
                    }

                    break;
                case "--output" or "-o":
                    if (!reader.TryTakeValue(out output) || string.IsNullOrWhiteSpace(output))
                    {
                        return ParseResult.Failure("--outputにはファイルパスが必要です。");
                    }

                    break;
                case "--reference-path":
                    if (!reader.TryTakeValue(out var referencePath) || string.IsNullOrWhiteSpace(referencePath))
                    {
                        return ParseResult.Failure("--reference-pathにはフォルダパスが必要です。");
                    }

                    referencePaths.Add(referencePath);
                    break;
                default:
                    return ParseResult.Failure(UnknownOptionMessage(reader.CurrentToken, hintSeparator: false));
            }
        }

        if (reader.Positionals.Count < 1)
        {
            return ParseResult.Failure("dumpには入力パスが必要です。");
        }

        if (reader.Positionals.Count > 1)
        {
            return ParseResult.Failure($"余分な引数 '{reader.Positionals[1]}' です。");
        }

        return ParseResult.Success(new DumpOptions(
            reader.Positionals[0], recursive, includeIl, symbolMode, output, referencePaths));
    }

    private static string UnknownOptionMessage(string token, bool hintSeparator)
    {
        var message = $"不明なオプション '{token}' です。";
        if (token.StartsWith("--", StringComparison.Ordinal) && token.Contains('='))
        {
            return message + "値は '--name value' または '--name=value' の形式で、既知のオプション名に続けて指定してください。";
        }

        return hintSeparator
            ? message + "'-'で始まる検索文言は '--' の後に指定してください。"
            : message;
    }

    private static ParseResult ValueNotAllowed(string option) =>
        ParseResult.Failure($"オプション '{option}' は値を取りません。");

    private static bool TryParseKinds(string text, out SearchKinds kinds)
    {
        kinds = SearchKinds.None;
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseEnumName(item, out SearchKinds parsed) || parsed == SearchKinds.None)
            {
                return false;
            }

            kinds |= parsed;
        }

        return kinds != SearchKinds.None;
    }

    /// <summary>
    /// Parses an enum by name only. <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> also
    /// accepts numeric text, which is never what a user means on the command line.
    /// </summary>
    private static bool TryParseEnumName<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        return text.Length > 0 &&
               !char.IsAsciiDigit(text[0]) && text[0] is not ('-' or '+') &&
               Enum.TryParse(text, true, out value) &&
               Enum.IsDefined(value);
    }

    /// <summary>A bare "help" right after the subcommand; "--help"/"-h" anywhere is handled by the reader.</summary>
    private static bool IsSubcommandHelp(string[] args) =>
        args.Length == 2 && IsHelpToken(args[1]);

    private static bool IsVersionToken(string value) =>
        value.Equals("version", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-v", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpToken(string value) =>
        value.Equals("help", StringComparison.OrdinalIgnoreCase) ||
        IsHelpOption(value);

    private static bool IsHelpOption(string value) =>
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-h", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks the arguments after the subcommand. Tokens starting with '-' are options and may carry
    /// their value inline as "--name=value"; everything else is positional. After a bare "--" only
    /// the known option names are still options, so a query such as "-Prefixed" (or "--unknown")
    /// can follow the separator while "--match exact" keeps working after it, as the help shows.
    /// </summary>
    private sealed class ArgumentReader(string[] args, int start, IReadOnlySet<string> knownOptions)
    {
        public const string HelpOption = "--help";

        private static readonly HashSet<string> RepeatableOptions = new(StringComparer.Ordinal)
        {
            "--kind", "--kinds", "--reference-path", "--case-sensitive", "--no-recursive", "--include-il",
        };

        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private int _index = start;
        private bool _afterSeparator;
        private string? _inlineValue;

        public List<string> Positionals { get; } = [];

        /// <summary>The argument the current option came from, including any "=value" part.</summary>
        public string CurrentToken { get; private set; } = string.Empty;

        /// <summary>True while the current option was written as "--name=value" and the value is untaken.</summary>
        public bool HasInlineValue => _inlineValue is not null;

        public bool TryNextOption(out string option)
        {
            _inlineValue = null;
            while (_index < args.Length)
            {
                var token = args[_index++];
                if (!_afterSeparator && token == "--")
                {
                    _afterSeparator = true;
                    continue;
                }

                if (IsOptionToken(token))
                {
                    var (name, inlineValue) = SplitInlineValue(token);
                    if (!_afterSeparator && IsHelpOption(name))
                    {
                        option = HelpOption;
                        CurrentToken = token;
                        return true;
                    }

                    if (!_afterSeparator || knownOptions.Contains(name))
                    {
                        option = name;
                        CurrentToken = token;
                        _inlineValue = inlineValue;
                        return true;
                    }
                }

                Positionals.Add(token);
            }

            option = string.Empty;
            return false;
        }

        /// <summary>
        /// Rejects a second occurrence of a value option; a silently winning last value would make a
        /// wrapper script's appended --output overwrite the user's own without any diagnostic.
        /// </summary>
        public bool IsRepeated(string option, out string error)
        {
            error = string.Empty;
            var canonical = option == "-o" ? "--output" : option;
            if (RepeatableOptions.Contains(canonical) || canonical == HelpOption || _seen.Add(canonical))
            {
                return false;
            }

            error = $"オプション '{canonical}' が重複しています。1回だけ指定してください。";
            return true;
        }

        public bool TryTakeValue(out string value)
        {
            if (_inlineValue is not null)
            {
                value = _inlineValue;
                _inlineValue = null;
                return true;
            }

            if (_index >= args.Length || IsSeparatorOrOption(args[_index]))
            {
                value = string.Empty;
                return false;
            }

            value = args[_index++];
            return true;
        }

        public bool TryTakeEnum<T>(out T value) where T : struct, Enum
        {
            value = default;
            return TryTakeValue(out var text) && TryParseEnumName(text, out value);
        }

        private bool IsSeparatorOrOption(string token)
        {
            if (token == "--")
            {
                return true;
            }

            if (!IsOptionToken(token))
            {
                return false;
            }

            var (name, _) = SplitInlineValue(token);
            return !_afterSeparator || knownOptions.Contains(name);
        }

        private static (string Name, string? Value) SplitInlineValue(string token)
        {
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                return (token, null);
            }

            var equals = token.IndexOf('=', StringComparison.Ordinal);
            return equals < 0 ? (token, null) : (token[..equals], token[(equals + 1)..]);
        }

        private static bool IsOptionToken(string value) =>
            value.StartsWith('-') && value != "--";
    }
}
