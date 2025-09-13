# ModPackUpdater 

A tiny ASP.NET Core service and CLI that hosts the "latest" version of Minecraft modpacks. Clients fetch a manifest (SHA-256 + size) and download changed files; the server never computes diffs. Includes a simple importer for .mcpack, .mrpack, or .zip archives.

- Model: single-version per pack (always "latest" at `packs/<packId>/`)
- Hashes: SHA-256, lower-hex
- Paths: forward slashes, safe-relative only (no leading `/`, no `..`)
- Caching: manifests are built once and cached in-memory with FS watcher invalidation and stampede protection
- Startup warmup: optionally pre-generate manifests at application start
- Mods metadata: manifest includes an optional `mods[]` with detected IDs, versions, and loaders
- Logs: structured with Serilog to console and `logs/`

See docs/CLIENT.md for a client mod integration guide. For deployment and admin usage, see docs/SERVER.md.

## Quick start

Requirements: .NET 9 SDK

1) Build

```bash
dotnet build ModPackUpdater.sln
```

2) Run the server

```bash
ASPNETCORE_URLS=http://localhost:5000 dotnet run --project ModPackUpdater
```

3) Health check

```bash
curl -sS http://localhost:5000/health
```

## Configuration

- Packs root directory: `PacksRoot`
  - appsettings.json / appsettings.Development.json
  - or environment variable `PacksRoot`
  - default: `./packs` relative to the application base directory
- Manifest warmup (pre-generate on startup): `ManifestWarmup`
  - `Enabled`: true/false (default true)
  - `BlockOnStartup`: true/false (default false — warmup runs in background after start)
  - `MaxConcurrency`: integer (default ≈ CPU/2)
- Manifest build concurrency: `Concurrency`
  - `Hash`: max concurrent file hashing (default ≈ 2x cores, clamped 1–64)
  - `ModExtract`: max concurrent mod metadata extraction (default ≈ cores/2, clamped 1–32)
- Logging: Serilog writes to
  - console
  - `logs/app-YYYYMMDD.log` (application logs)
  - `logs/access-YYYYMMDD.log` (HTTP request access logs)

Example appsettings override:

```json
{
  "PacksRoot": "/absolute/path/to/packs",
  "ManifestWarmup": {
    "Enabled": true,
    "BlockOnStartup": false,
    "MaxConcurrency": 4
  },
  "Concurrency": {
    "Hash": 8,
    "ModExtract": 4
  }
}
```

## Packs layout (server)

Single-version model. Each pack is a folder under `PacksRoot`:

```
PacksRoot/
  my-pack/
    pack.json         # metadata written by importer
    mods/
    config/
    resourcepacks/
    ...
```

Ignored for hashing: `pack.json`, `.DS_Store`, `Thumbs.db`, and any dot-directories (e.g., `.git/`). Symlinks are skipped (files and ancestor directories).

## Importer (CLI)

Create/update a pack from an archive. It parses common metadata (Bedrock manifest.json, Modrinth modrinth.index.json, or general zip) and writes a unified `pack.json`. In single-version mode it always populates `packs/<packId>/`.

Usage:

```bash
# From repo root
ASPNETCORE_URLS=http://localhost:5000 dotnet run --project ModPackUpdater -- \
  import --file /path/to/pack.(mcpack|mrpack|zip) [--pack my-pack] [--overwrite]
```

Options:
- `-f, --file` required: path to the archive
- `-p, --pack` optional: pack ID override (folder name under packs/); if omitted, importer tries to infer from metadata or file name
- `-y, --overwrite` optional: replace existing pack folder

Importer behavior:
- mcpack: reads Bedrock `manifest.json`
- mrpack: reads `modrinth.index.json` and extracts the `overrides` directory (default `overrides/`)
- zip: trims common top-level directory if present
- Writes `pack.json` with display name, mcVersion, loader info, channel, description
- Skips unsafe paths and anything outside target folder

## HTTP API

Base URL is your server root.

- GET `/health`
  - 200: `{ "status": "ok" }`

- GET `/packs/`
  - 200: `string[]` (pack IDs)

- GET `/packs/{packId}`
  - 200: `{ "packId": "string", "latestVersion": "latest", "versions": ["latest"] }`
  - 404: not found

- GET `/packs/{packId}/manifest`
  - 200: manifest
    - `packId`: string
    - `version`: "latest"
    - `displayName`: string|null
    - `mcVersion`: string|null
    - `loader`: `{ name, version } | null`
    - `files`: array of `{ path, sha256, size }`
    - `mods`: array of `{ path, id|null, version|null, name|null, loader|null }` | null
    - `createdAt`: ISO-8601 string
    - `channel`: string|null
    - `description`: string|null
  - 404: not found

- GET `/packs/{packId}/file?path=relative/path`
  - 200: file bytes (application/octet-stream); Range supported
  - 404: not found

Notes:
- Version is always `latest`.
- Paths must be safe-relative and use `/` separators.
- The server skips symlinked files and files under symlinked directories.

More client guidance and examples: docs/CLIENT.md

## Development

- Run tests:

```bash
dotnet test ModPackUpdater.sln -v minimal
```

- Local URLs:
  - Without config, `dotnet run` will choose a dynamic port; set `ASPNETCORE_URLS` to fix it (example above)
  - Launch profile `http` (Development) opens at `http://localhost:5154` when running from an IDE

- Logging and correlation:
  - Correlation ID header: `X-Correlation-ID` (echoed back; generated if missing)
  - Access logs include method, path, status, latency, host, user-agent, client IP, correlation id

## Security

- If exposing publicly, put behind TLS (reverse proxy) and add authentication at the proxy
- The server validates/sanitizes file paths and rejects symlinks, but you should still host packs from a dedicated directory

## License

GPL-3.0 (see LICENSE.txt)
