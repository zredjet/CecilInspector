using System.Text;

namespace CecilInspector.Output;

internal static class OutputFile
{
    private static readonly HashSet<string> AssemblyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".netmodule",
    };

    public static AtomicReportFile? OpenAtomic(string? outputPath)
    {
        if (outputPath is null)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(outputPath);
        if (AssemblyExtensions.Contains(Path.GetExtension(fullPath)))
        {
            throw new ArgumentException("レポートの出力先にアセンブリ拡張子（.dll/.exe/.netmodule）は指定できません。");
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new ArgumentException($"出力先は既に存在します。安全のため上書きしません: {fullPath}");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partialPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.partial");
        var stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return new AtomicReportFile(fullPath, partialPath, new StreamWriter(stream, new UTF8Encoding(false)));
    }
}

internal sealed class AtomicReportFile : IDisposable
{
    private readonly string _finalPath;
    private readonly string _partialPath;
    private bool _committed;
    private bool _writerDisposed;

    internal AtomicReportFile(string finalPath, string partialPath, StreamWriter writer)
    {
        _finalPath = finalPath;
        _partialPath = partialPath;
        Writer = writer;
    }

    public TextWriter Writer { get; }

    public void Commit()
    {
        if (_committed)
        {
            return;
        }

        Writer.Flush();
        Writer.Dispose();
        _writerDisposed = true;

        // The exclusive handle is released above, so this check-then-move has an inherent
        // window; it defends against a swap that happened while the report was written, and
        // the README requires a trusted output folder for the rest. Move(overwrite: false)
        // itself refuses a final path that appeared in the meantime.
        var attributes = File.GetAttributes(_partialPath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("レポートの一時ファイルがシンボリックリンク/再解析ポイントへ差し替えられました。");
        }

        File.Move(_partialPath, _finalPath, false);
        _committed = true;
    }

    public void Dispose()
    {
        if (!_writerDisposed)
        {
            Writer.Dispose();
            _writerDisposed = true;
        }

        if (!_committed && File.Exists(_partialPath))
        {
            File.Delete(_partialPath);
        }
    }
}
