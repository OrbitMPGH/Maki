using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// KOReader's sync client against the real host: routing, model binding, <c>[AllowAnonymous]</c>
/// under the fail-closed fallback policy and the antiforgery filter all sit between it and the
/// controller, and the controller tests build it by hand.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class KoreaderSyncPipelineTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public KoreaderSyncPipelineTests()
    {
        CookieSession.EnsureWebRoot();
        _configDir = Path.Combine(Path.GetTempPath(), "maki-kosync-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
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
        }
    }

    /// <summary>A request with the headers KOReader's spore client sends.</summary>
    private static HttpRequestMessage Koreader(HttpMethod method, string path, string? user, string? json = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1/kosync/" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.koreader.v1+json"));
        if (user is not null)
        {
            request.Headers.Add("x-auth-user", user);
            request.Headers.Add("x-auth-key", Convert.ToHexStringLower(MD5.HashData("anything"u8)));
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task Koreader_logs_in_pushes_and_pulls_through_the_real_pipeline()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var seeded = await ReadingAppSeed.SeedAsync(factory, Path.Combine(_configDir, "library"));
        string digest;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            digest = KoreaderDigest.PartialMd5(seeded.Path);
            db.KoreaderDocuments.Add(new KoreaderDocument
            {
                ChapterFileId = seeded.ChapterFileId, Kind = KoreaderDigestKind.Binary, Digest = digest,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Koreader(HttpMethod.Get, "healthcheck", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Koreader(HttpMethod.Get, "users/auth", seeded.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Koreader(HttpMethod.Get, "users/auth", "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Koreader(HttpMethod.Post, "users/create", null,
            JsonSerializer.Serialize(new { username = seeded.Token, password = "x" })))).StatusCode);

        // A paged document's progress is a page number, sent as a JSON number.
        var body = $$"""{"document":"{{digest}}","progress":2,"percentage":1,"device":"Kobo","device_id":"KOBO-1"}""";
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Koreader(HttpMethod.Put, "syncs/progress", null, body))).StatusCode);
        var pushed = await client.SendAsync(Koreader(HttpMethod.Put, "syncs/progress", seeded.Token, body));
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        using (var json = JsonDocument.Parse(await pushed.Content.ReadAsStringAsync()))
        {
            Assert.Equal(digest, json.RootElement.GetProperty("document").GetString());
            Assert.True(json.RootElement.GetProperty("timestamp").GetInt64() > 0);
        }

        Assert.True(await ReadingAppSeed.EventuallyAsync(factory, db => db.ChapterProgress.IgnoreQueryFilters()
            .AnyAsync(p => p.ChapterId == seeded.ChapterId && p.Completed)));

        // The writer stores the echo just after the progress row, so the first pull can still beat it.
        (string?, string?, string?) pulled = default;
        for (var i = 0; i < 40 && pulled.Item2 != "Kobo"; i++)
        {
            var response = await client.SendAsync(Koreader(HttpMethod.Get, "syncs/progress/" + digest, seeded.Token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            pulled = (json.RootElement.GetProperty("progress").GetString(), json.RootElement.GetProperty("device").GetString(),
                json.RootElement.GetProperty("device_id").GetString());
            await Task.Delay(50);
        }

        Assert.Equal(("2", "Kobo", "KOBO-1"), pulled);

        var unknown = await client.SendAsync(Koreader(HttpMethod.Get, "syncs/progress/" + new string('a', 32), seeded.Token));
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal("{}", await unknown.Content.ReadAsStringAsync());
    }
}

/// <summary>A reader for a reading app: the OPDS catalogue on, an OPDS token, and a one-chapter series on disk.</summary>
internal static class ReadingAppSeed
{
    public sealed record Seeded(int UserId, string Token, int SeriesId, int ChapterId, int ChapterFileId, string Path);

    public static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> factory, string root,
        params (string Key, string Value)[] settings)
    {
        Directory.CreateDirectory(root);
        const string name = "Pipeline - Ch. 1.cbz";
        var path = Path.Combine(root, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "001.jpg", "002.jpg" })
            {
                using var stream = archive.CreateEntry(entry).Open();
                stream.WriteByte(0xFF);
            }
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var user = new MakiUser
        {
            UserName = "pipeline-reader", NormalizedUserName = "PIPELINE-READER",
            Permissions = MakiPermission.UseOpds, AllRootFolders = true
        };
        db.Users.Add(user);
        var folder = new RootFolder { Path = root };
        db.RootFolders.Add(folder);
        await db.SaveChangesAsync();

        var token = ApiKeyCrypto.Generate();
        db.UserApiKeys.Add(new UserApiKey
        {
            UserId = user.Id, Name = "opds", KeyHash = ApiKeyCrypto.Hash(token), Prefix = ApiKeyCrypto.Prefix(token),
            Scope = UserApiKeyScope.Opds, CreatedAt = DateTime.UtcNow
        });
        foreach (var (key, value) in settings.Prepend((SettingKeys.OpdsEnabled, "true")))
        {
            db.UserSettings.Add(new UserSetting { UserId = user.Id, Key = key, Value = value });
        }

        var series = new Series
        {
            Title = "Pipeline", SortTitle = "pipeline", RootFolderId = folder.Id, FolderName = "",
            Added = DateTime.UtcNow
        };
        db.Series.Add(series);
        await db.SaveChangesAsync();
        var file = new ChapterFile
        {
            SeriesId = series.Id, RelativePath = name, Size = new FileInfo(path).Length, SourceName = "test",
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        await db.SaveChangesAsync();
        var chapter = new Chapter { SeriesId = series.Id, Number = 1, Language = "en", ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        await db.SaveChangesAsync();
        return new Seeded(user.Id, token, series.Id, chapter.Id, file.Id, path);
    }

    /// <summary>Polls the host's database, for writes made off the request.</summary>
    public static async Task<bool> EventuallyAsync(WebApplicationFactory<Program> factory, Func<MakiDbContext, Task<bool>> check)
    {
        for (var i = 0; i < 100; i++)
        {
            using (var scope = factory.Services.CreateScope())
            {
                if (await check(scope.ServiceProvider.GetRequiredService<MakiDbContext>()))
                {
                    return true;
                }
            }

            await Task.Delay(50);
        }

        return false;
    }
}
