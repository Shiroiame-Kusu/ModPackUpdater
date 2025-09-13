using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ModPackUpdater.Models;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ModPackUpdater.Services;

public class PackService
{
    private readonly string _root;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PackService> _logger;

    // prevent stampedes: one builder per pack key
    private readonly ConcurrentDictionary<string, Lazy<Task<ModPackManifest?>>> _builders = new();
    // file watchers per pack dir to invalidate cache on changes
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();

    private readonly int _hashConcurrency;
    private readonly int _modExtractConcurrency;

    private static readonly MemoryCacheEntryOptions CacheEntryOptions = new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        SlidingExpiration = TimeSpan.FromMinutes(2),
        Size = 1
    };

    private static readonly string[] DefaultIgnoreNames =
    {
        "pack.json", // metadata file per pack directory
        ".DS_Store",
        "Thumbs.db"
    };

    public PackService(string packsRoot, IMemoryCache cache, ILogger<PackService> logger, int? hashConcurrency = null, int? modExtractConcurrency = null)
    {
        _root = Path.GetFullPath(packsRoot);
        Directory.CreateDirectory(_root);
        _cache = cache;
        _logger = logger;
        _hashConcurrency = Clamp(hashConcurrency ?? Math.Max(2, Environment.ProcessorCount * 2), 1, 64);
        _modExtractConcurrency = Clamp(modExtractConcurrency ?? Math.Max(2, Environment.ProcessorCount / 2), 1, 32);
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

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

        // LATEST only at the moment, but include version in key to be future-proof
        var cacheKey = GetManifestCacheKey(packId, version);
        if (_cache.TryGetValue<ModPackManifest>(cacheKey, out var cached))
        {
            return cached;
        }

        EnsureWatcher(packId, dir);

        // dedupe concurrent builders
        var builder = _builders.GetOrAdd(cacheKey, _ => new Lazy<Task<ModPackManifest?>>(async () =>
        {
            try
            {
                var meta = await ReadPackMetaAsync(dir, ct);
                var files = await EnumerateFilesWithHashesAsync(dir, ct);
                var mods = await ExtractModsAsync(dir, files, ct);
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
                    Description: meta?.Description,
                    Mods: mods
                );

                // store in cache
                _cache.Set(cacheKey, manifest, CacheEntryOptions);
                return manifest;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build manifest for pack {PackId}", packId);
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var result = await builder.Value;
            return result;
        }
        finally
        {
            // remove the builder so next miss creates a fresh one
            _builders.TryRemove(cacheKey, out _);
        }
    }

    private void EnsureWatcher(string packId, string dir)
    {
        if (_watchers.ContainsKey(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
            };

            FileSystemEventHandler invalidate = (_, __) => Invalidate(packId);
            RenamedEventHandler invalidateRenamed = (_, __) => Invalidate(packId);
            ErrorEventHandler onError = (_, e) =>
            {
                _logger.LogDebug(e.GetException(), "Watcher error for pack {PackId}, will force invalidate", packId);
                Invalidate(packId);
            };

            w.Changed += invalidate;
            w.Created += invalidate;
            w.Deleted += invalidate;
            w.Renamed += invalidateRenamed;
            w.Error += onError;

            _watchers.TryAdd(dir, w);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create watcher for {Dir}", dir);
        }
    }

    private void Invalidate(string packId)
    {
        try
        {
            var key = GetManifestCacheKey(packId, "latest");
            _cache.Remove(key);
            _builders.TryRemove(key, out _);
            _logger.LogDebug("Invalidated cache for pack {PackId}", packId);
        }
        catch { /* best-effort */ }
    }

    private static string GetManifestCacheKey(string packId, string version) => $"manifest:{packId}:{version}";

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
        var sem = new SemaphoreSlim(Math.Max(1, _hashConcurrency));

        await Task.WhenAll(all.Select(async path =>
        {
            await sem.WaitAsync(ct);
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
            finally
            {
                try { sem.Release(); } catch { }
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
        var fsOpts = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        await using var stream = new FileStream(path, fsOpts);
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

    // --- Mod metadata extraction ---
    private async Task<IReadOnlyList<ModInfo>> ExtractModsAsync(string dir, IReadOnlyList<FileEntry> files, CancellationToken ct)
    {
        var modJars = files
            .Where(f => f.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .Select(f => new { Rel = f.Path, Abs = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar)) })
            .Where(p => File.Exists(p.Abs))
            .ToList();

        var results = new ConcurrentBag<ModInfo>();
        var sem = new SemaphoreSlim(Math.Max(1, _modExtractConcurrency));
        var tasks = modJars.Select(async jar =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var info = await Task.Run(() => ExtractModFromJar(jar.Abs, jar.Rel), ct);
                if (info != null) results.Add(info);
            }
            catch
            {
                // ignore per-jar errors
            }
            finally
            {
                try { sem.Release(); } catch { }
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private ModInfo? ExtractModFromJar(string jarPath, string relPath)
    {
        try
        {
            using var fs = File.OpenRead(jarPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            // Try Fabric
            var fabricEntry = zip.GetEntry("fabric.mod.json");
            if (fabricEntry != null)
            {
                using var sr = new StreamReader(fabricEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var json = sr.ReadToEnd();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string? id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                    string? ver = root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String ? vEl.GetString() : null;
                    string? name = root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
                    return new ModInfo(relPath, id, ver, name, Loader: "fabric");
                }
                catch { /* ignore parse errors */ }
            }

            // Try Quilt
            var quiltEntry = zip.GetEntry("quilt.mod.json");
            if (quiltEntry != null)
            {
                using var sr = new StreamReader(quiltEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var json = sr.ReadToEnd();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string? id = null, ver = null, name = null;
                    if (root.TryGetProperty("quilt_loader", out var ql) && ql.ValueKind == JsonValueKind.Object)
                    {
                        if (ql.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString();
                        if (ql.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String) ver = vEl.GetString();
                    }
                    if (root.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
                    {
                        if (md.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String) name = nEl.GetString();
                    }
                    return new ModInfo(relPath, id, ver, name, Loader: "quilt");
                }
                catch { }
            }

            // Try Forge/NeoForge mods.toml
            var tomlEntry = zip.GetEntry("META-INF/mods.toml") ?? zip.GetEntry("META-INF/neoforge.mods.toml");
            if (tomlEntry != null)
            {
                using var sr = new StreamReader(tomlEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var toml = sr.ReadToEnd();
                var (id, ver, name) = ParseModsToml(toml);
                if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(ver) || !string.IsNullOrWhiteSpace(name))
                {
                    var loaderKind = tomlEntry.FullName.EndsWith("neoforge.mods.toml", StringComparison.OrdinalIgnoreCase) ? "neoforge" : "forge";
                    return new ModInfo(relPath, id, ver, name, Loader: loaderKind);
                }
            }

            // Try Maven pom.properties (common in Java artifacts)
            if (TryReadPomProperties(zip, out var pomId, out var pomVersion, out var pomName))
            {
                if (!string.IsNullOrWhiteSpace(pomVersion) || !string.IsNullOrWhiteSpace(pomId) || !string.IsNullOrWhiteSpace(pomName))
                {
                    return new ModInfo(relPath, string.IsNullOrWhiteSpace(pomId) ? null : pomId, string.IsNullOrWhiteSpace(pomVersion) ? null : pomVersion, string.IsNullOrWhiteSpace(pomName) ? null : pomName);
                }
            }

            // Legacy Forge: mcmod.info (1.12.x and earlier)
            var mcmodEntry = zip.GetEntry("mcmod.info");
            if (mcmodEntry != null)
            {
                using var sr = new StreamReader(mcmodEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var json = sr.ReadToEnd();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var el = doc.RootElement.EnumerateArray().FirstOrDefault();
                        if (el.ValueKind == JsonValueKind.Object)
                        {
                            string? id = el.TryGetProperty("modid", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                            string? ver = el.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String ? vEl.GetString() : null;
                            string? name = el.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
                            if (id != null || ver != null || name != null)
                                return new ModInfo(relPath, id, ver, name, Loader: "forge");
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var el = doc.RootElement;
                        string? id = el.TryGetProperty("modid", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                        string? ver = el.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String ? vEl.GetString() : null;
                        string? name = el.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
                        if (id != null || ver != null || name != null)
                            return new ModInfo(relPath, id, ver, name, Loader: "forge");
                    }
                }
                catch { }
            }

            // Fallback: MANIFEST.MF
            var mfEntry = zip.GetEntry("META-INF/MANIFEST.MF");
            string? mfVersion = null;
            if (mfEntry != null)
            {
                using var sr = new StreamReader(mfEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (!sr.EndOfStream)
                {
                    var line = sr.ReadLine();
                    if (line == null) break;
                    if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                    {
                        mfVersion = line.Substring(line.IndexOf(':') + 1).Trim();
                        break;
                    }
                }
            }

            // Improved filename heuristic: prefer last non-MC version-like token
            var (fileId, fileVer) = GuessFromFileNameAdvanced(Path.GetFileName(relPath));
            var finalVersion = mfVersion ?? fileVer;
            if (fileId != null || finalVersion != null)
            {
                return new ModInfo(relPath, fileId, finalVersion, Name: fileId);
            }
        }
        catch
        {
            // ignore jar errors
        }
        return null;
    }

    private static bool TryReadPomProperties(ZipArchive zip, out string? artifactId, out string? version, out string? name)
    {
        artifactId = null; version = null; name = null;
        try
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("META-INF/maven/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith("/pom.properties", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            using var sr = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line.Substring(0, idx).Trim();
                var val = line[(idx + 1)..].Trim();
                if (key.Equals("version", StringComparison.OrdinalIgnoreCase)) version = string.IsNullOrWhiteSpace(val) ? version : val;
                else if (key.Equals("artifactId", StringComparison.OrdinalIgnoreCase)) artifactId = string.IsNullOrWhiteSpace(val) ? artifactId : val;
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = string.IsNullOrWhiteSpace(val) ? name : val;
            }
            return artifactId != null || version != null || name != null;
        }
        catch { return false; }
    }

    private static (string? id, string? version) GuessFromFileNameAdvanced(string fileName)
    {
        if (fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^4];
        // Split by common delimiters
        var tokens = Regex.Split(fileName, "[-+_]").Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (tokens.Count == 0) return (fileName, null);
        // Known non-version tokens to skip
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fabric", "forge", "neoforge", "quilt", "client", "server" };
        // Scan from the end for a suitable version token that is not a MC version
        string? chosenVersion = null;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var t = tokens[i];
            if (skip.Contains(t)) continue;
            if (LooksLikeMinecraftVersion(t)) continue;
            if (LooksLikeVersion(t)) { chosenVersion = t; break; }
        }
        // id guess: prefix before chosenVersion (or whole name if none)
        string id = chosenVersion != null ? string.Join('-', tokens.TakeWhile(t => t != chosenVersion)) : fileName;
        if (string.IsNullOrWhiteSpace(id)) id = fileName;
        return (id, chosenVersion);
    }

    private static bool LooksLikeVersion(string token)
    {
        // Simple: contains at least one digit and a dot or digit-run, allow v prefix
        if (token.StartsWith('v')) token = token[1..];
        return token.Any(char.IsDigit) && (token.Contains('.') || token.Any(char.IsDigit));
    }

    private static bool LooksLikeMinecraftVersion(string token)
    {
        // Common patterns: 1.20, 1.20.1, 1.12.2, possibly with mc prefix
        if (token.StartsWith("mc", StringComparison.OrdinalIgnoreCase)) token = token[2..];
        var mc = Regex.IsMatch(token, "^1\\.\\d{1,2}(?:\\.\\d{1,2})?(?:-pre\\d+|-rc\\d+)?$", RegexOptions.CultureInvariant);
        return mc;
    }

    private static (string? id, string? version, string? name) ParseModsToml(string toml)
    {
        string? id = null, ver = null, name = null;
        using var reader = new StringReader(toml);
        bool inMods = false;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
            if (trimmed.StartsWith("[[mods]]", StringComparison.OrdinalIgnoreCase)) { inMods = true; continue; }
            if (!inMods) continue;
            if (trimmed.StartsWith("[")) break; // next table

            static string? ReadValue(string s)
            {
                var idx = s.IndexOf('='); if (idx < 0) return null;
                var raw = s[(idx + 1)..];
                // Parse until an unquoted # (inline comment) is found
                var span = raw.AsSpan();
                bool inSingle = false, inDouble = false, escape = false;
                int end = span.Length;
                for (int i = 0; i < span.Length; i++)
                {
                    var ch = span[i];
                    if (escape) { escape = false; continue; }
                    if (ch == '\\') { // escape only matters inside quotes
                        if (inSingle || inDouble) escape = true;
                        continue;
                    }
                    if (ch == '"' && !inSingle) inDouble = !inDouble;
                    else if (ch == '\'' && !inDouble) inSingle = !inSingle;
                    else if (ch == '#' && !inSingle && !inDouble)
                    {
                        end = i; // comment starts here
                        break;
                    }
                }
                var value = span[..end].ToString().Trim();
                if (value.Length == 0) return null;
                // Strip surrounding quotes AFTER removing inline comment
                if ((value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2) || (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2))
                {
                    value = value[1..^1];
                }
                // Ignore placeholder values like ${file.jarVersion}
                if (value.Contains("${")) return null;
                // Unescape simple sequences (minimal TOML subset)
                value = value.Replace("\\\"", "\"");
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (trimmed.StartsWith("modId", StringComparison.OrdinalIgnoreCase)) id = ReadValue(trimmed) ?? id;
            else if (trimmed.StartsWith("version", StringComparison.OrdinalIgnoreCase)) ver = ReadValue(trimmed) ?? ver;
            else if (trimmed.StartsWith("displayName", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase)) name = ReadValue(trimmed) ?? name;
        }
        return (id, ver, name);
    }
}
