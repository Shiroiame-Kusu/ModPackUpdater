namespace ModPackUpdater.Services.Parsers;

public sealed record ParsedPack(
    string PackId,
    string Version,
    string? DisplayName = null,
    string? Description = null,
    string? McVersion = null,
    string? LoaderName = null,
    string? LoaderVersion = null,
    string? Channel = null,
    string? OverridesDirName = null
);

