using System.Text;
using CecilInspector.Core;

namespace CecilInspector.Output;

internal static class OutputFile
{
    public static AtomicReportFile? OpenAtomic(string? outputPath)
    {
        if (outputPath is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("--outputにはファイルパスが必要です。");
        }

        var fullPath = Path.GetFullPath(outputPath);
        if (AssemblyFiles.IsAssemblyFileName(fullPath))
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
        return new AtomicReportFile(fullPath, partialPath, stream);
    }
}

/// <summary>
/// A report written to a hidden ".name.guid.partial" sibling and renamed into place by
/// <see cref="Commit"/>. Disposing without a commit deletes the partial file, which is what the
/// interrupt handling in Program relies on to leave nothing behind after Ctrl-C.
/// </summary>
internal sealed class AtomicReportFile : IDisposable
{
    private readonly string _finalPath;
    private readonly string _partialPath;
    private readonly FileStream _stream;
    private bool _committed;
    private bool _writerDisposed;

    internal AtomicReportFile(string finalPath, string partialPath, FileStream stream)
    {
        _finalPath = finalPath;
        _partialPath = partialPath;
        _stream = stream;
        Writer = new StreamWriter(stream, new UTF8Encoding(false));
    }

    public TextWriter Writer { get; }

    public void Commit()
    {
        if (_committed)
        {
            return;
        }

        // Whatever happens below, Dispose() must not flush the same buffered text a second time:
        // a failed flush (disk full, quota) would otherwise throw again from inside the disposal
        // and replace the exception that names the real problem.
        _writerDisposed = true;
        try
        {
            Writer.Flush();
            Writer.Dispose();
        }
        catch
        {
            CloseStreamQuietly();
            throw;
        }

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
            _writerDisposed = true;
            try
            {
                Writer.Dispose();
            }
            catch (IOException)
            {
                // The report is being discarded; a failure to flush it must not mask the
                // exception that is already propagating.
                CloseStreamQuietly();
            }
        }

        if (!_committed && File.Exists(_partialPath))
        {
            File.Delete(_partialPath);
        }
    }

    private void CloseStreamQuietly()
    {
        try
        {
            _stream.Dispose();
        }
        catch (IOException)
        {
            // The handle is closed even when the final flush fails; nothing else to do.
        }
    }
}
