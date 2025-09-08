# ModPackUpdater Server & Deployment Guide

This guide covers setup, configuration, deployment, operations, and troubleshooting for running ModPackUpdater in production.

- App: ASP.NET Core (.NET 9)
- Model: Single-version per pack (always "latest")
- Packs root: PacksRoot (directory that contains `packs/<packId>/`)

## Prerequisites

- .NET SDK/Runtime 9.0 for bare-metal runs
- Linux x64 recommended (works on Windows/macOS too)
- Open a port (default examples use 5000)
- A dedicated folder for packs (readable by the app)

## Install and run (bare metal)

1) Build

```bash
dotnet build ModPackUpdater.sln -v minimal
```

2) Run (fix the port)

```bash
ASPNETCORE_URLS=http://0.0.0.0:5000 \
PacksRoot=/srv/modpacks/packs \
dotnet run --project ModPackUpdater -c Release
```

3) Health

```bash
curl -sS http://localhost:5000/health
```

## Publish and run a binary (dotnet publish)

You can publish a runnable artifact and deploy it without the source tree. Choose one:

- Framework-dependent (requires .NET runtime installed on the host)
- Self-contained single-file (no runtime needed; larger binary)

### Option 1: Framework-dependent

```bash
# Publish
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
dotnet publish ModPackUpdater/ModPackUpdater.csproj \
  -c Release \
  -o /opt/modpackupdater

# Run
ASPNETCORE_URLS=http://0.0.0.0:5000 \
PacksRoot=/srv/modpacks/packs \
/usr/bin/dotnet /opt/modpackupdater/ModPackUpdater.dll
```

### Option 2: Self-contained single-file (Linux x64)

```bash
# Publish self-contained, single-file, ReadyToRun
DOTNET_CLI_TELEMETRY_OPTOUT=1 \
dotnet publish ModPackUpdater/ModPackUpdater.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishReadyToRun=true \
  -o /opt/modpackupdater

# Make executable and run
chmod +x /opt/modpackupdater/ModPackUpdater
ASPNETCORE_URLS=http://0.0.0.0:5000 \
PacksRoot=/srv/modpacks/packs \
/opt/modpackupdater/ModPackUpdater
```

Other RIDs: `win-x64`, `osx-x64`, `osx-arm64`. Replace `linux-x64` above and run the produced `ModPackUpdater(.exe)` accordingly.

Notes:
- Logs are written under `<binaryDir>/logs/` by default (create/write permission required)
- Relative PacksRoot is resolved from the binary’s base directory
- For large packs, prefer a disk with ample space for `PacksRoot`

### Using the importer with a published binary

- Framework-dependent:

```bash
PacksRoot=/srv/modpacks/packs \
/usr/bin/dotnet /opt/modpackupdater/ModPackUpdater.dll \
  import -f /path/to/pack.mrpack -p my-pack -y
```

- Self-contained:

```bash
PacksRoot=/srv/modpacks/packs \
/opt/modpackupdater/ModPackUpdater \
  import -f /path/to/pack.mrpack -p my-pack -y
```

## Configuration

- PacksRoot: root directory containing pack folders. Accepts relative or absolute.
  - appsettings.Development.json sets `"PacksRoot": "packs"` by default
  - You can override via environment variable `PacksRoot`
  - Relative paths are resolved from `AppContext.BaseDirectory` (the app’s working dir)
- Logging: configured via Serilog in `appsettings.json`
  - Console output
  - Files under `logs/` (daily rolling):
    - `app-*.log` application logs
    - `access-*.log` HTTP access logs
- Correlation ID header: `X-Correlation-ID` is echoed (or auto-generated); included in logs

Example JSON override:

```json
{
  "PacksRoot": "/srv/modpacks/packs"
}
```

## Packs layout

Single-version model; each pack lives at `PacksRoot/<packId>/` and always represents the latest.

```
/srv/modpacks/packs/
  my-pack/
    pack.json
    mods/
    config/
    resourcepacks/
```

Notes:
- Hashing ignores `pack.json`, `.DS_Store`, `Thumbs.db`, and any dot-directories (e.g., `.git/`)
- Symlinks are skipped (files or ancestor directories)

## Importing packs (admin CLI)

The same binary includes a simple importer:

```bash
# Basic
PacksRoot=/srv/modpacks/packs \
dotnet run --project ModPackUpdater -- \
  import --file /path/to/pack.(mcpack|mrpack|zip) \
  [--pack my-pack] [--overwrite]
```

Behavior:
- Attempts to parse metadata from archive (Bedrock `manifest.json`, Modrinth `modrinth.index.json`)
- For `.mrpack`, extracts only the `overrides/` directory (default name) into the pack folder
- Normalizes paths, trims common top-level dir in generic zips, rejects unsafe paths
- Writes a unified `pack.json` in the target pack folder
- Single-version: always imports to `packs/<packId>/` (replaces contents when `--overwrite`)

Common examples:

```bash
# Infer pack id from archive metadata or filename
PacksRoot=/srv/modpacks/packs dotnet run --project ModPackUpdater -- import -f ./my-pack-1.0.0.mrpack

# Force a specific pack id and overwrite existing contents
PacksRoot=/srv/modpacks/packs dotnet run --project ModPackUpdater -- import -f ./pack.zip -p my-pack -y
```

Return codes: 0 success; non-zero indicates an error (see stderr messages).

## HTTP API (admin reference)

- GET /health → `{ "status": "ok" }`
- GET /packs/ → `string[]` of pack IDs (folders under PacksRoot)
- GET /packs/{id} → summary `{ packId, latestVersion: "latest", versions: ["latest"] }`
- GET /packs/{id}/manifest → manifest `{ files: [{ path, sha256, size }], ... }`
- GET /packs/{id}/file?path=relative/path → file bytes (Range supported)

See docs/CLIENT.md for client-side diff and usage patterns.

## Production deployment

### Option A: systemd service (bare metal)

1) Publish self-contained or framework-dependent build (recommended: publish for smaller runtime without SDK):

```bash
dotnet publish ModPackUpdater/ModPackUpdater.csproj -c Release -o /opt/modpackupdater
```

2) Create a systemd unit `/etc/systemd/system/modpackupdater.service`:

```
[Unit]
Description=ModPackUpdater
After=network.target

[Service]
WorkingDirectory=/opt/modpackupdater
# If framework-dependent publish:
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=PacksRoot=/srv/modpacks/packs
ExecStart=/usr/bin/dotnet /opt/modpackupdater/ModPackUpdater.dll
# If self-contained single-file publish, use instead:
# ExecStart=/opt/modpackupdater/ModPackUpdater
Restart=on-failure
User=modpacks
Group=modpacks

[Install]
WantedBy=multi-user.target
```

3) Reload and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now modpackupdater
```

### Option B: Docker

Use a multi-stage .NET image. Example Dockerfile:

```
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ModPackUpdater.sln ./
COPY ModPackUpdater/ ./ModPackUpdater/
COPY ModPackUpdater.Tests/ ./ModPackUpdater.Tests/
RUN dotnet publish ModPackUpdater/ModPackUpdater.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PacksRoot=/data/packs
VOLUME ["/data", "/app/logs"]
EXPOSE 8080
ENTRYPOINT ["dotnet", "ModPackUpdater.dll"]
```

Build and run:

```bash
docker build -t modpackupdater .

docker run -d \
  --name modpackupdater \
  -p 8080:8080 \
  -v /srv/modpacks:/data \
  -e PacksRoot=/data/packs \
  modpackupdater
```

Importer inside the container (bind-mount archive):

```bash
docker run --rm \
  -v /srv/modpacks:/data \
  -v $(pwd)/packs:/imports \
  -e PacksRoot=/data/packs \
  modpackupdater \
  import -f /imports/my-pack.mrpack -p my-pack -y
```

### Reverse proxy (TLS, auth, caching)

Place a proxy in front for HTTPS and optional auth/rate limiting.

Nginx example:

```
server {
    listen 80;
    server_name packs.example.com;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Correlation-ID $request_id;
    }
}
```

Caddy example:

```
packs.example.com {
    reverse_proxy 127.0.0.1:5000
    header_up X-Correlation-ID {http.request.id}
}
```

Optional: add Basic/OAuth at the proxy if you need authentication.

## Operations

- Logs: `logs/app-*.log` and `logs/access-*.log`; rotate daily, 7 files retained by default
- Health: `/health` should return `{"status":"ok"}`
- Backups: back up `PacksRoot/` (pack data) and optionally `logs/`
- Upgrades: stop app/container, deploy new build, start; no schema/state migrations
- Disk usage: packs can be large; ensure enough space under `PacksRoot`

## Troubleshooting

- 404 on `/packs/{id}`: the folder `PacksRoot/{id}` doesn’t exist; check spelling/case
- 404 on `/file?path=...`:
  - Path must be relative, use `/` separators, no `..`; ensure the file exists under the pack folder
  - Symlinks are rejected
- Import errors:
  - "unsupported file extension": use `.mcpack`, `.mrpack`, or `.zip`
  - "could not determine pack id": pass `--pack` or rename archive to `id-version.ext`
  - "archive is empty": verify the file contents
- Logs missing: ensure the process can create `./logs/` (write permissions)

## Security notes

- Always run behind TLS (reverse proxy)
- Prefer a dedicated, locked-down PacksRoot directory; don’t share with unrelated files
- The server sanitizes paths and rejects symlinks, but avoid importing untrusted packs blindly

## FAQ

- Q: Can I host multiple versions per pack?
  - A: Not currently. The server models a single-version "latest" per pack.
- Q: Can I require authentication?
  - A: Add auth at your reverse proxy (e.g., Basic, OAuth2). The service itself is unauthenticated by default.
- Q: How big can packs be?
  - A: There’s no hard-coded limit; bandwidth and disk space are the primary constraints.

## Useful cURL

```bash
# List packs
curl -sS http://localhost:5000/packs/ | jq .

# Manifest
curl -sS http://localhost:5000/packs/my-pack/manifest | jq .

# Download a file
curl -sS -OJ "http://localhost:5000/packs/my-pack/file?path=mods/example.jar"
```
