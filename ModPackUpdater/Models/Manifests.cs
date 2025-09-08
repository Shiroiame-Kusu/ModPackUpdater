namespace ModPackUpdater.Models;

public record LoaderInfo(string Name, string Version);

public record FileEntry(string Path, string Sha256, long Size);

public record ModPackManifest(
    string PackId,
    string Version,
    string? DisplayName,
    string? McVersion,
    LoaderInfo? Loader,
    IReadOnlyList<FileEntry> Files,
    DateTimeOffset CreatedAt,
    string? Channel = null,
    string? Description = null
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
