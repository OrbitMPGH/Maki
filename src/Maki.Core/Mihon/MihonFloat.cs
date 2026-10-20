using System.Globalization;

namespace Maki.Core.Mihon;

/// <summary>
/// Mihon stores a chapter number as a 32-bit float, and anything that widens it hands us 10.1 as
/// 10.100000381469727. Printing the float's shortest form recovers the number a person wrote.
/// </summary>
public static class MihonFloat
{
    public static double Narrow(float value) =>
        double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Null for NaN, infinity or a value too large to hold.</summary>
    public static decimal? NarrowToDecimal(float value)
    {
        if (!float.IsFinite(value))
        {
            return null;
        }

        return decimal.TryParse(value.ToString(CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var result) ? result : null;
    }
}
