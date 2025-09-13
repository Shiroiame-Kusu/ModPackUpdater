# ModPackUpdater Client Mod Integration Guide

This guide explains how to build a Minecraft client mod that integrates with ModPackUpdater. The server exposes a minimal HTTP API for discovering a pack, reading the latest manifest, listing mods, and downloading files. Diffing is done client-side.

Model: single-version ("latest"). Each pack is stored at `packs/<packId>/` on the server and always represents the latest contents.

- Hashing: SHA-256, lower-hex (64 chars)
- Paths: forward slashes, relative only (no leading `/`, no `..` segments)
- Ignored by server hashing: `pack.json`, `.DS_Store`, `Thumbs.db`, and any dot-directories (e.g., `.git/`)

## Server manifest caching

To prevent I/O exhaustion from repeated full directory scans, the server caches built manifests in memory per pack:
- Cache TTL: ~5 minutes absolute, with a sliding refresh of ~2 minutes under access
- Invalidation: file system watcher per pack; any change (create/update/delete/rename) invalidates the cache immediately
- Concurrency control: stampede protection ensures only one manifest builder runs per pack at a time
- Effect on clients: manifests may remain stable for a few minutes; `createdAt` reflects when the cached manifest was built

Clients can optionally cache the manifest for a short period (e.g., 60–120s) to reduce polling.

## Configure

- baseUrl: The server root, e.g., `http://localhost:5000`
- packId: The pack to sync, e.g., `example-pack`
- installRoot: Client install root (e.g., the instance’s `.minecraft` directory)

## Folder layout on the server (reference)

- `packs/<packId>/` (always latest)
  - `pack.json` (metadata written by importer)
  - `mods/`, `config/`, `resourcepacks/`, etc. (actual files to sync)

`pack.json` schema (server-side metadata; clients typically rely on the HTTP API):

```json
{
  "displayName": "string|null",
  "mcVersion": "string|null",
  "loaderName": "fabric|forge|neoforge|quilt|null",
  "loaderVersion": "string|null",
  "channel": "string|null",
  "description": "string|null"
}
```

## HTTP API

Base URL is your server root.

- GET /health
  - 200: `{ "status": "ok" }`

- GET /packs/
  - 200: `["string", ...]` (pack IDs)

- GET /packs/{packId}
  - 200: `{ "packId": "string", "latestVersion": "latest", "versions": ["latest"] }`
  - 404: pack not found

- GET /packs/{packId}/manifest[?version=latest]
  - 200:
    ```json
    {
      "packId": "string",
      "version": "latest",
      "displayName": "string|null",
      "mcVersion": "string|null",
      "loader": { "name": "string", "version": "string" } | null,
      "files": [ { "path": "string", "sha256": "string", "size": 0 } ],
      "createdAt": "ISO-8601 string",
      "channel": "string|null",
      "description": "string|null"
    }
    ```
  - 404: pack not found

- GET /packs/{packId}/mods[?version=latest]
  - 200: `[ { "path": "string", "id": "string|null", "version": "string|null", "name": "string|null", "loader": "fabric|forge|neoforge|quilt|null" } ]`
  - 404: pack not found

- GET /packs/{packId}/file?path=relative/path[&version=latest]
  - 200: application/octet-stream (Range supported)
  - 404: pack or file not found

Notes:
- Version is currently always `latest` in responses; the `version` query parameter is accepted for future compatibility.
- All paths must be safe relative. Use forward slashes on all platforms.
- The server skips symlinked files and files inside symlinked directories for safety.
- Mods list moved out of the manifest to reduce payload size and allow lazy loading.

## Client-side diff algorithm

Inputs:
- Server manifest: list of `{ path, sha256, size }`
- Client scan: list of `{ path, sha256, size }` from `installRoot`

Steps:
1) Build maps keyed by `path` (case-insensitive on Windows; prefer OrdinalIgnoreCase)
2) For each server file:
   - If the client doesn’t have it → Add
   - If hashes differ → Update
3) For each client file not present on server → Delete

Apply:
- For Add/Update: GET `/file?path=...`, write to disk atomically; verify hash after write
- For Delete: remove the local file after successful updates

## Client workflow

1) Discover (optional)
- GET `/health` (expect 200 ok)
- GET `/packs/{packId}` (expect 200, latestVersion=`latest`)

2) Get server manifest
- GET `/packs/{packId}/manifest`

3) (Optional) Get mods metadata (for UI / audit / reporting)
- GET `/packs/{packId}/mods`

4) Scan local files
- Recursively enumerate under `installRoot`
- Exclude the ignore list above
- For each file, compute SHA-256 (lower-hex)

5) Compute client-side diff
- Compare server manifest to local scan as described

6) Download changes
- GET `/file?path=...` for each changed path and write to `installRoot`
- After writing, re-hash updated files and verify against the manifest’s `sha256`

7) Apply loader, if needed
- Read loader info from manifest (`loader.name`, `loader.version`, optionally `mcVersion`)

8) Repeat periodically
- Re-fetch manifest, re-scan, compute diff, and apply changes.

## Implementation details

- Hashing: Use a streaming SHA-256; emit lowercase hex (64 chars)
- Concurrency: 4 parallel downloads is a good default
- Timeouts: connect 5s, read 120s
- Retries: 3 attempts with exponential/backoff (e.g., 250ms base)
- Atomic writes: write to a temp file, fsync, then move/replace
- Delete ops: remove files only after successful updates
- Range downloads: `/file` supports HTTP Range for resume; optional to implement
- Manifest caching: treat `createdAt` as a hint; a new manifest may appear a few minutes after files change due to cache TTL unless a change invalidates it sooner
- Mod metadata sources: server extracts from multiple file types; some fields may be null or inferred

## Data contracts (summarized)

- Manifest file entry
  ```json
  { "path": "string", "sha256": "string", "size": 0 }
  ```

- Mod entry
  ```json
  { "path": "mods/example.jar", "id": "string|null", "version": "string|null", "name": "string|null", "loader": "fabric|forge|neoforge|quilt|null" }
  ```

## cURL examples

- Manifest
```bash
curl -sS http://localhost:5000/packs/my-pack/manifest | jq .
```

- Mods
```bash
curl -sS http://localhost:5000/packs/my-pack/mods | jq .
```

- File
```bash
curl -sS -OJ "http://localhost:5000/packs/my-pack/file?path=mods/example.jar"
```

## Edge cases and errors

- 404 Not Found: pack or path doesn’t exist (re-check `packId`, `path`)
- Empty diff: no operations => already up to date
- Path safety: server rejects unsafe paths (starting with `/` or containing `..`)
- Mod metadata: some jars include placeholders or no metadata; fields may be null or filename-derived

## Security

- Place ModPackUpdater behind TLS (reverse proxy) if exposed publicly
- Add authentication at the proxy if needed (e.g., auth header injection)
- Client should validate baseUrl and use HTTPS where available

## Minimal checklist for a client mod

- [ ] Discover server and pack (health, summary)
- [ ] Get manifest
- [ ] (Optional) Get mods metadata
- [ ] Scan installRoot and compute SHA-256 hashes (lower-hex)
- [ ] Compute diff locally
- [ ] Fetch changes via `/file`
- [ ] Apply deletes and verify hashes
- [ ] Handle loader install/update if needed
- [ ] Implement retries, timeouts, and atomic writes
- [ ] Optionally surface mods info to users (IDs, names, versions)

---

For server-side importing, use:

```bash
ModPackUpdater import -f /path/to/pack.(mcpack|mrpack|zip) [-p mypack] [-y]
```

The importer parses in-archive metadata, extracts overrides, and writes a unified `pack.json`; in single-version mode it always updates `packs/<packId>/`.
