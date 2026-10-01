using System.Text;
using System.Text.Json;
using Maki.Core.Kavita;

namespace Maki.Core.Tests;

public class KavitaLiveEventsTests
{
    private static string Jwt(string payloadJson)
    {
        static string Segment(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("""{"alg":"HS512","typ":"JWT"}""")}.{Segment(payloadJson)}.signature";
    }

    private static JsonElement Message(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Reads_user_id_and_a_single_admin_role()
    {
        var identity = KavitaLiveEvents.ReadToken(Jwt("""{"name":"orbit","nameid":"3","role":"Admin"}"""));

        Assert.Equal(new KavitaLiveEvents.TokenIdentity(3, true), identity);
    }

    [Fact]
    public void Finds_admin_among_several_roles()
    {
        var identity = KavitaLiveEvents.ReadToken(Jwt("""{"nameid":"1","role":["Pleb","Admin","Download"]}"""));

        Assert.True(identity!.Value.IsAdmin);
    }

    [Fact]
    public void A_non_admin_key_is_reported_as_such()
    {
        var identity = KavitaLiveEvents.ReadToken(Jwt("""{"nameid":"7","role":["Pleb"]}"""));

        Assert.Equal(new KavitaLiveEvents.TokenIdentity(7, false), identity);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.!!!.c")]
    public void Garbage_is_null_rather_than_a_throw(string token) =>
        Assert.Null(KavitaLiveEvents.ReadToken(token));

    [Fact]
    public void Takes_the_series_from_the_keys_own_users_event()
    {
        var message = Message("""
            {"name":"UserProgressUpdate","body":{"userId":3,"seriesId":42,"volumeId":9,"chapterId":100,"pagesRead":12}}
            """);

        Assert.Equal(42, KavitaLiveEvents.SeriesIdFor(message, kavitaUserId: 3));
    }

    [Fact]
    public void Ignores_another_kavita_users_reading()
    {
        // Admins receive every user's progress events.
        var message = Message("""{"body":{"userId":5,"seriesId":42}}""");

        Assert.Null(KavitaLiveEvents.SeriesIdFor(message, kavitaUserId: 3));
    }

    [Fact]
    public void Accepts_pascal_case_property_names()
    {
        var message = Message("""{"Body":{"UserId":3,"SeriesId":8}}""");

        Assert.Equal(8, KavitaLiveEvents.SeriesIdFor(message, kavitaUserId: 3));
    }
}
