using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ModPackUpdater.Models;

namespace ModPackUpdater.Services;

public class PackService
{
    private readonly string _root;

    private static readonly string[] DefaultIgnoreNames =
    {
        "pack.json", // metadata file per version directory
        ".DS_Store",
        "Thumbs.db"
    };

    public PackService(string packsRoot)
    {
        _root = Path.GetFullPath(packsRoot);
        Directory.CreateDirectory(_root);
    }

    public IEnumerable<string> GetPackIds()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }

    public IReadOnlyList<string> GetVersions(string packId)
    {
        var packPath = Path.Combine(_root, packId);
        if (!Directory.Exists(packPath)) return Array.Empty<string>();
        var versions = Directory.EnumerateDirectories(packPath)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))!
            .Cast<string>()
            .ToList();
        // simple sort: try to sort by semantic-ish numeric segments, then lexicographic desc
        versions.Sort(CompareVersionDesc);
        return versions;
    }

    private static int CompareVersionDesc(string? a, string? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return 1;
        if (b == null) return -1;
        // try System.Version
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return -va.CompareTo(vb);
        // fallback: lexicographic desc
        return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
    }

    public string? GetLatestVersion(string packId)
    {
        return GetVersions(packId).FirstOrDefault();
    }

    public async Task<ModPackManifest?> TryGetManifestAsync(string packId, string version, CancellationToken ct)
    {
        var dir = GetPackVersionPath(packId, version);
        if (dir == null) return null;
        var meta = await ReadPackMetaAsync(dir, ct);
        var files = await EnumerateFilesWithHashesAsync(dir, ct);
        var manifest = new ModPackManifest(
            PackId: packId,
            Version: version,
            DisplayName: meta?.DisplayName ?? packId,
            McVersion: meta?.McVersion,
            Loader: meta?.LoaderName != null || meta?.LoaderVersion != null
                ? new LoaderInfo(meta?.LoaderName ?? string.Empty, meta?.LoaderVersion ?? string.Empty)
                : null,
            Files: files,
            CreatedAt: DateTimeOffset.UtcNow,
            Channel: meta?.Channel,
            Description: meta?.Description
        );
        return manifest;
    }

    private async Task<PackMeta?> ReadPackMetaAsync(string versionDir, CancellationToken ct)
    {
        var metaPath = Path.Combine(versionDir, "pack.json");
        if (!File.Exists(metaPath)) return null;
        await using var fs = File.OpenRead(metaPath);
        try
        {
            var meta = await JsonSerializer.DeserializeAsync(fs, AppJsonSerializerContext.Default.PackMeta, ct);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<FileEntry>> EnumerateFilesWithHashesAsync(string versionDir, CancellationToken ct)
    {
        var all = Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories)
            .Where(p => !ShouldIgnore(versionDir, p))
            .ToList();

        var results = new ConcurrentBag<FileEntry>();

        await Task.WhenAll(all.Select(async path =>
        {
            var rel = GetRelative(versionDir, path);
            var fi = new FileInfo(path);
            var hash = await ComputeSha256Async(path, ct);
            results.Add(new FileEntry(rel, hash, fi.Length));
        }));

        return results.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetRelative(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        // normalize to forward slashes for cross-platform consistency
        return rel.Replace('\\', '/');
    }

    private static bool ShouldIgnore(string root, string path)
    {
        var name = Path.GetFileName(path);
        if (DefaultIgnoreNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        // ignore dot-directories like .git inside pack version
        var rel = Path.GetRelativePath(root, path);
        if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => seg.StartsWith('.'))) return true;
        return false;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string? GetPackVersionPath(string packId, string version)
    {
        var dir = Path.Combine(_root, packId, version);
        dir = Path.GetFullPath(dir);
        // ensure within root
        if (!dir.StartsWith(_root, StringComparison.Ordinal)) return null;
        return Directory.Exists(dir) ? dir : null;
    }

    public async Task<DiffResponse?> DiffAsync(string packId, string version, DiffRequest request, CancellationToken ct)
    {
        var manifest = await TryGetManifestAsync(packId, version, ct);
        if (manifest == null) return null;

        var serverMap = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var clientMap = (request.Files ?? Array.Empty<ClientFile>()).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var ops = new List<Operation>();

        // additions or updates
        foreach (var kv in serverMap)
        {
            var path = kv.Key;
            var s = kv.Value;
            if (!clientMap.TryGetValue(path, out var c))
            {
                ops.Add(new Operation(path, FileOp.Add, s.Sha256, s.Size));
            }
            else if (!string.Equals(c.Sha256, s.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                ops.Add(new Operation(path, FileOp.Update, s.Sha256, s.Size));
            }
        }

        // deletions
        foreach (var kv in clientMap)
        {
            var path = kv.Key;
            if (!serverMap.ContainsKey(path))
            {
                ops.Add(new Operation(path, FileOp.Delete));
            }
        }

        return new DiffResponse(packId, version, ops);
    }

    public bool TryResolveFile(string packId, string version, string relativePath, out string? absolutePath)
    {
        absolutePath = null;
        var dir = GetPackVersionPath(packId, version);
        if (dir == null) return false;

        if (!IsSafeRelative(relativePath)) return false;
        var full = Path.GetFullPath(Path.Combine(dir, relativePath));
        if (!full.StartsWith(dir, StringComparison.Ordinal)) return false;
        if (!File.Exists(full)) return false;
        absolutePath = full;
        return true;
    }

    public async Task WriteBundleAsync(Stream output, string packId, string version, IEnumerable<string> paths, CancellationToken ct)
    {
        var dir = GetPackVersionPath(packId, version) ?? throw new DirectoryNotFoundException();

        await using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var rel in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!IsSafeRelative(rel)) continue;
                var full = Path.GetFullPath(Path.Combine(dir, rel));
                if (!full.StartsWith(dir, StringComparison.Ordinal) || !File.Exists(full)) continue;

                var entry = zip.CreateEntry(rel.Replace('\\', '/'), CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var fs = File.OpenRead(full);
                await fs.CopyToAsync(entryStream, ct);
            }
        }
        ms.Position = 0;
        await ms.CopyToAsync(output, ct);
    }

    private static bool IsSafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/")) return false;
        if (normalized.Contains("../")) return false;
        if (normalized.Contains("..\\")) return false;
        return true;
    }
}
