using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Microsoft.Maui.Cli.DevFlow.Evidence;

/// <summary>Everything a validated bundle yielded. Any field may be null if the entry was absent.</summary>
internal sealed class EvidenceReadResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? Path { get; init; }
    public EvidenceManifest? Manifest { get; init; }
    public EvidenceEnvironment? Environment { get; init; }
    public EvidenceTreeDocument? Tree { get; init; }
    public EvidenceProblemDocument? Problems { get; init; }
    public EvidenceLogDocument? Logs { get; init; }
    public EvidenceNetworkDocument? Network { get; init; }
    public string? Workflow { get; init; }
    public byte[]? Screenshot { get; init; }
    public List<string> Entries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public static EvidenceReadResult Fail(string error, string? path = null)
        => new() { Ok = false, Error = error, Path = path };
}

/// <summary>
/// Reads a <c>.mauitrace</c> bundle as HOSTILE input.
///
/// Every structural property is checked before any content is decompressed: entry names are
/// allow-listed and must be flat (no separators, traversal, or rooted paths), entries may not
/// repeat, and entry count / per-entry size / total size / compression ratio are all bounded.
/// Content is then parsed with a strict shape check. Nothing in a bundle is ever executed —
/// <c>workflow.md</c> is treated as inert text and the report is regenerated from scratch.
/// </summary>
internal static class EvidenceBundleReader
{
    public static EvidenceReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return EvidenceReadResult.Fail($"Bundle not found: {path}", path);
            if (info.Length == 0) return EvidenceReadResult.Fail("Bundle is empty.", path);
            if (info.Length > EvidenceFormat.MaxBundleFileBytes)
                return EvidenceReadResult.Fail("Bundle is larger than the supported maximum.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return EvidenceReadResult.Fail($"Could not open the bundle: {ex.Message}", path);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream, path);
        }
        catch (InvalidDataException)
        {
            return EvidenceReadResult.Fail("Bundle is not a readable evidence archive.", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EvidenceReadResult.Fail($"Could not read the bundle: {ex.Message}", path);
        }
    }

    public static EvidenceReadResult Read(Stream stream, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            return EvidenceReadResult.Fail("Bundle is not a readable evidence archive.", path);
        }

        using (archive)
        {
            try
            {
                return ReadArchive(archive, path);
            }
            catch (EvidenceReadException ex)
            {
                return EvidenceReadResult.Fail(ex.Message, path);
            }
            catch (InvalidDataException)
            {
                // Corrupt deflate stream or CRC mismatch mid-entry.
                return EvidenceReadResult.Fail("Bundle is not a readable evidence archive.", path);
            }
        }
    }

    private static EvidenceReadResult ReadArchive(ZipArchive archive, string? path)
    {
        {
            if (archive.Entries.Count == 0)
                return EvidenceReadResult.Fail("Bundle contains no entries.", path);
            if (archive.Entries.Count > EvidenceFormat.MaxBundleEntries)
                return EvidenceReadResult.Fail("Bundle contains too many entries.", path);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declaredTotal = 0;

            // Cheap pre-filter over the central directory. Everything here is attacker-declared, so
            // the real limits are enforced against the actual decompressed bytes in ReadBounded.
            foreach (var entry in archive.Entries)
            {
                var nameError = ValidateEntryName(entry.FullName);
                if (nameError is not null) return EvidenceReadResult.Fail(nameError, path);
                if (!seen.Add(entry.FullName))
                    return EvidenceReadResult.Fail($"Bundle contains duplicate entry '{entry.FullName}'.", path);

                if (entry.Length < 0 || entry.Length > EvidenceFormat.MaxEntryUncompressedBytes)
                    return EvidenceReadResult.Fail($"Entry '{entry.FullName}' is larger than the supported maximum.", path);

                declaredTotal += entry.Length;
                if (declaredTotal > EvidenceFormat.MaxTotalUncompressedBytes)
                    return EvidenceReadResult.Fail("Bundle expands beyond the supported maximum size.", path);
            }

            var budget = new ReadBudget();

            var manifestEntry = archive.GetEntry(EvidenceFormat.ManifestEntry);
            if (manifestEntry is null)
                return EvidenceReadResult.Fail("Bundle is missing manifest.json.", path);

            var manifestJson = DecodeUtf8(ReadBounded(manifestEntry, budget));
            if (manifestJson is null) return EvidenceReadResult.Fail("manifest.json is not valid UTF-8 text.", path);

            var manifestError = ValidateManifestShape(manifestJson);
            if (manifestError is not null) return EvidenceReadResult.Fail(manifestError, path);

            EvidenceManifest? manifest;
            try { manifest = EvidenceJson.Deserialize<EvidenceManifest>(manifestJson); }
            catch (JsonException) { return EvidenceReadResult.Fail("manifest.json could not be parsed.", path); }
            if (manifest is null) return EvidenceReadResult.Fail("manifest.json could not be parsed.", path);

            var warnings = new List<string>();
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName == EvidenceFormat.ManifestEntry)
                    continue;
                contents[entry.FullName] = ReadBounded(entry, budget);
            }

            var integrityError = ValidateManifestEntries(manifest, contents);
            if (integrityError is not null)
                return EvidenceReadResult.Fail(integrityError, path);

            var environment = ReadJsonEntry<EvidenceEnvironment>(contents, EvidenceFormat.EnvironmentEntry, warnings);
            var tree = ReadJsonEntry<EvidenceTreeDocument>(contents, EvidenceFormat.TreeEntry, warnings);
            var problems = ReadJsonEntry<EvidenceProblemDocument>(contents, EvidenceFormat.ProblemsEntry, warnings);
            var logs = ReadJsonEntry<EvidenceLogDocument>(contents, EvidenceFormat.LogsEntry, warnings);
            var network = ReadJsonEntry<EvidenceNetworkDocument>(contents, EvidenceFormat.NetworkEntry, warnings);

            string? workflow = null;
            if (contents.TryGetValue(EvidenceFormat.WorkflowEntry, out var workflowBytes))
            {
                workflow = DecodeUtf8(workflowBytes);
                if (workflow is null) warnings.Add("workflow.md was ignored: it is not valid UTF-8 text.");
            }

            byte[]? screenshot = null;
            if (contents.TryGetValue(EvidenceFormat.ScreenshotEntry, out var screenshotBytes))
            {
                if (!IsPng(screenshotBytes)) warnings.Add("screenshot.png was ignored: it is not a PNG image.");
                else screenshot = screenshotBytes;
            }

            return new EvidenceReadResult
            {
                Ok = true,
                Path = path,
                Manifest = manifest,
                Environment = environment,
                Tree = tree,
                Problems = problems,
                Logs = logs,
                Network = network,
                Workflow = workflow,
                Screenshot = screenshot,
                Entries = [.. archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal)],
                Warnings = warnings,
            };
        }
    }

    /// <summary>
    /// Entry names must be one of the allow-listed flat names. This rejects traversal
    /// (<c>../</c>), rooted paths, drive letters, nested directories, and unknown files outright.
    /// </summary>
    internal static string? ValidateEntryName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "Bundle contains an entry with no name.";
        if (name.Length > 128)
            return "Bundle contains an entry name that is too long.";
        if (name.Contains('\0') || name.Any(char.IsControl))
            return "Bundle contains an entry name with invalid characters.";
        if (name.Contains('/') || name.Contains('\\'))
            return $"Bundle contains a nested or traversing entry '{name}'.";
        if (name.Contains("..", StringComparison.Ordinal))
            return $"Bundle contains a traversing entry '{name}'.";
        if (name.Length >= 2 && name[1] == ':')
            return $"Bundle contains a rooted entry '{name}'.";
        if (!EvidenceFormat.AllowedEntries.Contains(name, StringComparer.Ordinal))
            return $"Bundle contains an unexpected entry '{name}'.";
        return null;
    }

    /// <summary>Structural check of manifest.json before it is bound to the typed model.</summary>
    internal static string? ValidateManifestShape(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = EvidenceJson.MaxJsonDepth }); }
        catch (JsonException) { return "manifest.json is not valid JSON."; }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "manifest.json must be a JSON object.";

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
                !string.Equals(schema.GetString(), EvidenceFormat.SchemaId, StringComparison.Ordinal))
            {
                return "manifest.json is not a MAUI DevFlow evidence manifest.";
            }

            if (!root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var formatVersion))
            {
                return "manifest.json is missing a numeric formatVersion.";
            }
            if (formatVersion != EvidenceFormat.Version)
                return $"Unsupported evidence format version {formatVersion} (this tool reads version {EvidenceFormat.Version}).";

            if (!root.TryGetProperty("capturedUtc", out var captured) || captured.ValueKind != JsonValueKind.String)
                return "manifest.json is missing capturedUtc.";

            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind != JsonValueKind.Array)
                return "manifest.json entries must be an array.";
        }

        return null;
    }

    private static string? ValidateManifestEntries(
        EvidenceManifest manifest,
        IReadOnlyDictionary<string, byte[]> contents)
    {
        if (manifest.Entries is null)
            return "manifest.json entries are missing.";

        var manifestEntries = new Dictionary<string, EvidenceEntryInfo>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)
                || !EvidenceFormat.AllowedEntries.Contains(entry.Name, StringComparer.Ordinal)
                || entry.Name == EvidenceFormat.ManifestEntry)
            {
                return "manifest.json describes an invalid evidence entry.";
            }
            if (!manifestEntries.TryAdd(entry.Name, entry))
                return $"manifest.json describes duplicate entry '{entry.Name}'.";
        }

        if (manifestEntries.Count != contents.Count
            || contents.Keys.Any(name => !manifestEntries.ContainsKey(name)))
        {
            return "Bundle contents do not match the entries declared by manifest.json.";
        }

        foreach (var pair in contents)
        {
            var declared = manifestEntries[pair.Key];
            if (declared.Bytes != pair.Value.LongLength)
                return $"Entry '{pair.Key}' size does not match manifest.json.";
            if (string.IsNullOrWhiteSpace(declared.Sha256))
                return $"Entry '{pair.Key}' is missing its integrity hash.";

            var actualHash = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant();
            if (!string.Equals(actualHash, declared.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"Entry '{pair.Key}' integrity hash does not match manifest.json.";
        }

        return null;
    }

    private static T? ReadJsonEntry<T>(
        IReadOnlyDictionary<string, byte[]> contents,
        string name,
        List<string> warnings)
        where T : class
    {
        if (!contents.TryGetValue(name, out var bytes))
            return null;

        var json = DecodeUtf8(bytes);
        if (json is null)
        {
            warnings.Add($"{name} was ignored: it is not valid UTF-8 text.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = EvidenceJson.MaxJsonDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{name} was ignored: unexpected JSON shape.");
                return null;
            }
            return EvidenceJson.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            warnings.Add($"{name} was ignored: it could not be parsed.");
            return null;
        }
    }

    private sealed class ReadBudget
    {
        public long TotalBytes;
    }

    /// <summary>Signals a structural violation that must fail the whole read.</summary>
    private sealed class EvidenceReadException(string message) : Exception(message);

    /// <summary>
    /// Copies an entry while enforcing the real limits against the ACTUAL decompressed bytes: the
    /// declared entry length in the central directory is attacker-controlled, so a bundle that lies
    /// about its sizes cannot use the cheap pre-filter to smuggle a decompression bomb past the
    /// per-entry cap, the cumulative cap, or the ratio guard.
    /// </summary>
    private static byte[] ReadBounded(ZipArchiveEntry entry, ReadBudget budget)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();

        var chunk = new byte[81_920];
        long actual = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            actual += read;
            budget.TotalBytes += read;
            if (actual > EvidenceFormat.MaxEntryUncompressedBytes)
                throw new EvidenceReadException($"Entry '{entry.FullName}' is larger than the supported maximum.");
            if (budget.TotalBytes > EvidenceFormat.MaxTotalUncompressedBytes)
                throw new EvidenceReadException("Bundle expands beyond the supported maximum size.");
            buffer.Write(chunk, 0, read);
        }

        if (entry.CompressedLength >= EvidenceFormat.RatioCheckMinCompressedBytes &&
            actual / Math.Max(entry.CompressedLength, 1) > EvidenceFormat.MaxCompressionRatio)
        {
            throw new EvidenceReadException($"Entry '{entry.FullName}' has a suspicious compression ratio.");
        }

        return buffer.ToArray();
    }

    private static string? DecodeUtf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
}
