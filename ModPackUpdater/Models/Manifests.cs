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

public record ClientFile(string Path, string? Sha256, long? Size);

public record DiffRequest(IReadOnlyList<ClientFile>? Files);

public enum FileOp { Add, Update, Delete, Keep }

public record Operation(string Path, FileOp Op, string? Sha256 = null, long? Size = null);

public record DiffResponse(string PackId, string Version, IReadOnlyList<Operation> Operations);

public record PackSummary(string PackId, string LatestVersion, IReadOnlyList<string> Versions);

public record BundleRequest(IReadOnlyList<string>? Paths);

public record PackMeta(
    string? DisplayName,
    string? McVersion,
    string? LoaderName,
    string? LoaderVersion,
    string? Channel,
    string? Description
);
