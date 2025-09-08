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
        // Seed a sample pack (single version: latest)
        var packDir = Path.Combine(root, "example-pack");
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
            Assert.Equal("latest", summary.GetProperty("latestVersion").GetString());

            // Manifest
            var manifestText = await client.GetStringAsync("/packs/example-pack/manifest");
            _output.WriteLine($"/packs/example-pack/manifest: {manifestText}");
            using var manifestDoc = JsonDocument.Parse(manifestText);
            var manifest = manifestDoc.RootElement;
            Assert.Equal("example-pack", manifest.GetProperty("packId").GetString());
            Assert.Equal("latest", manifest.GetProperty("version").GetString());
            var files = manifest.GetProperty("files");
            Assert.True(files.GetArrayLength() >= 2);

            // Single file download
            var text = await client.GetStringAsync("/packs/example-pack/file?path=config/some.cfg");
            _output.WriteLine($"download file content: {text}");
            Assert.Equal("key=value", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
