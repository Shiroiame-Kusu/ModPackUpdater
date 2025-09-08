using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModPackUpdater.Models;

namespace ModPackUpdater.Services;

public static class PackImportService
{
    public sealed record ImportOptions(string FilePath, string? PackId, string? Version, bool Overwrite);

    private sealed record ArchiveMeta(
        string? PackId,
        string? Version,
        string? DisplayName,
        string? Description,
        string? McVersion,
        string? LoaderName,
        string? LoaderVersion,
        string? Channel,
        string? OverridesDir
    );

    public static async Task<int> Import(string packsRoot, ImportOptions opt)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(opt.FilePath) || !File.Exists(opt.FilePath))
            {
                Console.Error.WriteLine("Import error: file not found: " + opt.FilePath);
                return 2;
            }
            var ext = Path.GetExtension(opt.FilePath).ToLowerInvariant();
            if (ext is not ".mcpack" and not ".zip" and not ".mrpack")
            {
                Console.Error.WriteLine("Import error: unsupported file extension. Use .mcpack, .mrpack, or .zip");
                return 2;
            }

            string? packId = opt.PackId;
            ArchiveMeta? meta = null;

            // Try to parse metadata from inside the archive first
            if (TryParseArchive(opt.FilePath, out var metaFromArchive))
            {
                meta = metaFromArchive;
                packId ??= meta.PackId;
            }

            // If still missing, fallback for packId from file name
            if (string.IsNullOrWhiteSpace(packId))
            {
                if (TryInferIdAndVersionFromFile(opt.FilePath, out var id3, out _))
                {
                    packId = id3;
                }
            }

            if (string.IsNullOrWhiteSpace(packId))
            {
                Console.Error.WriteLine("Import error: could not determine pack id from archive metadata. Provide --pack.");
                return 2;
            }

            packId = SanitizeId(packId!);
            if (string.IsNullOrWhiteSpace(packId))
            {
                Console.Error.WriteLine("Import error: invalid packId after sanitization.");
                return 2;
            }

            var targetDir = Path.GetFullPath(Path.Combine(packsRoot, packId!));
            var rootDir = Path.GetFullPath(packsRoot);
            if (!targetDir.StartsWith(rootDir, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Import error: invalid target path.");
                return 3;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            var exists = Directory.Exists(targetDir);
            if (exists && opt.Overwrite)
            {
                // Clean replace latest
                Directory.Delete(targetDir, recursive: true);
                exists = false;
            }
            if (!exists)
            {
                Directory.CreateDirectory(targetDir);
            }

            Console.WriteLine($"Importing {opt.FilePath} -> {targetDir} (id={packId})");

            using var zip = ZipFile.OpenRead(opt.FilePath);
            // Build list of file entries
            var files = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            if (files.Count == 0)
            {
                Console.Error.WriteLine("Import error: archive is empty.");
                return 5;
            }

            // If the archive has a known overrides directory, only extract those contents
            string? overridesPrefix = null;
            if (meta?.OverridesDir is { } ov && !string.IsNullOrWhiteSpace(ov))
            {
                var norm = ov.Replace('\\', '/').Trim('/');
                if (!string.IsNullOrWhiteSpace(norm)) overridesPrefix = norm + "/";
            }

            // Compute common root segments to strip
            var splitPaths = files.Select(e => e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
            var common = CommonPrefix(splitPaths);

            int extracted = 0;
            foreach (var entry in files)
            {
                var fullNorm = entry.FullName.Replace('\\', '/');
                // Skip non-overrides when we know an overrides dir
                if (overridesPrefix != null)
                {
                    var idx = fullNorm.IndexOf(overridesPrefix, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    var after = fullNorm[(idx + overridesPrefix.Length)..];
                    if (string.IsNullOrWhiteSpace(after)) continue;
                    if (!IsSafeRelative(after)) { Console.Error.WriteLine($"Skipping unsafe entry: {entry.FullName}"); continue; }
                    var destPath2 = Path.GetFullPath(Path.Combine(targetDir, after));
                    if (!destPath2.StartsWith(targetDir, StringComparison.Ordinal)) { Console.Error.WriteLine($"Skipping out-of-root entry: {entry.FullName}"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath2)!);
                    entry.ExtractToFile(destPath2, overwrite: true);
                    extracted++;
                    continue;
                }

                // Generic extraction with common-prefix trimming
                var segs = fullNorm.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var relSegs = segs.Skip(common).ToArray();
                var relPath = string.Join('/', relSegs);
                if (string.IsNullOrWhiteSpace(relPath))
                {
                    continue;
                }
                if (!IsSafeRelative(relPath))
                {
                    Console.Error.WriteLine($"Skipping unsafe entry: {entry.FullName}");
                    continue;
                }
                var destPath = Path.GetFullPath(Path.Combine(targetDir, relPath));
                if (!destPath.StartsWith(targetDir, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"Skipping out-of-root entry: {entry.FullName}");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                extracted++;
            }

            if (extracted == 0)
            {
                Console.Error.WriteLine("Import warning: no files extracted (maybe the pack only lists remote mods and has no overrides).");
            }

            // Write pack.json metadata for unified structure
            var packMeta = new PackMeta(
                DisplayName: meta?.DisplayName ?? packId,
                McVersion: meta?.McVersion,
                LoaderName: meta?.LoaderName,
                LoaderVersion: meta?.LoaderVersion,
                Channel: meta?.Channel,
                Description: meta?.Description
            );
            try
            {
                var metaPath = Path.Combine(targetDir, "pack.json");
                await using var fs = File.Create(metaPath);
                await JsonSerializer.SerializeAsync(fs, packMeta, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception mex)
            {
                Console.Error.WriteLine("Import warning: failed to write pack.json: " + mex.Message);
            }

            Console.WriteLine("Import completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Import failed: " + ex.Message);
            return 10;
        }
    }

    private static bool TryParseArchive(string filePath, out ArchiveMeta meta)
    {
        meta = new ArchiveMeta(null, null, null, null, null, null, null, null, null);
        try
        {
            using var zip = ZipFile.OpenRead(filePath);

            static ZipArchiveEntry? FindByName(ZipArchive z, string fileName)
                => z.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase));

            var manifest = FindByName(zip, "manifest.json");
            if (manifest != null)
            {
                using var ms = manifest.Open();
                using var doc = JsonDocument.Parse(ms);
                var root = doc.RootElement;

                if (root.TryGetProperty("header", out var header))
                {
                    string? name = TryGetString(header, "name");
                    if (!string.IsNullOrWhiteSpace(name)) meta = meta with { PackId = SanitizeId(name), DisplayName = name };
                    if (header.TryGetProperty("version", out var verArr) && verArr.ValueKind == JsonValueKind.Array)
                    {
                        var parts = verArr.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetInt32().ToString() : v.GetString() ?? "0");
                        var ver = string.Join('.', parts);
                        meta = meta with { Version = SanitizeVersion(ver) };
                    }
                    var desc = TryGetString(header, "description");
                    if (!string.IsNullOrWhiteSpace(desc)) meta = meta with { Description = desc };
                }

                if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    var n = nameProp.GetString()!;
                    meta = meta with { PackId = meta.PackId ?? SanitizeId(n), DisplayName = meta.DisplayName ?? n };
                }
                if (root.TryGetProperty("version", out var verProp) && verProp.ValueKind == JsonValueKind.String)
                {
                    meta = meta with { Version = meta.Version ?? SanitizeVersion(verProp.GetString()!) };
                }
                if (root.TryGetProperty("overrides", out var ovProp) && ovProp.ValueKind == JsonValueKind.String)
                {
                    var ov = ovProp.GetString();
                    if (!string.IsNullOrWhiteSpace(ov)) meta = meta with { OverridesDir = ov };
                }
                if (root.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.Object)
                {
                    var mcVer = TryGetString(mc, "version");
                    if (!string.IsNullOrWhiteSpace(mcVer)) meta = meta with { McVersion = mcVer };
                    if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in loaders.EnumerateArray())
                        {
                            var id = TryGetString(el, "id");
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                var parts = id!.Split('-', 2);
                                if (parts.Length == 2)
                                    meta = meta with { LoaderName = parts[0], LoaderVersion = parts[1] };
                            }
                        }
                    }
                }
            }

            var mr = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), "modrinth.index.json", StringComparison.OrdinalIgnoreCase));
            if (mr != null)
            {
                using var ms2 = mr.Open();
                using var doc2 = JsonDocument.Parse(ms2);
                var root2 = doc2.RootElement;
                var name = TryGetString(root2, "name");
                if (!string.IsNullOrWhiteSpace(name)) meta = meta with { PackId = meta.PackId ?? SanitizeId(name!), DisplayName = meta.DisplayName ?? name };

                var version = TryGetString(root2, "version")
                              ?? TryGetString(root2, "versionId")
                              ?? TryGetString(root2, "version_id")
                              ?? TryGetString(root2, "version_number")
                              ?? TryGetString(root2, "versionNumber");
                if (!string.IsNullOrWhiteSpace(version)) meta = meta with { Version = meta.Version ?? SanitizeVersion(version!) };

                var ov = TryGetString(root2, "overrides");
                if (string.IsNullOrWhiteSpace(ov)) ov = "overrides";
                meta = meta with { OverridesDir = ov };

                if (root2.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    var mcVer = TryGetString(deps, "minecraft");
                    if (!string.IsNullOrWhiteSpace(mcVer)) meta = meta with { McVersion = mcVer };
                    var loaderKeys = new[] { "fabric-loader", "quilt-loader", "forge", "neoforge" };
                    foreach (var key in loaderKeys)
                    {
                        if (TryGetString(deps, key) is { } lv && !string.IsNullOrWhiteSpace(lv))
                        {
                            meta = meta with { LoaderName = key.Replace("-loader", ""), LoaderVersion = lv };
                            break;
                        }
                    }
                }
            }

            return meta.PackId != null || meta.Version != null || meta.OverridesDir != null;
        }
        catch
        {
            return false;
        }

        static string? TryGetString(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(name, out var prop)) return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
    }

    private static int CommonPrefix(List<string[]> paths)
    {
        if (paths.Count == 0) return 0;
        var minLen = paths.Min(a => a.Length);
        var depth = 0;
        for (int i = 0; i < minLen; i++)
        {
            var s = paths[0][i];
            if (paths.Any(p => !string.Equals(p[i], s, StringComparison.Ordinal))) break;
            depth++;
        }
        return depth;
    }

    private static bool IsSafeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/')) return false;
        if (normalized.Contains("../")) return false;
        return true;
    }

    public static bool TryInferIdAndVersionFromFile(string filePath, out string? packId, out string? version)
    {
        packId = null; version = null;
        try
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var idx = name.LastIndexOf('-');
            if (idx > 0 && idx < name.Length - 1)
            {
                packId = name[..idx];
                version = name[(idx + 1)..];
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool TryInferIdAndVersionFromArchive(string filePath, out string? packId, out string? version)
    {
        packId = null; version = null;
        if (TryParseArchive(filePath, out var meta))
        {
            packId = meta.PackId;
            version = meta.Version;
            return !string.IsNullOrWhiteSpace(packId) && !string.IsNullOrWhiteSpace(version);
        }
        return false;
    }

    private static string SanitizeId(string input)
    {
        if (input == null) return "pack";
        var s = input.Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] {'/', '\\'}).ToHashSet();
        s = new string(s.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        s = Regex.Replace(s, "-+", "-");
        s = s.Trim('-').Trim();
        if (string.IsNullOrWhiteSpace(s)) s = "pack";
        return s;
    }

    private static string SanitizeVersion(string input)
    {
        if (input == null) return "1.0.0";
        var s = input.Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] {'/', '\\'}).ToHashSet();
        s = new string(s.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        s = s.Trim('-').Trim();
        if (string.IsNullOrWhiteSpace(s)) s = "1.0.0";
        return s;
    }
}
