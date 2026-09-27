using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Seeds the starter upgrade profiles and the one format they use, once. Gated by an AppConfig
/// marker rather than by an empty table, so an admin who deletes every profile does not get them
/// back on the next restart. Neither profile is made the default; nothing changes for any series
/// until an admin picks one.
/// </summary>
public class UpgradeProfileSeeder(MakiDbContext db, ILogger<UpgradeProfileSeeder> logger)
{
    public const string MarkerKey = "upgrades.seeded";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        if (!await db.UpgradeProfiles.AnyAsync(ct))
        {
            var highResolution = await db.QualityFormats.FirstOrDefaultAsync(f => f.Name == "High resolution", ct);
            if (highResolution is null)
            {
                highResolution = new QualityFormat
                {
                    Name = "High resolution",
                    Conditions = [new FormatCondition(FormatConditionType.MinWidth, "1400", Required: true, Negate: false)]
                };
                db.QualityFormats.Add(highResolution);
                await db.SaveChangesAsync(ct);
            }

            var any = new UpgradeProfile
            {
                Name = "Any",
                Cutoff = QualityTier.Aggregator,
                UpgradesEnabled = false
            };
            var preferOfficial = new UpgradeProfile
            {
                Name = "Prefer official",
                Cutoff = QualityTier.Official,
                UpgradesEnabled = true,
                FormatScores = [new FormatScore(highResolution.Id, 10)]
            };
            UpgradeProfileDefaults.Normalise(any);
            UpgradeProfileDefaults.Normalise(preferOfficial);
            db.UpgradeProfiles.AddRange(any, preferOfficial);
            logger.LogInformation("Seeded the starter upgrade profiles");
        }

        db.AppConfig.Add(new AppConfigEntry { Key = MarkerKey, Value = DateTime.UtcNow.ToString("O") });
        await db.SaveChangesAsync(ct);
    }
}
