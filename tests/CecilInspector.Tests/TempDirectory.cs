namespace CecilInspector.Tests;

/// <summary>
/// A unique directory under the system temp folder that is removed on dispose, so a test that
/// fails during setup cannot leak it. Declare it with <c>using var</c> before creating any file.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Returns a path inside the directory without creating anything.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Creates and returns a subdirectory.</summary>
    public string CreateSubdirectory(string name)
    {
        var path = File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
