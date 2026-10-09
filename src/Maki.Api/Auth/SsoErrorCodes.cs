namespace Maki.Api.Auth;

/// <summary>
/// The short codes a failed single sign-on redirect carries in its query string
/// (<c>?ssoError=</c> on the login page, <c>?oidcLinkError=</c> on the settings page). The browser
/// arrives by a top-level navigation, so the message cannot travel in a response body, and putting a
/// sentence in the URL would let anyone craft a link that shows arbitrary text. The client maps each
/// code to its own translated message and shows a generic one for anything it does not know.
/// </summary>
public static class SsoErrorCodes
{
    public const string Fallback = "signInFailed";

    private const string NamePrefix = "sso";

    private static readonly HashSet<string> Known =
    [
        "accountDisabled",
        "accountNotSetUp",
        "alreadyLinked",
        "alreadyLinkedOther",
        "challengeRejected",
        "incomplete",
        "linkFailed",
        "linkNeedsPassword",
        "linkNewAccountFailed",
        "noAccountLinked",
        "noSubject",
        "usernameExists",
        Fallback,
    ];

    /// <summary>The code for a catalogue key such as <c>error.auth.ssoAlreadyLinked</c>; the fallback for anything else.</summary>
    public static string FromKey(string? key)
    {
        var name = key is null ? "" : key[(key.LastIndexOf('.') + 1)..];
        if (!name.StartsWith(NamePrefix, StringComparison.Ordinal) || name.Length == NamePrefix.Length)
        {
            return Fallback;
        }

        var code = char.ToLowerInvariant(name[NamePrefix.Length]) + name[(NamePrefix.Length + 1)..];
        return Known.Contains(code) ? code : Fallback;
    }
}
