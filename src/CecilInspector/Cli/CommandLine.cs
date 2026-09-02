using System.Globalization;
using System.Reflection;
using CecilInspector.Core;

namespace CecilInspector.Cli;

public static class CommandLine
{
    public const string HelpText = """
        CecilInspector - .NETアセンブリのメタデータ検索ツール

        使用方法:
          cecil-inspector search <assembly-or-directory> <query> [options]
          cecil-inspector dump   <assembly-or-directory> [options]
          cecil-inspector --version

        オプションは位置引数の前後どちらにも置けます。'--' 以降はすべて位置引数として扱うので、
        '-' で始まる検索文言は '--' の後に指定してください。

        search options:
          --kind <list>          namespace,type,method,property,field,event,all
                                 (既定: all、カンマ区切り)
          --scope <value>        definitions | references | all (既定: definitions)
          --match <value>        contains | exact | regex (既定: contains)
          --format <value>       text | msbuild (既定: text)
                                 msbuildはエディターが解釈できる path(line,col): 形式で出力する
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

    /// <summary>Product version without the source-revision suffix, e.g. "0.2.0".</summary>
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
        if (args.Length == 0 || IsHelpToken(args[0]))
        {
            return ParseResult.Failure(null);
        }

        if (args.Length == 1 && IsVersionToken(args[0]))
        {
            return ParseResult.Version();
        }

        return args[0].ToLowerInvariant() switch
        {
            "search" => IsSubcommandHelp(args) ? ParseResult.Failure(null) : ParseSearch(args),
            "dump" => IsSubcommandHelp(args) ? ParseResult.Failure(null) : ParseDump(args),
            _ => ParseResult.Failure($"不明なコマンド '{args[0]}' です。"),
        };
    }

    private static ParseResult ParseSearch(string[] args)
    {
        var kinds = SearchKinds.All;
        var scope = SearchScope.Definitions;
        var matchMode = MatchMode.Contains;
        var format = ReportFormat.Text;
        var ignoreCase = true;
        var recursive = true;
        var symbolMode = SymbolMode.Auto;
        var maxResults = 1000;
        string? output = null;
        var referencePaths = new List<string>();

        var reader = new ArgumentReader(args, 1);
        while (reader.TryNextOption(out var option))
        {
            switch (option)
            {
                case "--kind" or "--kinds":
                    if (!reader.TryTakeValue(out var kindText) || !TryParseKinds(kindText, out kinds))
                    {
                        return ParseResult.Failure("--kindには namespace,type,method,property,field,event,all を指定してください。");
                    }

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
                    ignoreCase = false;
                    break;
                case "--no-recursive":
                    recursive = false;
                    break;
                case "--max-results":
                    if (!reader.TryTakeValue(out var maxText) ||
                        !int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out maxResults) ||
                        maxResults < 1)
                    {
                        return ParseResult.Failure("--max-resultsには1以上の整数を指定してください。");
                    }

                    break;
                case "--output" or "-o":
                    if (!reader.TryTakeValue(out output))
                    {
                        return ParseResult.Failure("--outputにはファイルパスが必要です。");
                    }

                    break;
                case "--reference-path":
                    if (!reader.TryTakeValue(out var referencePath))
                    {
                        return ParseResult.Failure("--reference-pathにはフォルダパスが必要です。");
                    }

                    referencePaths.Add(referencePath);
                    break;
                default:
                    return ParseResult.Failure(
                        $"不明なオプション '{option}' です。'-'で始まる検索文言は '--' の後に指定してください。");
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
            input, query, kinds, scope, matchMode, ignoreCase, recursive, symbolMode, maxResults, output, referencePaths,
            format));
    }

    private static ParseResult ParseDump(string[] args)
    {
        var recursive = true;
        var includeIl = false;
        var symbolMode = SymbolMode.Auto;
        string? output = null;
        var referencePaths = new List<string>();

        var reader = new ArgumentReader(args, 1);
        while (reader.TryNextOption(out var option))
        {
            switch (option)
            {
                case "--include-il":
                    includeIl = true;
                    break;
                case "--no-recursive":
                    recursive = false;
                    break;
                case "--symbols":
                    if (!reader.TryTakeEnum(out symbolMode))
                    {
                        return ParseResult.Failure("--symbolsには auto, off, required を指定してください。");
                    }

                    break;
                case "--output" or "-o":
                    if (!reader.TryTakeValue(out output))
                    {
                        return ParseResult.Failure("--outputにはファイルパスが必要です。");
                    }

                    break;
                case "--reference-path":
                    if (!reader.TryTakeValue(out var referencePath))
                    {
                        return ParseResult.Failure("--reference-pathにはフォルダパスが必要です。");
                    }

                    referencePaths.Add(referencePath);
                    break;
                default:
                    return ParseResult.Failure($"不明なオプション '{option}' です。");
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

    private static bool IsSubcommandHelp(string[] args) =>
        args.Length == 2 && IsHelpToken(args[1]);

    private static bool IsVersionToken(string value) =>
        value.Equals("version", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-v", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpToken(string value) =>
        value.Equals("help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-h", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks the arguments after the subcommand. Tokens starting with '-' are options, everything
    /// else is positional, and a bare "--" makes every following token positional.
    /// </summary>
    private sealed class ArgumentReader(string[] args, int start)
    {
        private int _index = start;
        private bool _afterSeparator;

        public List<string> Positionals { get; } = [];

        public bool TryNextOption(out string option)
        {
            while (_index < args.Length)
            {
                var token = args[_index++];
                if (!_afterSeparator && token == "--")
                {
                    _afterSeparator = true;
                    continue;
                }

                if (!_afterSeparator && IsOptionToken(token))
                {
                    option = token;
                    return true;
                }

                Positionals.Add(token);
            }

            option = string.Empty;
            return false;
        }

        public bool TryTakeValue(out string value)
        {
            if (_index >= args.Length || IsOptionToken(args[_index]))
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

        private static bool IsOptionToken(string value) =>
            value.StartsWith('-') && value != "--";
    }
}
