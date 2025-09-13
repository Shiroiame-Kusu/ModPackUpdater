using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using ModPackUpdater.Models;
using ModPackUpdater.Services;
using Serilog;
using Serilog.Events;
using ModPackUpdater.Filters;
using Microsoft.Extensions.Configuration; // added for CLI config loading
using Microsoft.Extensions.Caching.Memory; // added for in-memory cache

namespace ModPackUpdater;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Simple CLI: `import` subcommand to create packs from a .mcpack/.zip
        if (args.Length > 0 && string.Equals(args[0], "import", StringComparison.OrdinalIgnoreCase))
        {
            var (file, packId, version, overwrite, autoDownload, showHelp) = ParseImportArgs(args);
            if (showHelp || string.IsNullOrWhiteSpace(file))
            {
                PrintImportHelp();
                Environment.Exit(showHelp ? 0 : 2);
                return; // just in case
            }

            // Load configuration similarly to the web app to resolve PacksRoot
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile($"appsettings.{env}.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            var cfgRoot = config["PacksRoot"];
            var packsRoot = !string.IsNullOrWhiteSpace(cfgRoot)
                ? (Path.IsPathRooted(cfgRoot) ? cfgRoot : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, cfgRoot)))
                : Path.Combine(AppContext.BaseDirectory, "packs");

            // Delegate inference to importer; pass through optional overrides if provided
            var code = await PackImportService.Import(packsRoot, new PackImportService.ImportOptions(file!, packId, version, overwrite, autoDownload));
            Environment.Exit(code);
            return;
        }

        var builder = WebApplication.CreateSlimBuilder(args);

        // Ensure logs directory exists early
        try { Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs")); } catch { /* ignore */ }

        // Serilog configuration from appsettings
        builder.Host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .Enrich.FromLogContext();
        });

        // Caching
        builder.Services.AddMemoryCache(options =>
        {
            // Small size limit; entries are 1 size each
            options.SizeLimit = 1024; // up to 1024 cached manifests
        });

        // Services
        builder.Services.AddSingleton<PackService>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var cfgRoot = cfg["PacksRoot"];
            var root = !string.IsNullOrWhiteSpace(cfgRoot)
                ? (Path.IsPathRooted(cfgRoot) ? cfgRoot : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, cfgRoot)))
                : Path.Combine(AppContext.BaseDirectory, "packs");
            Log.Information("PacksRoot resolved to {PacksRoot}", root);

            // Concurrency settings
            int? hashConc = cfg.GetValue<int?>("Concurrency:Hash");
            int? modConc = cfg.GetValue<int?>("Concurrency:ModExtract");

            return new PackService(
                root,
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PackService>>() ,
                hashConc,
                modConc
            );
        });
        builder.Services.AddControllers(options =>
        {
            options.Filters.Add<ComponentLoggingFilter>();
        });

        var app = builder.Build();

        // Optional: warm up manifests at startup to pre-populate cache and watchers
        var warmupEnabled = app.Configuration.GetValue<bool?>("ManifestWarmup:Enabled") ?? true;
        var warmupBlock = app.Configuration.GetValue<bool?>("ManifestWarmup:BlockOnStartup") ?? false;
        var warmupMaxConc = app.Configuration.GetValue<int?>("ManifestWarmup:MaxConcurrency") ?? Math.Max(2, Environment.ProcessorCount / 2);
        if (warmupEnabled)
        {
            if (warmupBlock)
            {
                try
                {
                    await WarmupManifestsAsync(app.Services, warmupMaxConc, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Startup manifest warmup failed (continuing startup)");
                }
            }
            else
            {
                app.Lifetime.ApplicationStarted.Register(() =>
                {
                    _ = Task.Run(async () =>
                    {
                        try { await WarmupManifestsAsync(app.Services, warmupMaxConc, CancellationToken.None); }
                        catch (Exception ex) { Log.Warning(ex, "Background manifest warmup failed"); }
                    });
                });
            }
        }

        // Correlation + LogContext enrichment must come before request logging
        app.UseMiddleware<Middleware.CorrelationLoggingMiddleware>();

        // HTTP request access logging
        app.UseSerilogRequestLogging(options =>
        {
            // Customize message and enrich diagnostic context
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.EnrichDiagnosticContext = (diag, httpContext) =>
            {
                var req = httpContext.Request;
                var scheme = req.Scheme;
                if (req.Headers.TryGetValue("X-Forwarded-Proto", out var xfProto) && !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(xfProto))
                {
                    var proto = xfProto.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    scheme = string.IsNullOrWhiteSpace(proto) ? scheme : proto;
                }

                var host = req.Host.Value;
                if (req.Headers.TryGetValue("X-Forwarded-Host", out var xfHost) && !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(xfHost))
                {
                    var fhost = xfHost.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    host = string.IsNullOrWhiteSpace(fhost) ? host : fhost;
                }

                var path = req.Path.HasValue ? req.Path.Value : string.Empty;
                var query = req.QueryString.HasValue ? req.QueryString.Value : string.Empty;
                var fullUrl = string.Concat(scheme, "://", host, path, query);

                // Prefer X-Forwarded-For (first IP) when present, else RemoteIpAddress
                string? clientIp = null;
                if (req.Headers.TryGetValue("X-Forwarded-For", out var xff) && !Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(xff))
                {
                    var first = xff.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    clientIp = string.IsNullOrWhiteSpace(first) ? null : first;
                }
                clientIp ??= httpContext.Connection.RemoteIpAddress?.ToString();

                diag.Set("RequestHost", host);
                diag.Set("RequestScheme", scheme);
                diag.Set("UserAgent", req.Headers.UserAgent.ToString());
                diag.Set("QueryString", req.QueryString.HasValue ? req.QueryString.Value : "");
                diag.Set("RemoteIpAddress", httpContext.Connection.RemoteIpAddress?.ToString());
                diag.Set("ClientIp", clientIp ?? "");
                diag.Set("FullUrl", fullUrl);
                if (httpContext.Items.TryGetValue("CorrelationId", out var corr) && corr is string s)
                    diag.Set("CorrelationId", s);
            };
            // Leave default GetLevel (Info, Error on 5xx)
        });

        app.MapControllers();

        app.Run();
    }

    private static async Task WarmupManifestsAsync(IServiceProvider services, int maxConcurrency, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PackService>();
        var ids = svc.GetPackIds().ToList();
        if (ids.Count == 0)
        {
            Log.Information("No packs found for warmup");
            return;
        }
        Log.Information("Warming up manifests for {Count} pack(s) with concurrency {Conc}", ids.Count, maxConcurrency);
        var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = ids.Select(async id =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var manifest = await svc.TryGetManifestAsync(id, "latest", ct);
                sw.Stop();
                if (manifest != null)
                    Log.Information("Warmup built manifest for {PackId} in {Ms} ms", id, sw.ElapsedMilliseconds);
                else
                    Log.Warning("Warmup failed to build manifest for {PackId}", id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Warmup error for pack {PackId}", id);
            }
            finally
            {
                try { sem.Release(); } catch { }
            }
        });
        await Task.WhenAll(tasks);
        Log.Information("Manifest warmup complete");
    }

    private static (string? file, string? packId, string? version, bool overwrite, bool autoDownload, bool help) ParseImportArgs(string[] args)
    {
        string? file = null, packId = null, version = null; bool overwrite = false, help = false; bool auto = true;
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--file":
                case "-f":
                    if (i + 1 < args.Length) file = args[++i];
                    break;
                case "--pack":
                case "-p":
                    if (i + 1 < args.Length) packId = args[++i];
                    break;
                case "--version":
                case "-v":
                    if (i + 1 < args.Length) version = args[++i]; // accepted but ignored in single-version mode
                    break;
                case "--overwrite":
                case "-y":
                    overwrite = true;
                    break;
                case "--no-download":
                case "--no-dl":
                case "-n":
                    auto = false;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                default:
                    // Allow bare argument as file path if not set yet
                    if (string.IsNullOrEmpty(file)) file = a;
                    break;
            }
        }
        return (file, packId, version, overwrite, auto, help);
    }

    private static void PrintImportHelp()
    {
        Console.WriteLine(@"Usage:
  ModPackUpdater import --file <path.(mcpack|mrpack|zip)> [--pack <id>] [--overwrite] [--no-download]

Imports the given archive into the packs directory configured by PacksRoot (or ./packs by default).
This app uses a single-version model; each pack lives at packs/<id>/ and always represents 'latest'.
The importer reads name and metadata from inside the archive when possible (Bedrock manifest.json, CurseForge manifest.json, Modrinth modrinth.index.json).
If pack id is still unknown, it falls back to the filename (before the last '-').

Options:
  -f, --file         Path to .mcpack, .mrpack, or .zip
  -p, --pack         Pack ID override (folder name under packs/)
  -y, --overwrite    Replace the existing pack folder if it exists
  -n, --no-download  Disable auto-download of remote files (e.g., modrinth.index.json files)
  -h, --help         Show this help
");
    }
}

// JSON source generation removed; relying on default reflection-based System.Text.Json
