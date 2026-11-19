using Maki.Core.Images;

namespace Maki.Api.Tests;

/// <summary>
/// Skipped where libjxl is not beside the tests, unless <c>MAKI_REQUIRE_LIBJXL=1</c> (CI builds it and sets
/// that, so a missing library fails instead).
/// </summary>
public sealed class JpegXlFactAttribute : FactAttribute
{
    public JpegXlFactAttribute()
    {
        if (JpegXlTests.Skipped)
        {
            Skip = "libjxl is not installed beside the tests";
        }
    }
}

/// <summary><see cref="JpegXlFactAttribute"/> for a theory.</summary>
public sealed class JpegXlTheoryAttribute : TheoryAttribute
{
    public JpegXlTheoryAttribute()
    {
        if (JpegXlTests.Skipped)
        {
            Skip = "libjxl is not installed beside the tests";
        }
    }
}

internal static class JpegXlTests
{
    public static bool Skipped => !JpegXl.IsAvailable && Environment.GetEnvironmentVariable("MAKI_REQUIRE_LIBJXL") != "1";
}
