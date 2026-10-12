using Maki.Core.Parsing;
using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class ReleaseMarkersTests
{
    private static NameParsingProfile On(string language) => new([language]);

    [Theory]
    [InlineData("fr", "Series_Name_Tome_7.cbz", 7, null)]
    [InlineData("fr", "Series.Name.T29.Author.FR.[CBZ]-GROUP", 29, null)]
    [InlineData("fr", "Series Name Tome 105 (FR).cbz", 105, null)]
    [InlineData("fr", "Series Name T01-02.cbz", 1, 2)]
    [InlineData("fr", "Series Name T01-T03.cbz", 1, 3)]
    [InlineData("es", "Serie Tomo 3.cbz", 3, null)]
    [InlineData("es", "Serie Volumen 12.cbz", 12, null)]
    [InlineData("de", "Titel Band 4.cbz", 4, null)]
    [InlineData("de", "Titel Bd. 5.cbz", 5, null)]
    [InlineData("de", "Titel Band 1-3.cbz", 1, 3)]
    [InlineData("pl", "Tytuł Tom 2.cbz", 2, null)]
    [InlineData("pl", "Tytuł T03.cbz", 3, null)]
    [InlineData("ru", "Название Том 6.cbz", 6, null)]
    [InlineData("ru", "Название т. 7.cbz", 7, null)]
    [InlineData("ru", "Название Т08.cbz", 8, null)]
    [InlineData("ru", "Nazvanie Tom 9.cbz", 9, null)]
    [InlineData("tr", "Seri Cilt 10.cbz", 10, null)]
    [InlineData("tr", "SERİ CİLT 11.cbz", 11, null)]
    [InlineData("ja", "ワンピース12巻.zip", 12, null)]
    [InlineData("ja", "ワンピース 第3巻.cbz", 3, null)]
    [InlineData("ja", "ワンピース 第１２巻.cbz", 12, null)]
    [InlineData("ja", "ワンピース 第1〜3巻.cbz", 1, 3)]
    [InlineData("ja", "ワンピース 全12巻.zip", 1, 12)]
    [InlineData("zh-Hans", "海贼王 第5卷.cbz", 5, null)]
    [InlineData("zh-Hans", "海贼王 7册.cbz", 7, null)]
    [InlineData("ko", "원피스 3권.cbz", 3, null)]
    [InlineData("ko", "원피스 01-02권.cbz", 1, 2)]
    public void Reads_volume_markers(string language, string file, int volume, int? volumeEnd)
    {
        var parsed = ReleaseNameParser.ParseFileName(file, On(language));

        Assert.True(parsed.IsVolume);
        Assert.Equal(volume, parsed.Volume);
        Assert.Equal(volumeEnd, parsed.VolumeEnd);
    }

    [Theory]
    [InlineData("fr", "Titre Chapitre 12.cbz", 12, null)]
    [InlineData("fr", "Titre T03 Chapitre 12.cbz", 12, 3)]
    [InlineData("fr", "Titre Chap. 4.5.cbz", 4.5, null)]
    [InlineData("es", "Serie Capítulo 15.cbz", 15, null)]
    [InlineData("es", "Serie Cap. 16.cbz", 16, null)]
    [InlineData("pt-BR", "Série Capitulo 17.cbz", 17, null)]
    [InlineData("de", "Titel Kapitel 20.cbz", 20, null)]
    [InlineData("de", "Titel Band 2 Kap. 21.cbz", 21, 2)]
    [InlineData("pl", "Tytuł Rozdział 30.cbz", 30, null)]
    [InlineData("pl", "Tytuł Rozdz. 31.cbz", 31, null)]
    [InlineData("ru", "Название Глава 40.cbz", 40, null)]
    [InlineData("ru", "Название Том 2 Гл. 41.cbz", 41, 2)]
    [InlineData("ru", "Nazvanie Glava 42.cbz", 42, null)]
    [InlineData("tr", "Seri Bölüm 50.cbz", 50, null)]
    [InlineData("tr", "Seri Bolum 51.cbz", 51, null)]
    [InlineData("ja", "ワンピース 第1050話.cbz", 1050, null)]
    [InlineData("ja", "タイトル 第3巻 第12話.cbz", 12, 3)]
    [InlineData("ja", "タイトル 第2章.cbz", 2, null)]
    [InlineData("zh-Hans", "海贼王 第1051话.cbz", 1051, null)]
    [InlineData("zh-Hans", "标题 第3回.cbz", 3, null)]
    [InlineData("ko", "나 혼자만 레벨업 120화.cbz", 120, null)]
    [InlineData("ko", "타이틀 제5장.cbz", 5, null)]
    [InlineData("ko", "타이틀 3편.cbz", 3, null)]
    public void Reads_chapter_markers(string language, string file, double number, int? volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file, On(language));

        Assert.True(parsed.IsChapter);
        Assert.Equal((decimal)number, parsed.Number);
        Assert.Equal(volume, parsed.Volume);
    }

    [Theory]
    [InlineData("Series_Name_Tome_7.cbz", 7)]
    [InlineData("Titel Band 4.cbz", 4)]
    [InlineData("Brass Band 2.cbz", 2)]
    [InlineData("T2 Trainspotting 3.cbz", 3)]
    public void With_no_language_on_names_parse_as_they_always_did(string file, int chapter)
    {
        var parsed = ReleaseNameParser.ParseFileName(file, NameParsingProfile.Default);

        Assert.True(parsed.IsChapter);
        Assert.Equal(chapter, parsed.Number);
    }

    [Fact]
    public void A_language_with_no_words_of_its_own_reads_nothing_extra()
    {
        Assert.False(ReleaseNameParser.ParseFileName("ワンピース12巻.zip", NameParsingProfile.Default).IsRecognized);
    }

    [Theory]
    [InlineData("Terminator T2 v05.cbz", null, 5)]
    [InlineData("Don't 2.cbz", 2.0, null)]
    [InlineData("T-800 012.cbz", 12.0, null)]
    [InlineData("t2g 5.cbz", 5.0, null)]
    [InlineData("T2 Trainspotting 3.cbz", null, 2)]
    public void A_single_letter_yields_to_words_and_word_boundaries(string file, double? chapter, int? volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file, On("fr"));

        Assert.Equal((decimal?)chapter, parsed.Number);
        Assert.Equal(volume, parsed.Volume);
    }

    [Theory]
    [InlineData("de", "Brass Band 2.cbz", null, 2)]
    [InlineData("de", "Girls Band Cry 3.cbz", 3.0, null)]
    [InlineData("de", "Bandana 4.cbz", 4.0, null)]
    [InlineData("pl", "Tom Sawyer 2.cbz", 2.0, null)]
    [InlineData("es", "Red Cap 2.cbz", 2.0, null)]
    public void A_title_word_is_a_marker_only_when_a_number_follows_it(
        string language, string file, double? chapter, int? volume)
    {
        var parsed = ReleaseNameParser.ParseFileName(file, On(language));

        Assert.Equal((decimal?)chapter, parsed.Number);
        Assert.Equal(volume, parsed.Volume);
    }

    [Fact]
    public void The_setting_keeps_known_languages_in_picker_order()
    {
        var profile = NameParsingProfile.FromSetting("pl, FR ,en,xx");

        Assert.Equal(["fr", "pl"], profile.Languages);
        Assert.Equal("fr,pl", profile.Fingerprint);
        Assert.Equal(NameParsingProfile.Default.Fingerprint, NameParsingProfile.FromSetting(" ").Fingerprint);
    }

    [Fact]
    public void Every_language_but_english_has_words()
    {
        Assert.Equal(["de", "fr", "es", "pt-BR", "pl", "ru", "tr", "ja", "zh-Hans", "ko"],
            ReleaseMarkers.Languages.OrderBy(l => Array.IndexOf(Maki.Core.Localization.SupportedLanguages.All, l)));
        Assert.All(ReleaseMarkers.Languages, l => Assert.NotEmpty(ReleaseMarkers.ChapterWords(l)));
    }

    [Fact]
    public void A_scene_title_matches_its_series_once_its_volume_marker_reads()
    {
        const string title = "One.Piece.T29.Eiichiro.Oda.FR.[CBZ]-GROUP";

        var parsed = ReleaseTitleParser.Parse(title, On("fr"));

        Assert.Equal(new NumberRange(29, 29), parsed.Span.Volumes);
        Assert.Contains("One Piece", parsed.TitleCandidates);
        Assert.Equal("GROUP", parsed.Group);
        Assert.True(ReleaseTitleMatcher.Matches(parsed, "One Piece", null, []));
        Assert.False(ReleaseTitleMatcher.Matches(ReleaseTitleParser.Parse(title, NameParsingProfile.Default), "One Piece", null, []));
    }

    [Fact]
    public void A_scene_title_keeps_its_scanlator_tier_through_its_real_group()
    {
        const string title = "One.Piece.T29.Eiichiro.Oda.FR.[CBZ]-GROUP";

        Assert.Equal(QualityTier.Scanlator, QualityTierResolver.Resolve(null, title, title, isVolume: true));
    }

    [Fact]
    public void An_english_scene_title_matches_and_names_its_group()
    {
        var parsed = ReleaseTitleParser.Parse("One.Piece.v01-v10.Digital-GRP", NameParsingProfile.Default);

        Assert.Equal(new NumberRange(1, 10), parsed.Span.Volumes);
        Assert.Contains("One Piece", parsed.TitleCandidates);
        Assert.Equal("GRP", parsed.Group);
    }

    [Theory]
    [InlineData("Series.Name.T01-02")]
    [InlineData("Series.Name.T01-T02")]
    [InlineData("Spider-Man.T01.FR")]
    public void A_scene_suffix_that_is_a_number_or_a_marker_is_no_group(string title)
    {
        Assert.Null(ReleaseTitleParser.Parse(title, On("fr")).Group);
    }

    [Theory]
    [InlineData("Chainsaw-Man.cbz")]
    [InlineData("Look-Back 012.cbz")]
    public void A_hyphenated_title_is_not_a_scene_name(string file)
    {
        Assert.Empty(ReleaseNameParser.ParseFileName(file).Tags);
    }

    [Fact]
    public void A_cjk_title_loses_its_author_bracket_and_volume()
    {
        var parsed = ReleaseTitleParser.Parse("【尾田栄一郎】ワンピース 第1-3巻", On("ja"));

        Assert.Equal(new NumberRange(1, 3), parsed.Span.Volumes);
        Assert.Equal(["ワンピース"], parsed.TitleCandidates);
        Assert.Equal(["尾田栄一郎"], parsed.Tags);
    }

    [Theory]
    [InlineData("CBZ")]
    [InlineData("FR")]
    [InlineData("Español")]
    [InlineData("Tome 3")]
    [InlineData("T05")]
    [InlineData("Chapitre 12")]
    [InlineData("第3巻")]
    public void Markers_and_language_words_are_never_a_group_whatever_is_switched_on(string tag)
    {
        Assert.Null(ReleaseTags.Group([tag]));
    }
}
