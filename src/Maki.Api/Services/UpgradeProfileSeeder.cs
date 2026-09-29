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
/// Sharpness and compression are scored by <see cref="MeasuredQuality"/> rather than by width or size
/// formats, whose thresholds turned into cliffs: aggregators cap width at 1200 to 1400px, and width
/// otherwise follows the series rather than the source. At weight 10, a copy with twice the image data
/// per pixel is 10 points ahead, and the profiles' MinScoreDelta of 5 ignores differences under about
/// 40%. No starter profile replaces an Unknown file: that is mostly an imported library, and the
/// lowest tier, so the first aggregator scrape would win.
/// </remarks>
public class UpgradeProfileSeeder(MakiDbContext db, ILogger<UpgradeProfileSeeder> logger)
{
    public const string MarkerKey = "upgrades.seeded.v2";

    public const int MeasuredWeight = 10;

    public const string RawOrMachineTranslated = "Raw or machine translated";
    public const string TrustedDigitalRipper = "Trusted digital ripper";

    private static readonly (string Name, FormatCondition[] Conditions)[] Formats =
    [
        (RawOrMachineTranslated,
            [new(FormatConditionType.ReleaseNameMatches, @"[\[(]\s*(raws?|mtl|machine[ ._-]?translat\w*)\s*[\])]", Required: true, Negate: false)]),
        (TrustedDigitalRipper,
            [new(FormatConditionType.GroupMatches, "^(1r0n|danke-Empire|LuCaZ|Oak)$", Required: true, Negate: false)])
    ];

    private static readonly (string Format, int Score)[] BaseScores =
    [
        (RawOrMachineTranslated, -100)
    ];

    private static readonly (string Name, QualityTier Cutoff, bool Upgrades, (string Format, int Score)[] Scores)[] Profiles =
    [
        ("Never upgrade", QualityTier.Aggregator, false, []),
        ("Balanced", QualityTier.Scanlator, true, BaseScores),
        ("Official releases", QualityTier.Official, true, BaseScores),
        ("Digital volumes", QualityTier.Volume, true, [.. BaseScores, (TrustedDigitalRipper, 20)])
    ];

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var formats = await db.QualityFormats.ToDictionaryAsync(f => f.Name, StringComparer.OrdinalIgnoreCase, ct);
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

        var taken = (await db.UpgradeProfiles.Select(p => p.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, cutoff, upgrades, scores) in Profiles)
        {
            if (taken.Contains(name))
            {
                continue;
            }

            var profile = new UpgradeProfile
            {
                Name = name,
                Cutoff = cutoff,
                UpgradesEnabled = upgrades,
                MinScoreDelta = 5,
                AllowReplacingUnknown = false,
                ResolutionWeight = MeasuredWeight,
                CompressionWeight = MeasuredWeight,
                FormatScores = [.. scores.Select(s => new FormatScore(formats[s.Format].Id, s.Score))]
            };
            UpgradeProfileDefaults.Normalise(profile);
            db.UpgradeProfiles.Add(profile);
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded the starter quality formats and upgrade profiles");
    }
}
