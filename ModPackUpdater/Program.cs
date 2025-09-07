using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using ModPackUpdater.Models;
using ModPackUpdater.Services;

namespace ModPackUpdater;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        // Services
        builder.Services.AddSingleton<PackService>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var root = cfg["PacksRoot"] ?? Path.Combine(AppContext.BaseDirectory, "packs");
            return new PackService(root);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        var app = builder.Build();

        // Health
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        var packs = app.MapGroup("/packs");

        // List packs
        packs.MapGet("/", (PackService svc) => Results.Ok(svc.GetPackIds()));

        // Pack summary with versions
        packs.MapGet("/{packId}", (string packId, PackService svc) =>
        {
            var versions = svc.GetVersions(packId);
            if (versions.Count == 0) return Results.NotFound();
            var latest = versions.First();
            var summary = new PackSummary(packId, latest, versions);
            return Results.Ok(summary);
        });

        // Manifest of a specific or latest version
        packs.MapGet("/{packId}/manifest", async Task<Results<Ok<ModPackManifest>, NotFound>> (string packId, string? version, PackService svc, CancellationToken ct) =>
        {
            version ??= svc.GetLatestVersion(packId);
            if (string.IsNullOrWhiteSpace(version)) return TypedResults.NotFound();
            var manifest = await svc.TryGetManifestAsync(packId, version!, ct);
            return manifest is null ? TypedResults.NotFound() : TypedResults.Ok(manifest);
        });

        // Diff between client file list and server manifest
        packs.MapPost("/{packId}/diff", async Task<Results<Ok<DiffResponse>, NotFound>> (string packId, string? version, DiffRequest req, PackService svc, CancellationToken ct) =>
        {
            version ??= svc.GetLatestVersion(packId);
            if (string.IsNullOrWhiteSpace(version)) return TypedResults.NotFound();
            var diff = await svc.DiffAsync(packId, version!, req, ct);
            return diff is null ? TypedResults.NotFound() : TypedResults.Ok(diff);
        });

        // Download a single file by relative path (query: path, version)
        packs.MapGet("/{packId}/file", (string packId, string? version, string path, PackService svc) =>
        {
            version ??= svc.GetLatestVersion(packId);
            if (string.IsNullOrWhiteSpace(version)) return Results.NotFound();
            if (!svc.TryResolveFile(packId, version!, path, out var full)) return Results.NotFound();
            var fileName = System.IO.Path.GetFileName(path);
            return Results.File(full!, contentType: "application/octet-stream", fileDownloadName: fileName, enableRangeProcessing: true);
        });

        // Download a bundle zip for a set of relative paths
        packs.MapPost("/{packId}/bundle", async Task<IResult> (HttpContext ctx, string packId, string? version, BundleRequest req, PackService svc, CancellationToken ct) =>
        {
            version ??= svc.GetLatestVersion(packId);
            if (string.IsNullOrWhiteSpace(version)) return Results.NotFound();

            ctx.Response.ContentType = "application/zip";
            var fileName = $"{packId}-{version}-bundle.zip";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

            await svc.WriteBundleAsync(ctx.Response.Body, packId, version!, req.Paths ?? Array.Empty<string>(), ct);
            return Results.Empty;
        });

        app.Run();
    }
}

// JSON source generation context for AOT-friendly serialization
[JsonSerializable(typeof(IEnumerable<string>))]
[JsonSerializable(typeof(PackSummary))]
[JsonSerializable(typeof(ModPackManifest))]
[JsonSerializable(typeof(DiffRequest))]
[JsonSerializable(typeof(DiffResponse))]
[JsonSerializable(typeof(BundleRequest))]
[JsonSerializable(typeof(PackMeta))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}