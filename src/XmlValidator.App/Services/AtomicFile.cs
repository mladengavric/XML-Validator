namespace XmlValidator.App.Services;

/// <summary>Upis preko privremenog fajla u istom folderu, da prekinut upis ne ostavi polovičan fajl.</summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken = default) =>
        Task.Run(() => WriteAllBytes(path, content), cancellationToken);
}
