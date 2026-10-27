namespace Maki.Core.Parsing;

public enum ChapterAccess
{
    Free,
    Locked,
    Preview
}

public static class ChapterAccessReader
{
    /// <summary>
    /// What a Mihon extension's chapter label says about reading it: a lock or yen mark in front means
    /// it needs a purchase or login, and "(Preview)" means a few sample pages. Neither can be
    /// downloaded as the chapter, so listing them only adds chapters that can never be fetched.
    /// </summary>
    public static ChapterAccess Of(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return ChapterAccess.Free;
        }

        if (label.Contains("(Preview)", StringComparison.OrdinalIgnoreCase))
        {
            return ChapterAccess.Preview;
        }

        return label.Contains("\U0001F512") || label.Contains("\U0001F4B4") ? ChapterAccess.Locked : ChapterAccess.Free;
    }
}
