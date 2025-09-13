namespace ModPackUpdater.Models;

using System.Text.Json.Serialization; // added

public record LoaderInfo(string Name, string Version);

public record FileEntry(string Path, string Sha256, long Size);

// Basic mod metadata extracted from jar manifests
public record ModInfo(
    string Path,
    string? Id,
    string? Version,
    string? Name = null,
    string? Loader = null
);

public record ModPackManifest(
    string PackId,
    string Version,
    string? DisplayName,
    string? McVersion,
    LoaderInfo? Loader,
    IReadOnlyList<FileEntry> Files,
    DateTimeOffset CreatedAt,
    string? Channel = null,
    string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ModInfo>? Mods = null // omit when null
);

public record PackSummary(string PackId, string LatestVersion, IReadOnlyList<string> Versions);

public record PackMeta(
    string? DisplayName,
    string? McVersion,
    string? LoaderName,
    string? LoaderVersion,
    string? Channel,
    string? Description
);

// Typed DTO for health endpoint to support source-generated JSON context
public record HealthResponse(string status);
