using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using ModPackUpdater.Models;

namespace ModPackUpdater.Services;

public class PackService
{
    private readonly string _root;

    private static readonly string[] DefaultIgnoreNames =
    {
        "pack.json", // metadata file per pack directory
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
        var dir = GetPackPath(packId);
        if (dir is null) return Array.Empty<string>();
        // Single-version model: always 'latest'
        return new[] { "latest" };
    }

    public string? GetLatestVersion(string packId)
    {
        var dir = GetPackPath(packId);
        return dir is not null ? "latest" : null;
    }

    public async Task<ModPackManifest?> TryGetManifestAsync(string packId, string version, CancellationToken ct)
    {
        var dir = GetPackPath(packId);
        if (dir == null) return null;
        var meta = await ReadPackMetaAsync(dir, ct);
        var files = await EnumerateFilesWithHashesAsync(dir, ct);
        var manifest = new ModPackManifest(
            PackId: packId,
            Version: "latest",
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

    private async Task<PackMeta?> ReadPackMetaAsync(string dir, CancellationToken ct)
    {
        var metaPath = Path.Combine(dir, "pack.json");
        if (!File.Exists(metaPath)) return null;
        await using var fs = File.OpenRead(metaPath);
        try
        {
            var meta = await JsonSerializer.DeserializeAsync<PackMeta>(fs, cancellationToken: ct);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<FileEntry>> EnumerateFilesWithHashesAsync(string dir, CancellationToken ct)
    {
        // Avoid traversing into symlinked directories and skip symlinked files
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        var all = Directory.EnumerateFiles(dir, "*", opts)
            .Where(p => !ShouldIgnore(dir, p))
            .ToList();

        var results = new ConcurrentBag<FileEntry>();

        await Task.WhenAll(all.Select(async path =>
        {
            try
            {
                if (IsSymlink(path)) return; // skip symlink files
                if (HasSymlinkAncestor(dir, path)) return; // skip files under symlinked dirs

                var rel = GetRelative(dir, path);
                var fi = new FileInfo(path);
                var hash = await ComputeSha256Async(path, ct);
                results.Add(new FileEntry(rel, hash, fi.Length));
            }
            catch
            {
                // Skip files we can't read/hash
            }
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
        // ignore dot-directories like .git inside pack
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

    private string? GetPackPath(string packId)
    {
        var dir = Path.Combine(_root, packId);
        dir = Path.GetFullPath(dir);
        if (!dir.StartsWith(_root, StringComparison.Ordinal)) return null;
        return Directory.Exists(dir) ? dir : null;
    }

    public bool TryResolveFile(string packId, string version, string relativePath, out string? absolutePath)
    {
        absolutePath = null;
        var dir = GetPackPath(packId);
        if (dir == null) return false;

        if (!IsSafeRelative(relativePath)) return false;
        var full = Path.GetFullPath(Path.Combine(dir, relativePath));
        if (!full.StartsWith(dir, StringComparison.Ordinal) || !File.Exists(full)) return false;

        // Reject symlinked files or files under symlinked directories
        if (IsSymlink(full)) return false;
        if (HasSymlinkAncestor(dir, full)) return false;

        absolutePath = full;
        return true;
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

    private static bool IsSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSymlinkAncestor(string baseDir, string path)
    {
        try
        {
            var baseFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
            while (current != null)
            {
                var curFull = current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!curFull.StartsWith(baseFull, StringComparison.Ordinal)) break;
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                if (string.Equals(curFull, baseFull, StringComparison.Ordinal)) break;
                current = current.Parent;
            }
        }
        catch
        {
            // if in doubt, do not treat as having symlink ancestor
        }
        return false;
    }
}
