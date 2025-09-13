using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;
using System.IO.Compression;
using System.Linq;

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

    [Fact]
    public async Task Manifest_Is_Cached_Between_Quick_Calls()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory; // dispose later
        try
        {
            var json1 = await client.GetStringAsync("/packs/example-pack/manifest");
            // tiny delay to avoid same-request artifacts, but keep within cache window
            await Task.Delay(50);
            var json2 = await client.GetStringAsync("/packs/example-pack/manifest");

            using var doc1 = JsonDocument.Parse(json1);
            using var doc2 = JsonDocument.Parse(json2);
            var created1 = doc1.RootElement.GetProperty("createdAt").GetString();
            var created2 = doc2.RootElement.GetProperty("createdAt").GetString();

            Assert.False(string.IsNullOrEmpty(created1));
            Assert.False(string.IsNullOrEmpty(created2));
            Assert.Equal(created1, created2); // same cached manifest instance/materialized value
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Manifest_Includes_Mod_Versions_When_Present()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory; // dispose later
        try
        {
            // Create a valid mod JAR with fabric.mod.json before requesting mods
            var jarPath = Path.Combine(root, "example-pack", "mods", "fabric-example.jar");
            using (var fs = File.Create(jarPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("fabric.mod.json");
                await using var es = entry.Open();
                var content = """
                {
                  "schemaVersion": 1,
                  "id": "fabric_example",
                  "version": "1.2.3",
                  "name": "Fabric Example"
                }
                """;
                var bytes = Encoding.UTF8.GetBytes(content);
                await es.WriteAsync(bytes, 0, bytes.Length);
            }

            // Request mods endpoint and verify list contains our mod with version
            var modsText = await client.GetStringAsync("/packs/example-pack/mods");
            _output.WriteLine($"mods: {modsText}");
            using var doc = JsonDocument.Parse(modsText);
            var modsArr = doc.RootElement.EnumerateArray().ToArray();
            Assert.Contains(modsArr, m => m.GetProperty("path").GetString() == "mods/fabric-example.jar");
            var mod = modsArr.First(m => m.GetProperty("path").GetString() == "mods/fabric-example.jar");
            Assert.Equal("fabric_example", mod.GetProperty("id").GetString());
            Assert.Equal("1.2.3", mod.GetProperty("version").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Mods_Endpoint_Extracts_Version_From_PomProperties()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory;
        try
        {
            var jarPath = Path.Combine(root, "example-pack", "mods", "example-lib.jar");
            using (var fs = File.Create(jarPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("META-INF/maven/com.example/example-lib/pom.properties");
                await using var es = entry.Open();
                var content = """
                version=9.9.9
                artifactId=example-lib
                name=Example Lib
                """;
                var bytes = Encoding.UTF8.GetBytes(content);
                await es.WriteAsync(bytes, 0, bytes.Length);
            }

            var modsText = await client.GetStringAsync("/packs/example-pack/mods");
            using var doc = JsonDocument.Parse(modsText);
            var modsArr = doc.RootElement.EnumerateArray().ToArray();
            var mod = modsArr.FirstOrDefault(m => m.GetProperty("path").GetString() == "mods/example-lib.jar");
            Assert.True(mod.ValueKind != JsonValueKind.Undefined);
            Assert.Equal("9.9.9", mod.GetProperty("version").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Mods_Endpoint_Extracts_From_mcmod_info()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory;
        try
        {
            var jarPath = Path.Combine(root, "example-pack", "mods", "old-forge-mod.jar");
            using (var fs = File.Create(jarPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("mcmod.info");
                await using var es = entry.Open();
                var content = """
                [
                  {
                    "modid": "oldmod",
                    "name": "Old Forge Mod",
                    "version": "0.4.2"
                  }
                ]
                """;
                var bytes = Encoding.UTF8.GetBytes(content);
                await es.WriteAsync(bytes, 0, bytes.Length);
            }

            var modsText = await client.GetStringAsync("/packs/example-pack/mods");
            using var doc = JsonDocument.Parse(modsText);
            var modsArr = doc.RootElement.EnumerateArray().ToArray();
            var mod = modsArr.FirstOrDefault(m => m.GetProperty("path").GetString() == "mods/old-forge-mod.jar");
            Assert.True(mod.ValueKind != JsonValueKind.Undefined);
            Assert.Equal("0.4.2", mod.GetProperty("version").GetString());
            Assert.Equal("oldmod", mod.GetProperty("id").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Mods_Endpoint_Extracts_From_NeoForge_Toml()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory;
        try
        {
            var jarPath = Path.Combine(root, "example-pack", "mods", "neoforge-mod.jar");
            using (var fs = File.Create(jarPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("META-INF/neoforge.mods.toml");
                await using var es = entry.Open();
                var content = """
                modLoader="javafml"
                loaderVersion="[2,)"
                [[mods]]
                modId="neotest"
                version="2.3.4"
                displayName="Neo Test Mod"
                """;
                var bytes = Encoding.UTF8.GetBytes(content);
                await es.WriteAsync(bytes, 0, bytes.Length);
            }

            var modsText = await client.GetStringAsync("/packs/example-pack/mods");
            using var doc = JsonDocument.Parse(modsText);
            var modsArr = doc.RootElement.EnumerateArray().ToArray();
            var mod = modsArr.FirstOrDefault(m => m.GetProperty("path").GetString() == "mods/neoforge-mod.jar");
            Assert.True(mod.ValueKind != JsonValueKind.Undefined);
            Assert.Equal("neotest", mod.GetProperty("id").GetString());
            Assert.Equal("2.3.4", mod.GetProperty("version").GetString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Manifest_Does_Not_Include_Mods_List_After_Split()
    {
        var (factory, client, root) = await CreateAppAsync();
        await using var _ = factory;
        try
        {
            var manifestText = await client.GetStringAsync("/packs/example-pack/manifest");
            using var doc = JsonDocument.Parse(manifestText);
            var rootEl = doc.RootElement;
            Assert.False(rootEl.TryGetProperty("mods", out _)); // property should be omitted entirely now
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
