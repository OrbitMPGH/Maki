using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Indexers;
using Maki.Data;
using Maki.Sources.Suwayomi;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="Message">Why it failed, in the reader's language. Null on success.</param>
public record ConnectionTestStatus(string Key, bool Ok, string? Message, DateTime TestedAt);

/// <summary>
/// The last Test result per connection, for the settings cards. A result only counts while the
/// stored settings are the ones it tested, so a changed address or credential reads as not tested
/// without every writer having to clear it, and a test of unsaved values counts once they are saved.
/// </summary>
public sealed class ConnectionTestStore(MakiDbContext db, IAppSettings settings, SuwayomiSettings suwayomi, ILocalizer localizer)
{
    public const string Kavita = "kavita";
    public const string FlareSolverr = "flaresolverr";
    public const string DownloadClient = "downloadclient";
    public const string Suwayomi = "suwayomi";

    public static string IndexerKey(IndexerBackend backend) => "indexer:" + backend.Key();

    public static string KavitaFingerprint(string? url, string? apiKey) => Hash(Url(url), apiKey);

    public static string FlareSolverrFingerprint(string? url) => Hash(Url(url));

    public static string IndexerFingerprint(IndexerSettings s) => Hash(s.Backend.Key(), Url(s.Url), s.ApiKey);

    public static string DownloadClientFingerprint(TorrentClientSettings s) =>
        Hash(s.Kind.Key(), Url(s.Url), s.Username, s.Password);

    public static string SuwayomiFingerprint(SuwayomiConnection c) => c.AuthMode == SuwayomiAuthMode.None
        ? Hash(c.BaseUri.ToString(), c.AuthMode.ToString())
        : Hash(c.BaseUri.ToString(), c.AuthMode.ToString(), c.Username, c.Password);

    public async Task RecordAsync(string key, string fingerprint, bool ok, string? messageKey = null, object? args = null,
        CancellationToken ct = default)
    {
        var row = await db.ConnectionTestResults.FirstOrDefaultAsync(r => r.Key == key, ct);
        if (row is null)
        {
            row = new ConnectionTestResult { Key = key };
            db.ConnectionTestResults.Add(row);
        }

        row.Ok = ok;
        row.MessageKey = ok ? null : messageKey;
        row.ParamsJson = ok || args is null ? null : JsonSerializer.Serialize(args);
        row.Fingerprint = fingerprint;
        row.TestedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The results that still describe the stored settings; a connection missing here is not tested.</summary>
    public async Task<IReadOnlyList<ConnectionTestStatus>> CurrentAsync(CancellationToken ct = default)
    {
        var rows = await db.ConnectionTestResults.AsNoTracking().ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var current = await CurrentFingerprintsAsync(ct);
        return rows
            .Where(r => current.TryGetValue(r.Key, out var fingerprint) && fingerprint == r.Fingerprint)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new ConnectionTestStatus(r.Key, r.Ok,
                r.Ok || r.MessageKey is null ? null : localizer.Get(r.MessageKey, HealthMonitor.HealthParams(r.ParamsJson)),
                DateTime.SpecifyKind(r.TestedAt, DateTimeKind.Utc)))
            .ToList();
    }

    private async Task<Dictionary<string, string>> CurrentFingerprintsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Kavita] = KavitaFingerprint(await settings.GetAsync(SettingKeys.KavitaUrl, ct), await settings.GetAsync(SettingKeys.KavitaApiKey, ct)),
            [FlareSolverr] = FlareSolverrFingerprint(await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct)),
            [DownloadClient] = DownloadClientFingerprint(await DownloadClientSettingsStore.LoadAsync(settings, ct)),
        };
        foreach (var backend in Enum.GetValues<IndexerBackend>())
        {
            map[IndexerKey(backend)] = IndexerFingerprint(await IndexerSettingsStore.LoadAsync(settings, ct, backend));
        }

        if (await suwayomi.GetAsync(ct) is { } connection)
        {
            map[Suwayomi] = SuwayomiFingerprint(connection);
        }

        return map;
    }

    private static string? Url(string? url) => url?.Trim().TrimEnd('/');

    private static string Hash(params string?[] parts)
    {
        var joined = string.Join('\u001f', parts.Select(p => p ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
