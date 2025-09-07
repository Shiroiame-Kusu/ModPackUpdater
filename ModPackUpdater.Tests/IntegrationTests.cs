using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace ModPackUpdater.Tests;

public class IntegrationTests
{
    private readonly ITestOutputHelper _output;
    public IntegrationTests(ITestOutputHelper output) => _output = output;

    private sealed class TestAppFactory : WebApplicationFactory<ModPackUpdater.Program>
    {
        private readonly string _packsRoot;
        public TestAppFactory(string packsRoot) => _packsRoot = packsRoot;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["PacksRoot"] = _packsRoot
                };
                cfg.AddInMemoryCollection(dict!);
            });
        }
    }

    private static async Task<(TestAppFactory factory, HttpClient client, string root)> CreateAppAsync()
    {
        var root = Directory.CreateTempSubdirectory("packs-test-").FullName;
        // Seed a sample pack
        var packDir = Path.Combine(root, "example-pack", "1.0.0");
        Directory.CreateDirectory(Path.Combine(packDir, "mods"));
        Directory.CreateDirectory(Path.Combine(packDir, "config"));
        await File.WriteAllTextAsync(Path.Combine(packDir, "pack.json"),
            """
            {
              "displayName": "Example Pack",
              "mcVersion": "1.20.1",
              "loaderName": "fabric",
              "loaderVersion": "0.15.11",
              "channel": "stable",
              "description": "Sample pack for tests"
            }
            """
        );
        await File.WriteAllTextAsync(Path.Combine(packDir, "mods", "example.jar"), "dummy jar bytes");
        await File.WriteAllTextAsync(Path.Combine(packDir, "config", "some.cfg"), "key=value");

        var factory = new TestAppFactory(root);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        return (factory, client, root);
    }

    [Fact]
    public async Task Health_And_Basic_Flow_Works()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory; // dispose later
        try
        {
            // Health
            var healthResp = await client.GetAsync("/health");
            var healthText = await healthResp.Content.ReadAsStringAsync();
            _output.WriteLine($"/health: {healthText}");
            healthResp.EnsureSuccessStatusCode();
            using var healthDoc = JsonDocument.Parse(healthText);
            var health = healthDoc.RootElement;
            Assert.True(health.TryGetProperty("status", out var status));
            Assert.Equal("ok", status.GetString());

            // List packs
            var packsResp = await client.GetAsync("/packs/");
            var packsText = await packsResp.Content.ReadAsStringAsync();
            _output.WriteLine($"/packs: {packsText}");
            packsResp.EnsureSuccessStatusCode();
            var packs = JsonSerializer.Deserialize<string[]>(packsText)!;
            Assert.Contains("example-pack", packs);

            // Summary
            var summaryText = await client.GetStringAsync("/packs/example-pack");
            _output.WriteLine($"/packs/example-pack: {summaryText}");
            using var summaryDoc = JsonDocument.Parse(summaryText);
            var summary = summaryDoc.RootElement;
            Assert.Equal("example-pack", summary.GetProperty("packId").GetString());
            Assert.Equal("1.0.0", summary.GetProperty("latestVersion").GetString());

            // Manifest
            var manifestText = await client.GetStringAsync("/packs/example-pack/manifest");
            _output.WriteLine($"/packs/example-pack/manifest: {manifestText}");
            using var manifestDoc = JsonDocument.Parse(manifestText);
            var manifest = manifestDoc.RootElement;
            Assert.Equal("example-pack", manifest.GetProperty("packId").GetString());
            Assert.Equal("1.0.0", manifest.GetProperty("version").GetString());
            var files = manifest.GetProperty("files");
            Assert.True(files.GetArrayLength() >= 2);

            // Diff (client has one file with wrong hash; expects Update + Add for the other)
            var diffReq = JsonContent.Create(new
            {
                files = new[]
                {
                    new { path = "mods/example.jar", sha256 = "deadbeef", size = 123 }
                }
            });
            var diffResp = await client.PostAsync("/packs/example-pack/diff", diffReq);
            var diffText = await diffResp.Content.ReadAsStringAsync();
            _output.WriteLine($"/packs/example-pack/diff: {diffText}");
            diffResp.EnsureSuccessStatusCode();
            using var diffDoc = JsonDocument.Parse(diffText);
            var diff = diffDoc.RootElement;
            var ops = diff.GetProperty("operations");
            Assert.True(ops.GetArrayLength() >= 2);
            var opPaths = ops.EnumerateArray().Select(o => o.GetProperty("path").GetString()).ToArray();
            Assert.Contains("mods/example.jar", opPaths);
            Assert.Contains("config/some.cfg", opPaths);

            // Single file download
            var text = await client.GetStringAsync("/packs/example-pack/file?path=config/some.cfg");
            _output.WriteLine($"download file content: {text}");
            Assert.Equal("key=value", text);

            // Bundle download and inspect zip entries
            var bundleReq = JsonContent.Create(new { paths = new[] { "mods/example.jar", "config/some.cfg" } });
            var bundleResp = await client.PostAsync("/packs/example-pack/bundle", bundleReq);
            _output.WriteLine($"bundle status: {(int)bundleResp.StatusCode}");
            bundleResp.EnsureSuccessStatusCode();
            await using var bundleStream = await bundleResp.Content.ReadAsStreamAsync();
            using var zip = new ZipArchive(bundleStream, ZipArchiveMode.Read, leaveOpen: false);
            var names = zip.Entries.Select(e => e.FullName).ToArray();
            _output.WriteLine($"bundle entries: {string.Join(", ", names)}");
            Assert.Contains("mods/example.jar", names);
            Assert.Contains("config/some.cfg", names);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
