using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Seeds the starter formats and upgrade profiles, once. Gated by an AppConfig marker rather than
/// by an empty table, so an admin who deletes them does not get them back on the next restart.
/// Anything whose name is already taken is left alone. No profile is made the default; nothing
/// changes for any series until an admin picks one.
/// </summary>
/// <remarks>
/// The profiles share their format scores and weights and differ only in how far they upgrade, which
/// the names and descriptions say. Sharpness and compression are scored by <see cref="MeasuredQuality"/>
/// at weight 10, so a copy with twice the image data per pixel is 10 points ahead, and MinScoreDelta 5
/// ignores differences under about 40%. No starter replaces an Unknown file: that is mostly an
/// imported library, and the lowest tier, so the first aggregator scrape would win.
/// <para>
/// An instance seeded under <see cref="PreviousMarkerKey"/> has its untouched starters (still at
/// version 1) renamed and described in place; edited ones, and ones an admin deleted, are left be.
/// </para>
/// </remarks>
public class UpgradeProfileSeeder(MakiDbContext db, ILogger<UpgradeProfileSeeder> logger)
{
    public const string MarkerKey = "upgrades.seeded.v3";
    public const string PreviousMarkerKey = "upgrades.seeded.v2";

    public const int MeasuredWeight = 10;

    public const string RawOrMachineTranslated = "Raw or machine translated";
    public const string TrustedDigitalRipper = "Trusted digital ripper";

    public const string NeverUpgrade = "Never upgrade";
    public const string ReplaceAggregatorCopies = "Replace aggregator copies";
    public const string UpgradeToOfficial = "Upgrade to official releases";
    public const string UpgradeToVolumes = "Upgrade to digital volumes";

    private static readonly (string Name, FormatCondition[] Conditions)[] Formats =
    [
        (RawOrMachineTranslated,
            [new(FormatConditionType.ReleaseNameMatches, @"[\[(]\s*(raws?|mtl|machine[ ._-]?translat\w*)\s*[\])]", Required: true, Negate: false)]),
        (TrustedDigitalRipper,
            [new(FormatConditionType.GroupMatches, "^(1r0n|danke-Empire|LuCaZ|Oak)$", Required: true, Negate: false)])
    ];

    private static readonly (string Format, int Score)[] Scores =
    [
        (RawOrMachineTranslated, -100),
        (TrustedDigitalRipper, 20)
    ];

    private sealed record Starter(string Name, string? LegacyName, QualityTier Cutoff, bool Upgrades, string Description);

    private static readonly Starter[] Profiles =
    [
        new(NeverUpgrade, null, QualityTier.Aggregator, false,
            "Keeps whatever was downloaded first. Pin a series to this to keep it out of upgrades while another profile is the default."),
        new(ReplaceAggregatorCopies, "Balanced", QualityTier.Scanlator, true,
            "Swaps chapters from aggregator sites for a copy from a scanlator or an official source, then stops. Scanlations you already have are kept."),
        new(UpgradeToOfficial, "Official releases", QualityTier.Official, true,
            "Keeps upgrading until each chapter comes from an official source such as MANGA Plus or Webtoons, replacing scanlations once an official copy exists."),
        new(UpgradeToVolumes, "Digital volumes", QualityTier.Volume, true,
            "Keeps upgrading until chapters are covered by a digital volume release. Needs Prowlarr and a torrent client. Volumes come out months after chapters, so ongoing series keep being checked, and a volume file takes over the single chapter files it covers.")
    ];

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var seededBefore = await db.AppConfig.AnyAsync(c => c.Key == PreviousMarkerKey, ct);
        var formats = await db.QualityFormats.ToDictionaryAsync(f => f.Name, StringComparer.OrdinalIgnoreCase, ct);
        if (!seededBefore)
        {
            foreach (var (name, conditions) in Formats)
            {
                if (!formats.ContainsKey(name))
                {
                    var format = new QualityFormat { Name = name, Conditions = [.. conditions] };
                    db.QualityFormats.Add(format);
                    formats[name] = format;
                }
            }

            await db.SaveChangesAsync(ct);
        }

        var scores = Scores
            .Where(s => formats.ContainsKey(s.Format))
            .Select(s => new FormatScore(formats[s.Format].Id, s.Score))
            .ToList();
        var profiles = await db.UpgradeProfiles.ToListAsync(ct);
        UpgradeProfile? Named(string? name) =>
            name is null ? null : profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        foreach (var starter in Profiles)
        {
            if ((Named(starter.Name) ?? Named(starter.LegacyName)) is { } existing)
            {
                if (seededBefore && existing.Version == 1)
                {
                    existing.Name = starter.Name;
                    existing.Description ??= starter.Description;
                    existing.FormatScores = [.. scores];
                    existing.Version++;
                }

                continue;
            }

            if (seededBefore)
            {
                continue;
            }

            var profile = new UpgradeProfile
            {
                Name = starter.Name,
                Description = starter.Description,
                Cutoff = starter.Cutoff,
                UpgradesEnabled = starter.Upgrades,
                MinScoreDelta = 5,
                AllowReplacingUnknown = false,
                ResolutionWeight = MeasuredWeight,
                CompressionWeight = MeasuredWeight,
                FormatScores = [.. scores]
            };
            UpgradeProfileDefaults.Normalise(profile);
            db.UpgradeProfiles.Add(profile);
            profiles.Add(profile);
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
        logger.LogInformation(seededBefore
            ? "Renamed and described the untouched starter upgrade profiles"
            : "Seeded the starter quality formats and upgrade profiles");
    }
}
