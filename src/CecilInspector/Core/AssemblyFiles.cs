namespace CecilInspector.Core;

public sealed record AssemblyDiscoveryResult(
    IReadOnlyList<string> Files,
    int FileCount,
    IReadOnlyList<string> SearchDirectories,
    IReadOnlyList<ScanError> Errors);

public static class AssemblyFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".netmodule",
    };

    public static IReadOnlyList<string> Discover(string inputPath, bool recursive) =>
        DiscoverDetailed(inputPath, recursive).Files.ToArray();

    public static AssemblyDiscoveryResult DiscoverDetailed(string inputPath, bool recursive)
    {
        var fullPath = Path.GetFullPath(inputPath);
        if (File.Exists(fullPath))
        {
            return new AssemblyDiscoveryResult(
                [fullPath],
                1,
                [Path.GetDirectoryName(fullPath)!],
                []);
        }

        if (!Directory.Exists(fullPath))
        {
            throw new ArgumentException($"入力パスが見つかりません: {inputPath}");
        }

        if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException($"入力フォルダにシンボリックリンク/再解析ポイントは指定できません: {inputPath}");
        }

        var errors = new List<ScanError>();
        var directoriesToVisit = new Stack<string>();
        var searchableDirectories = new List<string>();
        var files = new List<string>();
        directoriesToVisit.Push(fullPath);

        while (directoriesToVisit.Count > 0)
        {
            var directory = directoriesToVisit.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                errors.Add(new ScanError(directory, $"フォルダを走査できません: {ex.Message}"));
                continue;
            }

            var containsAssembly = false;
            Array.Sort(entries, ComparePaths);
            for (var index = entries.Length - 1; index >= 0; index--)
            {
                var entry = entries[index];
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    errors.Add(new ScanError(entry, $"属性を取得できません: {ex.Message}"));
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    errors.Add(new ScanError(entry, "シンボリックリンク/再解析ポイントは走査・解析しません。"));
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!recursive)
                    {
                        continue;
                    }

                    directoriesToVisit.Push(entry);
                }
                else if (Extensions.Contains(Path.GetExtension(entry)))
                {
                    files.Add(entry);
                    containsAssembly = true;
                }
            }

            if (containsAssembly)
            {
                searchableDirectories.Add(directory);
            }
        }

        searchableDirectories.Sort(ComparePaths);
        files.Sort(ComparePaths);
        if (files.Count == 0 && errors.Count == 0)
        {
            throw new ArgumentException($"対象アセンブリが見つかりません: {inputPath}");
        }

        return new AssemblyDiscoveryResult(
            files,
            files.Count,
            searchableDirectories,
            errors);
    }

    private static int ComparePaths(string left, string right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
    }
}
