using Maki.Api.Auth;

namespace Maki.Api.Tests;

/// <summary>The step matching is checked against the RFC 6238 appendix B vectors (SHA-1, 6 digits).</summary>
public sealed class TotpReplayGuardTests
{
    private const string Rfc6238Key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082", 1L)]
    [InlineData(1111111109L, "081804", 37037036L)]
    [InlineData(1234567890L, "005924", 41152263L)]
    public void The_step_of_a_known_code_is_found(long unixSeconds, string code, long step)
    {
        var found = TotpReplayGuard.MatchStep(Rfc6238Key, code, DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(step, found);
    }

    [Fact]
    public void A_code_inside_the_window_matches_its_own_step_and_one_outside_matches_nothing()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1111111109L);

        // Two steps old is still inside the window Identity accepts, three is not.
        Assert.Equal(37037034L, TotpReplayGuard.MatchStep(Rfc6238Key, StepCode(37037034L), at));
        Assert.Null(TotpReplayGuard.MatchStep(Rfc6238Key, StepCode(37037033L), at));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void A_malformed_code_matches_nothing(string code) =>
        Assert.Null(TotpReplayGuard.MatchStep(Rfc6238Key, code, DateTimeOffset.UtcNow));

    private static string StepCode(long step) =>
        TotpReplayGuard.CodeFor(TotpReplayGuard.Base32Decode(Rfc6238Key)!, step);
}
