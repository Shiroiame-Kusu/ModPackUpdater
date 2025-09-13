# ModPackUpdater C# Client Implementation Guide (CLIENT2)

Audience: Engineers building a .NET / C# (desktop tool, launcher plugin, server-side updater, or library) that syncs a local Minecraft instance with a ModPackUpdater server.

Scope: End-to-end design, data contracts, resilient HTTP access, diffing, hashing, download pipeline, progress reporting, caching, testing, and optional advanced features.

---
## 1. Core Concepts Recap

Single-version model: each pack lives at `packs/<packId>/` representing the **latest** state. The HTTP API exposes:
- Pack listing & summary
- Manifest (file inventory + metadata) WITHOUT mods list (mods moved to separate endpoint)
- Mods metadata endpoint
- Raw file download endpoint

Manifests are cached server-side (see CLIENT.md) and include `createdAt`. Treat it as informational — always trust hash list.

---
## 2. HTTP Endpoints (Quick Reference)

Base: `https://host/`

| Endpoint | Method | Response | Notes |
|----------|--------|----------|-------|
| /health | GET | `{ "status": "ok" }` | Liveness check |
| /packs/ | GET | `["packId", ...]` | Discover packs |
| /packs/{packId} | GET | `{ packId, latestVersion:"latest", versions:["latest"] }` | Summary |
| /packs/{packId}/manifest | GET | Manifest JSON (see below) | `version` query optional (currently always latest) |
| /packs/{packId}/mods | GET | Array of mod metadata objects | Lazy; not embedded in manifest |
| /packs/{packId}/file?path=relative/path | GET | File bytes (supports Range) | Use for incremental downloads |

### 2.1 Manifest Shape
```json
{
  "packId": "example-pack",
  "version": "latest",
  "displayName": "Example Pack",
  "mcVersion": "1.20.1",
  "loader": { "name": "fabric", "version": "0.15.11" },
  "files": [ { "path": "mods/example.jar", "sha256": "<64 hex>", "size": 12345 } ],
  "createdAt": "2025-09-10T12:34:56.789Z",
  "channel": "stable",
  "description": "Sample"
}
```

### 2.2 Mod Metadata Entry
```json
{
  "path": "mods/example.jar",
  "id": "fabric_example",
  "version": "1.2.3",
  "name": "Fabric Example",
  "loader": "fabric"
}
```
Values may be null if extraction fails.

---
## 3. C# Data Models

Prefer immutable `record` types with source-generated serialization for performance.

```csharp
using System.Text.Json.Serialization;

public sealed record PackSummary(
    [property: JsonPropertyName("packId")] string PackId,
    [property: JsonPropertyName("latestVersion")] string LatestVersion,
    [property: JsonPropertyName("versions")] string[] Versions
);

public sealed record LoaderInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version
);

public sealed record ManifestFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size
);

public sealed record PackManifest(
    [property: JsonPropertyName("packId")] string PackId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("mcVersion")] string? McVersion,
    [property: JsonPropertyName("loader")] LoaderInfo? Loader,
    [property: JsonPropertyName("files")] ManifestFile[] Files,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("description")] string? Description
);

public sealed record ModMetadata(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("loader")] string? Loader
);
```

Common `JsonSerializerOptions`:
```csharp
static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = false
};
```

---
## 4. HttpClient Design

Guidelines:
- Reuse a single `HttpClient` (or `IHttpClientFactory`) for entire app.
- Set default timeout (e.g., 100s) but allow longer for large downloads by using per-request CTS.
- Add decompression: `AutomaticDecompression = GZip | Deflate` if server enables.

Example factory:
```csharp
var handler = new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All
};
var http = new HttpClient(handler)
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30)
};
```

Optional resilience (with Polly): retries for transient 5xx / network / 408.

---
## 5. Fetch Helpers

```csharp
public async Task<PackManifest> GetManifestAsync(string packId, CancellationToken ct)
{
    using var resp = await _http.GetAsync($"packs/{packId}/manifest", ct);
    resp.EnsureSuccessStatusCode();
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    return await JsonSerializer.DeserializeAsync<PackManifest>(stream, JsonOpts, ct)
        ?? throw new InvalidOperationException("Null manifest");
}

public async Task<IReadOnlyList<ModMetadata>> GetModsAsync(string packId, CancellationToken ct)
{
    using var resp = await _http.GetAsync($"packs/{packId}/mods", ct);
    resp.EnsureSuccessStatusCode();
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    return await JsonSerializer.DeserializeAsync<List<ModMetadata>>(stream, JsonOpts, ct)
        ?? new();
}
```

---
## 6. Local File Scanning

Ignore list (must mirror server view):
- `pack.json`
- `.DS_Store`, `Thumbs.db`
- Any directory starting with `.` (e.g. `.git`, `.idea`)

Implementation:
```csharp
IEnumerable<(string path, FileInfo fi)> EnumerateLocalFiles(DirectoryInfo root)
{
    int rootLen = root.FullName.Length + 1;
    foreach (var fi in root.EnumerateFiles("*", SearchOption.AllDirectories))
    {
        if (fi.Directory?.Name.StartsWith('.') == true) continue;
        var rel = fi.FullName[rootLen..].Replace(Path.DirectorySeparatorChar, '/');
        if (rel.Equals("pack.json", StringComparison.OrdinalIgnoreCase)) continue;
        if (rel.EndsWith(".DS_Store", StringComparison.OrdinalIgnoreCase)) continue;
        if (rel.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
        yield return (rel, fi);
    }
}
```

---
## 7. Hashing (SHA-256 Lower Hex)

```csharp
static string HashFile(string path)
{
    using var fs = File.OpenRead(path);
    using var sha = System.Security.Cryptography.SHA256.Create();
    var hash = sha.ComputeHash(fs);
    return Convert.ToHexString(hash).ToLowerInvariant();
}
```

Parallel hashing (bounded):
```csharp
var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
var results = new ConcurrentDictionary<string,(string hash,long size)>();
await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (f, ct) =>
{
    await semaphore.WaitAsync(ct);
    try
    {
        string h = HashFile(f.fi.FullName);
        results[f.path] = (h, f.fi.Length);
    }
    finally { semaphore.Release(); }
});
```

---
## 8. Diff Algorithm

Definitions:
- Server map: `Dictionary<string, ManifestFile>` (OrdinalIgnoreCase on Windows)
- Local map: `Dictionary<string, (hash,size)>`

Operations:
```csharp
public sealed record Plan(
    List<ManifestFile> Downloads,
    List<string> Deletes
);

Plan Diff(PackManifest manifest, IReadOnlyDictionary<string,(string hash,long size)> local, bool windows)
{
    var cmp = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    var server = manifest.Files.ToDictionary(f => f.Path, cmp);

    var downloads = new List<ManifestFile>();
    foreach (var f in manifest.Files)
    {
        if (!local.TryGetValue(f.Path, out var loc) || !string.Equals(loc.hash, f.Sha256, StringComparison.OrdinalIgnoreCase))
            downloads.Add(f);
    }

    var deletes = new List<string>();
    foreach (var kv in local)
        if (!server.ContainsKey(kv.Key))
            deletes.Add(kv.Key);

    return new Plan(downloads, deletes);
}
```

---
## 9. Download Pipeline

Goals:
- Parallel (default 4)
- Atomic writes
- Hash verification post-download
- Optional progress reporting

```csharp
public async Task DownloadFileAsync(string packId, ManifestFile f, DirectoryInfo root, IProgress<(string path,long read,long total)>? progress, CancellationToken ct)
{
    var url = $"packs/{packId}/file?path={Uri.EscapeDataString(f.Path)}";
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    resp.EnsureSuccessStatusCode();

    var targetPath = Path.Combine(root.FullName, f.Path.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
    var tmp = targetPath + ".downloading";

    await using (var src = await resp.Content.ReadAsStreamAsync(ct))
    await using (var dst = File.Create(tmp))
    {
        var buffer = new byte[81920];
        long total = 0;
        int r;
        while ((r = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, r), ct);
            total += r;
            progress?.Report((f.Path, total, f.Size));
        }
    }

    // Verify hash
    var actual = HashFile(tmp);
    if (!actual.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
    {
        File.Delete(tmp);
        throw new InvalidOperationException($"Hash mismatch for {f.Path}: expected {f.Sha256} got {actual}");
    }

    // Atomic replace
    if (File.Exists(targetPath)) File.Delete(targetPath);
    File.Move(tmp, targetPath);
}
```

Parallel orchestration:
```csharp
public async Task ApplyPlanAsync(string packId, Plan plan, DirectoryInfo root, CancellationToken ct)
{
    var progress = new Progress<(string,long,long)>(p => Console.WriteLine($"DL {p.Item1} {p.Item2}/{p.Item3}"));
    var throttler = new SemaphoreSlim(4);
    var tasks = plan.Downloads.Select(async f =>
    {
        await throttler.WaitAsync(ct);
        try { await DownloadFileAsync(packId, f, root, progress, ct); }
        finally { throttler.Release(); }
    });
    await Task.WhenAll(tasks);

    foreach (var del in plan.Deletes)
    {
        var path = Path.Combine(root.FullName, del.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }
}
```

---
## 10. Manifest Caching (Client Side)

Simple time + ETag-like cache (server does not send ETag now; use content hash of `files`).

```csharp
public sealed class ManifestCache
{
    private PackManifest? _manifest;
    private DateTimeOffset _expires;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(90);

    public bool TryGet(out PackManifest? manifest)
    {
        if (_manifest != null && DateTimeOffset.UtcNow < _expires)
        { manifest = _manifest; return true; }
        manifest = null; return false;
    }

    public void Store(PackManifest m)
    { _manifest = m; _expires = DateTimeOffset.UtcNow + _ttl; }
}
```

---
## 11. Putting It Together (High-Level Flow)

```csharp
var packId = "example-pack";
var rootDir = new DirectoryInfo("/path/to/.minecraft");
var manifest = cache.TryGet(out var cached) ? cached! : await client.GetManifestAsync(packId, ct);
cache.Store(manifest);

var localFiles = EnumerateLocalFiles(rootDir)
    .Select(f => (f.path, hashInfo: (hash: HashFile(f.fi.FullName), size: f.fi.Length)))
    .ToDictionary(x => x.path, x => x.hashInfo, StringComparer.OrdinalIgnoreCase);

var plan = Diff(manifest, localFiles, OperatingSystem.IsWindows());
if (plan.Downloads.Count == 0 && plan.Deletes.Count == 0)
{
    Console.WriteLine("Up to date.");
}
else
{
    await ApplyPlanAsync(packId, plan, rootDir, ct);
}
```

---
## 12. Progress & UX Enhancements

- Aggregate progress: sum total bytes from `manifest.files` of planned downloads.
- Per-file + overall progress; expose events or `IProgress<double>`.
- Log phases: fetch manifest, hash scan, plan, downloads, verify, cleanup.

---
## 13. Error Handling Strategy

| Scenario | Handling |
|----------|----------|
| Network transient (timeout, 5xx) | Retry w/ exponential backoff (cap attempts) |
| Hash mismatch | Redownload once; if still mismatch fail fast |
| Disk full | Abort, surface specific exception message |
| Unauthorized (future auth) | Prompt or refresh token |
| Partial local corruption | Treated like changed -> re-download |

Always differentiate fatal vs transient faults.

---
## 14. Range / Resume (Optional)

File endpoint supports HTTP Range. Implement if large file downloads are common:
1. Attempt full download.
2. On interruption, record bytes written.
3. Re-issue with header: `Range: bytes={already}-`.
4. Validate server returns 206.
5. Continue writing in append mode.
6. Verify final hash.

---
## 15. Mod Metadata Consumption

Use `/mods` for UI (listing mod versions, detecting mismatches). Cache similarly to manifest but can refresh less often (e.g., every 5–10 minutes). Do **not** block critical sync on mods metadata failure.

---
## 16. Thread Safety & Concurrency

- Reuse `HttpClient` (thread-safe)
- Use immutable models
- Protect mutable caches with simple locks if needed
- Avoid scanning + downloading same file concurrently (plan-first pattern prevents this)

---
## 17. Testing Strategy

- Unit: diff logic (add/update/delete scenarios)
- Unit: hash function (known vector)
- Unit: manifest cache TTL
- Integration: hit a test server (see existing test patterns) and perform full sync into temp dir
- Property-based (optional): random manifest vs local set to ensure no false negatives

Example xUnit diff test:
```csharp
[Fact]
public void Diff_Computes_Add_And_Delete()
{
    var mf = new PackManifest("p","latest",null,null,null,new[]{
        new ManifestFile("a.txt","h1",1),
        new ManifestFile("b.txt","h2",1)
    }, DateTimeOffset.UtcNow, null, null);
    var local = new Dictionary<string,(string,long)>{ {"a.txt",("h1",1)}, {"c.txt",("zzz",5)} };
    var plan = Diff(mf, local, false);
    Assert.Single(plan.Downloads, f => f.Path == "b.txt");
    Assert.Single(plan.Deletes, p => p == "c.txt");
}
```

---
## 18. Packaging & Distribution

If shipping as a library:
- Project: `net8.0` + `net9.0` targets
- Strong naming optional
- XML docs + `README` in package root
- Consider source generator for manifest -> strongly typed wrapper if expanding

If shipping as CLI:
- Provide `sync` command with options: `--base-url`, `--pack-id`, `--root`, `--parallel`, `--dry-run`.

---
## 19. Performance Considerations

- Hashing is the main CPU cost; parallelize but avoid saturating I/O (P = cores or cores - 1)
- Avoid re-hashing unchanged files by storing a local cache (file path + size + last write time + hash). Recompute only if size or writeTime changed.
- Stream downloads directly to disk; avoid buffering whole file in memory.

Local hash cache sketch:
```csharp
public sealed record HashEntry(string Hash,long Size,DateTime LastWrite);
```
Serialize to JSON after successful sync.

---
## 20. Security & Hardening

- Enforce HTTPS when remote (validate scheme on `baseUrl`)
- Validate all `path` values are safe before writing (no `..`, no absolute). The server already filters, but defense-in-depth:
```csharp
bool IsSafe(string path) => !string.IsNullOrEmpty(path) && path.IndexOf("..", StringComparison.Ordinal) < 0 && !Path.IsPathRooted(path);
```
- Optionally verify server certificate pinning in sensitive deployments.

---
## 21. Logging & Observability

Suggest structured logging (Serilog):
- Manifest fetch: packId, fileCount, createdAt
- Diff summary: addCount, updateCount, deleteCount, totalBytes
- Per download: path, size, duration, success/fail
- Final result: elapsed, success

---
## 22. Extensibility Hooks

Interfaces:
```csharp
public interface IManifestProvider { Task<PackManifest> GetAsync(string packId, CancellationToken ct); }
public interface IFileDownloader { Task DownloadAsync(string packId, ManifestFile file, CancellationToken ct); }
public interface IHasher { string Hash(string path); }
public interface IProgressReporter { void FileProgress(string path,long read,long total); }
```
Allow consumers to swap in custom implementations.

---
## 23. Minimal End-to-End Example

```csharp
public async Task SyncAsync(string baseUrl, string packId, DirectoryInfo root, CancellationToken ct)
{
    var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    var client = new MyPackClient(http);
    var manifest = await client.GetManifestAsync(packId, ct);

    var local = EnumerateLocalFiles(root)
        .ToDictionary(x => x.path, x => (HashFile(x.fi.FullName), x.fi.Length), StringComparer.OrdinalIgnoreCase);

    var plan = Diff(manifest, local, OperatingSystem.IsWindows());
    if (plan.Downloads.Count == 0 && plan.Deletes.Count == 0) return;

    await ApplyPlanAsync(packId, plan, root, ct);
}
```

---
## 24. Validation Checklist

- [ ] Manifest fetched and parsed
- [ ] Files enumerated & hashed (ignore list honored)
- [ ] Diff computed correctly (spot test)
- [ ] Parallel downloads limited and stable under cancellation
- [ ] Hash verification fails fast & cleans temp files
- [ ] Deletes only after successful downloads
- [ ] Logging & progress integrated
- [ ] Graceful cancellation via `CancellationToken`

---
## 25. Future Enhancements (Ideas)

- Delta compression (patches) if network constrained
- ETag / If-None-Match when server adds support
- Persistent chunked resume metadata
- Integrity attestation via signature file
- Batched multi-file endpoint (if introduced for latency optimization)

---
## 26. Quick Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Always re-downloading | Hash mismatch due to uppercase vs lowercase or CRLF translation | Ensure raw bytes + lowercase hex; avoid altering line endings |
| Missing mods metadata | Not calling `/mods` endpoint | Invoke lazily after manifest |
| Slow hashing | Single-threaded on HDD | Increase parallelism modestly (avoid saturating disk) |
| Intermittent failures at large files | Timeout too low | Increase per-request timeout / implement range resume |

---
## 27. License & Attribution

Match the project license (see root `LICENSE.txt`). Attribute upstream if redistributing the client library as open source.

---
## 28. Summary

This guide provides a comprehensive path to a robust, efficient, and maintainable ModPackUpdater C# client. Start minimal: fetch manifest → diff → download → verify. Add caching, retries, progress, and resume as needed.

Happy building!

