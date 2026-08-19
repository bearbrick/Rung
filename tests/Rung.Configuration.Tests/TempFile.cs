namespace Rung.Configuration.Tests;

/// <summary>用完即删的临时文件。</summary>
internal sealed class TempFile(string extension) : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rung-test-{Guid.NewGuid():N}{extension}");

    public void Dispose()
    {
        foreach (var path in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
