using Maki.Core.Images;

namespace Maki.Core.Entities;

/// <summary>
/// One entry of the ordered list that decides how a file is converted. The first rule whose
/// conditions all match a file decides it; see <see cref="PageConversionPlan"/>.
/// </summary>
public class PageConversionRule
{
    public int Id { get; set; }

    /// <summary>Position in the list, from 0. Rules are tried in ascending order.</summary>
    public int Order { get; set; }

    public string Name { get; set; } = "";

    /// <summary>A <see cref="PageConversionConditions"/> as JSON.</summary>
    public string ConditionsJson { get; set; } = "{}";

    public bool Skip { get; set; }

    /// <summary>The stop a matching file's black-and-white pages are converted at. Set whenever <see cref="Skip"/> is false.</summary>
    public PageConversionStop? Stop { get; set; }

    /// <summary>The format of its black-and-white pages. Null takes the plan's.</summary>
    public PageConversionFormat? Format { get; set; }

    /// <summary>The format of its colour pages. Null, with <see cref="ColourStop"/>, converts them like its black-and-white pages.</summary>
    public PageConversionFormat? ColourFormat { get; set; }

    /// <summary>The stop of its colour pages, set exactly when <see cref="ColourFormat"/> is.</summary>
    public PageConversionStop? ColourStop { get; set; }
}
