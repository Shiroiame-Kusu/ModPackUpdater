# ModPackUpdater Client Mod Integration Guide

This guide explains how to build a Minecraft client mod that integrates with ModPackUpdater. The server exposes a minimal HTTP API for discovering a pack, reading the latest manifest, and downloading files. Diffing is done client-side.

Model: single-version ("latest"). Each pack is stored at `packs/<packId>/` on the server and always represents the latest contents.

- Hashing: SHA-256, lower-hex (64 chars)
- Paths: forward slashes, relative only (no leading `/`, no `..` segments)
- Ignored by server hashing: `pack.json`, `.DS_Store`, `Thumbs.db`, and any dot-directories (e.g., `.git/`)

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

- GET /packs/{packId}/manifest
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

- GET /packs/{packId}/file?path=relative/path
  - 200: application/octet-stream (Range supported)
  - 404: pack or file not found

Notes:
- Version is always `latest` in responses.
- All paths must be safe relative. Use forward slashes on all platforms.

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

3) Scan local files
- Recursively enumerate under `installRoot`
- Exclude the ignore list above
- For each file, compute SHA-256 (lower-hex) and collect `{ path, sha256, size }`, where `path` is the install-root-relative path with forward slashes

4) Compute client-side diff
- Compare server manifest to local scan as described
- Produce operations: Add, Update, Delete

5) Download changes
- GET `/file?path=...` for each changed path and write to `installRoot`
- After writing, re-hash updated files and verify against the manifest’s `sha256`

6) Apply loader, if needed
- Read loader info from `/packs/{packId}/manifest` (`loader.name`, `loader.version`, optionally `mcVersion`)
- If the client’s loader differs, run the appropriate installer (Fabric/Forge/NeoForge/Quilt) before or after file sync, then restart if required

7) Repeat periodically
- Re-fetch manifest, re-scan, compute diff, and apply changes. The server always represents the current latest.

## Implementation details

- Hashing: Use a streaming SHA-256; emit lowercase hex (64 chars)
- Concurrency: 4 parallel downloads is a good default
- Timeouts: connect 5s, read 120s
- Retries: 3 attempts with exponential/backoff (e.g., 250ms base)
- Atomic writes: write to a temp file, fsync, then move/replace
- Delete ops: remove files only after successful updates
- Range downloads: `/file` supports HTTP Range for resume; optional to implement

## Data contracts (summarized)

- Manifest file entry
  ```json
  { "path": "string", "sha256": "string", "size": 0 }
  ```

## cURL examples

- Manifest
```bash
curl -sS http://localhost:5000/packs/my-pack/manifest | jq .
```

- File
```bash
curl -sS -OJ "http://localhost:5000/packs/my-pack/file?path=mods/example.jar"
```

## Edge cases and errors

- 404 Not Found: pack or path doesn’t exist (re-check `packId`, `path`)
- Empty diff: no operations => already up to date
- No overrides in pack: importing may produce few files; still valid
- Path safety: the server rejects unsafe paths (starting with `/` or containing `..`)

## Security

- Place ModPackUpdater behind TLS (reverse proxy) if exposed publicly
- Add authentication at the proxy if needed (e.g., auth header injection)
- Client should validate baseUrl and use HTTPS where available

## Minimal checklist for a client mod

- [ ] Discover server and pack (health, summary)
- [ ] Get manifest
- [ ] Scan installRoot and compute SHA-256 hashes (lower-hex)
- [ ] Compute diff locally
- [ ] Fetch changes via `/file`
- [ ] Apply deletes and verify hashes
- [ ] Handle loader install/update if needed
- [ ] Implement retries, timeouts, and atomic writes

---

For server-side importing, use:

```bash
ModPackUpdater import -f /path/to/pack.(mcpack|mrpack|zip) [-p mypack] [-y]
```

The importer parses in-archive metadata (manifest.json or modrinth.index.json), extracts overrides, and writes a unified `pack.json`; in single-version mode it always updates `packs/<packId>/`.
