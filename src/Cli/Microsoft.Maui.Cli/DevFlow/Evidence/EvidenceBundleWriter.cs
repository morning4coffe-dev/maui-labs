using System.IO.Compression;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

internal sealed record EvidenceWriteResult(bool Ok, string? Path, long Bytes, string? Error);

/// <summary>
/// Writes a bundle to disk atomically: build the ZIP in a temporary file beside the destination,
/// validate it by reading it back through the hostile-input reader, and only then move it into
/// place. A partially written or structurally invalid bundle never appears at the destination.
/// </summary>
internal static class EvidenceBundleWriter
{
    public static byte[] ToBytes(EvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        using var buffer = new MemoryStream();
        WriteArchive(bundle, buffer);
        var bytes = buffer.ToArray();
        using var validationStream = new MemoryStream(bytes, writable: false);
        var validation = EvidenceBundleReader.Read(validationStream);
        if (!validation.Ok)
            throw new InvalidDataException($"The generated bundle failed validation: {validation.Error}");
        return bytes;
    }

    public static EvidenceWriteResult Write(EvidenceBundle bundle, string destinationPath, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (string.IsNullOrEmpty(directory))
            return new EvidenceWriteResult(false, null, 0, "Output path has no parent directory.");

        if (!overwrite && File.Exists(destinationPath))
        {
            return new EvidenceWriteResult(false, null, 0,
                $"File already exists: {destinationPath} (use --overwrite to replace)");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EvidenceWriteResult(false, null, 0, $"Could not create the output directory: {ex.Message}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteArchive(bundle, file);
                file.Flush(flushToDisk: true);
            }

            // Validate before publishing: the same reader that guards imported bundles.
            var validation = EvidenceBundleReader.Read(temporaryPath);
            if (!validation.Ok)
            {
                TryDelete(temporaryPath);
                return new EvidenceWriteResult(false, null, 0,
                    $"The generated bundle failed validation: {validation.Error}");
            }

            var bytes = new FileInfo(temporaryPath).Length;

            if (overwrite)
                File.Move(temporaryPath, destinationPath, overwrite: true);
            else
                File.Move(temporaryPath, destinationPath); // fails if another writer won the race

            return new EvidenceWriteResult(true, Path.GetFullPath(destinationPath), bytes, null);
        }
        catch (IOException) when (!overwrite && File.Exists(destinationPath))
        {
            TryDelete(temporaryPath);
            return new EvidenceWriteResult(false, null, 0,
                $"File already exists: {destinationPath} (use --overwrite to replace)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporaryPath);
            return new EvidenceWriteResult(false, null, 0, $"Could not write the bundle: {ex.Message}");
        }
    }

    private static void WriteArchive(EvidenceBundle bundle, Stream destination)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, EvidenceFormat.ManifestEntry, bundle.ManifestBytes);
        foreach (var entry in bundle.Entries)
            WriteEntry(archive, entry.Name, entry.Content);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        if (!EvidenceFormat.AllowedEntries.Contains(name, StringComparer.Ordinal))
            throw new InvalidOperationException($"Refusing to write unknown evidence entry '{name}'.");

        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* the temp file is uniquely named; a leftover is harmless */ }
    }
}
