using System.Net.Http.Json;
using Maki.Api.Services;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maki.Api.Tests;

/// <summary>
/// The import endpoint through the real host: folders run side by side, each in a scope of its own,
/// and that scope has to carry the caller. The caller here is granted one root folder only, so an
/// import scope that came up anonymous would answer "root folder not found" for every item.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class LibraryImportParallelTests : IDisposable
{
    private readonly string _configDir;
    private readonly string _root;
    private readonly string? _previousConfigDir;

    public LibraryImportParallelTests()
    {
        EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-importparallel-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_configDir, "library");
        Directory.CreateDirectory(_root);
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // A file the host still holds open. The directory is under TEMP either way.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Import_runs_folders_in_parallel_as_the_caller_and_keeps_one_series_per_id()
    {
        var provider = new SlowProvider();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IMetadataProvider>();
            s.AddSingleton<IMetadataProvider>(provider);
            s.RemoveAll<ISource>();
        }));
        using var client = factory.CreateClient();

        int rootId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var root = new Maki.Core.Entities.RootFolder { Path = _root };
            db.RootFolders.Add(root);
            var user = new MakiUser
            {
                UserName = "importer",
                NormalizedUserName = "IMPORTER",
                Permissions = Maki.Core.Security.MakiPermission.ImportLibrary,
                AllRootFolders = false,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.UserRootFolders.Add(new UserRootFolder { UserId = user.Id, RootFolderId = root.Id });
            const string secret = "import-parallel-test-key";
            db.UserApiKeys.Add(new UserApiKey
            {
                UserId = user.Id,
                Name = "import test",
                KeyHash = Maki.Api.Auth.ApiKeyCrypto.Hash(secret),
                Prefix = "import",
                Scope = UserApiKeyScope.Full,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
            client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        }

        string[] folders = ["Alpha (Digital)", "Beta", "Gamma", "Epsilon", "Delta"];
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
        }

        var response = await client.PostAsJsonAsync("/api/v1/libraryimport/import", new
        {
            rootFolderId = rootId,
            items = new[]
            {
                new { folderName = "Alpha (Digital)", metadataProviderId = "1" },
                new { folderName = "Beta", metadataProviderId = "2" },
                new { folderName = "Gamma", metadataProviderId = "3" },
                new { folderName = "Epsilon", metadataProviderId = "4" },
                // Same series as Alpha: must run after it and land in the series Alpha created.
                new { folderName = "Delta", metadataProviderId = "1" },
            },
            updateComicInfo = false,
        });
        response.EnsureSuccessStatusCode();
        var results = await response.Content.ReadFromJsonAsync<List<ImportResult>>();

        Assert.NotNull(results);
        Assert.Equal(folders, results.Select(r => r.FolderName));
        Assert.All(results, r => Assert.True(r.Success, r.Error));
        Assert.True(provider.MaxInFlight > 1, $"imports never overlapped (max in flight {provider.MaxInFlight})");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var series = await db.Series.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(4, series.Count);
            Assert.Single(series, s => s.MangaBakaId == 1);
        }
    }

    private static void EnsureWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var api = Path.Combine(dir.FullName, "src", "Maki.Api");
            if (File.Exists(Path.Combine(api, "Maki.Api.csproj")))
            {
                Directory.CreateDirectory(Path.Combine(api, "wwwroot"));
                return;
            }
        }
    }

    private sealed class SlowProvider : IMetadataProvider
    {
        private int _inFlight;
        private int _maxInFlight;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public async Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) &&
                   Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(300, ct);
                return new SeriesMetadata
                {
                    ProviderId = providerId,
                    Title = $"Series {providerId}",
                    MangaBakaId = int.Parse(providerId),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
