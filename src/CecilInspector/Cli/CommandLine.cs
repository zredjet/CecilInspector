using System.Globalization;
using System.Text.RegularExpressions;

namespace CecilInspector.Cli;

public static class CommandLine
{
    public const string HelpText = """
        CecilInspector - .NETアセンブリのメタデータ検索ツール

        使用方法:
          cecil-inspector search <assembly-or-directory> <query> [options]
          cecil-inspector dump   <assembly-or-directory> [options]

        search options:
          --kind <list>          namespace,type,method,property,field,event,all
                                 (既定: all、カンマ区切り)
          --scope <value>        definitions | references | all (既定: definitions)
          --match <value>        contains | exact | regex (既定: contains)
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
          cecil-inspector dump app.dll --include-il --output metadata.txt
        """;

    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            return ParseResult.Failure(null);
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
        if (args.Length < 3)
        {
            return ParseResult.Failure("searchには入力パスと検索文言が必要です。");
        }

        var input = args[1];
        var query = args[2];
        var kinds = SearchKinds.All;
        var scope = SearchScope.Definitions;
        var matchMode = MatchMode.Contains;
        var ignoreCase = true;
        var recursive = true;
        var symbolMode = SymbolMode.Auto;
        var maxResults = 1000;
        string? output = null;
        var referencePaths = new List<string>();

        for (var index = 3; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--kind" or "--kinds":
                    if (!TryTakeValue(args, ref index, out var kindText) || !TryParseKinds(kindText, out kinds))
                    {
                        return ParseResult.Failure("--kindには namespace,type,method,property,field,event,all を指定してください。");
                    }

                    break;
                case "--scope":
                    if (!TryTakeEnum(args, ref index, out scope))
                    {
                        return ParseResult.Failure("--scopeには definitions, references, all を指定してください。");
                    }

                    break;
                case "--match":
                    if (!TryTakeEnum(args, ref index, out matchMode))
                    {
                        return ParseResult.Failure("--matchには contains, exact, regex を指定してください。");
                    }

                    break;
                case "--symbols":
                    if (!TryTakeEnum(args, ref index, out symbolMode))
                    {
                        return ParseResult.Failure("--symbolsには auto, off, required を指定してください。");
                    }

                    break;
                case "--case-sensitive":
                    ignoreCase = false;
                    break;
                case "--no-recursive":
                    recursive = false;
                    break;
                case "--max-results":
                    if (!TryTakeValue(args, ref index, out var maxText) ||
                        !int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out maxResults) ||
                        maxResults < 1)
                    {
                        return ParseResult.Failure("--max-resultsには1以上の整数を指定してください。");
                    }

                    break;
                case "--output" or "-o":
                    if (!TryTakeValue(args, ref index, out output))
                    {
                        return ParseResult.Failure("--outputにはファイルパスが必要です。");
                    }

                    break;
                case "--reference-path":
                    if (!TryTakeValue(args, ref index, out var referencePath))
                    {
                        return ParseResult.Failure("--reference-pathにはフォルダパスが必要です。");
                    }

                    referencePaths.Add(referencePath);
                    break;
                default:
                    return ParseResult.Failure($"不明なオプション '{args[index]}' です。");
            }
        }

        if (string.IsNullOrEmpty(query))
        {
            return ParseResult.Failure("検索文言を空にはできません。");
        }

        if (matchMode == MatchMode.Regex)
        {
            var regexOptions = RegexOptions.CultureInvariant;
            if (ignoreCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            try
            {
                _ = new Regex(query, regexOptions, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                return ParseResult.Failure($"正規表現が不正です: {ex.Message}");
            }
        }

        return ParseResult.Success(new SearchOptions(
            input, query, kinds, scope, matchMode, ignoreCase, recursive, symbolMode, maxResults, output, referencePaths));
    }

    private static ParseResult ParseDump(string[] args)
    {
        if (args.Length < 2)
        {
            return ParseResult.Failure("dumpには入力パスが必要です。");
        }

        var input = args[1];
        var recursive = true;
        var includeIl = false;
        var symbolMode = SymbolMode.Auto;
        string? output = null;
        var referencePaths = new List<string>();

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--include-il":
                    includeIl = true;
                    break;
                case "--no-recursive":
                    recursive = false;
                    break;
                case "--symbols":
                    if (!TryTakeEnum(args, ref index, out symbolMode))
                    {
                        return ParseResult.Failure("--symbolsには auto, off, required を指定してください。");
                    }

                    break;
                case "--output" or "-o":
                    if (!TryTakeValue(args, ref index, out output))
                    {
                        return ParseResult.Failure("--outputにはファイルパスが必要です。");
                    }

                    break;
                case "--reference-path":
                    if (!TryTakeValue(args, ref index, out var referencePath))
                    {
                        return ParseResult.Failure("--reference-pathにはフォルダパスが必要です。");
                    }

                    referencePaths.Add(referencePath);
                    break;
                default:
                    return ParseResult.Failure($"不明なオプション '{args[index]}' です。");
            }
        }

        return ParseResult.Success(new DumpOptions(input, recursive, includeIl, symbolMode, output, referencePaths));
    }

    private static bool TryParseKinds(string text, out SearchKinds kinds)
    {
        kinds = SearchKinds.None;
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<SearchKinds>(item, true, out var parsed) ||
                !Enum.IsDefined(parsed) ||
                parsed == SearchKinds.None)
            {
                return false;
            }

            kinds |= parsed;
        }

        return kinds != SearchKinds.None;
    }

    private static bool TryTakeEnum<T>(string[] args, ref int index, out T value) where T : struct, Enum
    {
        value = default;
        return TryTakeValue(args, ref index, out var text) &&
               Enum.TryParse(text, true, out value) &&
               Enum.IsDefined(value);
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length || IsOptionToken(args[index + 1]))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool IsSubcommandHelp(string[] args) =>
        args.Length == 2 && args[1] is "help" or "--help" or "-h";

    private static bool IsOptionToken(string value) =>
        value.StartsWith("-", StringComparison.Ordinal) || value == "help";
}
