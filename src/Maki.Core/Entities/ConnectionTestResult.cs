namespace Maki.Core.Entities;

/// <summary>
/// The last Test run against one external connection, shown on its settings card. Counts only for
/// the settings it ran against: <see cref="Fingerprint"/> is a hash of those, and a row whose hash no
/// longer matches the stored settings reads as not tested.
/// </summary>
public class ConnectionTestResult
{
    /// <summary><c>kavita</c>, <c>flaresolverr</c>, <c>downloadclient</c>, <c>suwayomi</c>, <c>indexer:{backend}</c>.</summary>
    public string Key { get; set; } = "";

    public bool Ok { get; set; }

    /// <summary>Server catalogue key of the failure, rendered when the card loads. Null on success.</summary>
    public string? MessageKey { get; set; }

    /// <summary>JSON object filling the message's placeholders, or null.</summary>
    public string? ParamsJson { get; set; }

    /// <summary>SHA-256 over the address and credentials tested, never the values themselves.</summary>
    public string Fingerprint { get; set; } = "";

    public DateTime TestedAt { get; set; }
}
