using Maki.Core.Quality;
using Maki.Core.Sources;

namespace Maki.Core.Tests;

public class QualityTierResolverTests
{
    [Theory]
    [InlineData(SourceKind.Official, QualityTier.Official)]
    [InlineData(SourceKind.Scanlator, QualityTier.Scanlator)]
    [InlineData(SourceKind.Aggregator, QualityTier.Aggregator)]
    public void A_scraped_file_maps_straight_from_its_source_kind(SourceKind kind, QualityTier expected)
    {
        var tier = QualityTierResolver.Resolve(kind, null, "148.cbz", isVolume: false);

        Assert.Equal(expected, tier);
    }

    [Fact]
    public void A_digital_volume_import_is_a_volume()
    {
        var tier = QualityTierResolver.Resolve(null,
            "Insomniacs After School v01 (2023) (Digital) (1r0n).cbz", "v01.cbz", isVolume: true);

        Assert.Equal(QualityTier.Volume, tier);
    }

    [Fact]
    public void A_torrent_release_naming_a_group_is_a_scanlator()
    {
        var tier = QualityTierResolver.Resolve(null,
            "Dandadan 148 (2024) (Digital) (1r0n).cbz", "148.cbz", isVolume: false);

        Assert.Equal(QualityTier.Scanlator, tier);
    }

    // A digital release that isn't a volume compilation still names a group, so it reads as
    // Scanlator rather than Volume: the Digital tag alone is not enough without isVolume too.
    [Fact]
    public void A_digital_chapter_that_is_not_a_volume_reads_as_scanlator_not_volume()
    {
        var tier = QualityTierResolver.Resolve(null,
            "Dandadan 148 (2024) (Digital) (1r0n).cbz", "148.cbz", isVolume: false);

        Assert.Equal(QualityTier.Scanlator, tier);
    }

    [Fact]
    public void An_import_with_no_readable_tags_is_unknown()
    {
        var tier = QualityTierResolver.Resolve(null, null, "Look Back.cbz", isVolume: false);

        Assert.Equal(QualityTier.Unknown, tier);
    }

    [Fact]
    public void Without_a_source_kind_the_file_name_tags_decide()
    {
        var tier = QualityTierResolver.Resolve(null, null,
            "Dandadan v01 (2024) (Digital) (1r0n).cbz", isVolume: true);

        Assert.Equal(QualityTier.Volume, tier);
    }

    [Fact]
    public void Falls_back_to_the_file_name_when_no_release_name_is_recorded()
    {
        var tier = QualityTierResolver.Resolve(null, null,
            "Dandadan 148 (2024) (Digital) (1r0n).cbz", isVolume: false);

        Assert.Equal(QualityTier.Scanlator, tier);
    }
}
