namespace Maki.Api.Localization;

/// <summary>
/// One user's interface language, for code with no request behind it: a Quartz job rendering a
/// notification, or the hub pushing one to a named recipient.
/// <para>
/// Most of that need went away by not rendering in the job at all. Notification rows store a message
/// key plus parameters and are rendered when somebody reads them, which is both simpler and more
/// correct, since one row can be read by several people who do not share a language. What is left is
/// the handful of places that genuinely have to produce a finished string for one person.
/// </para>
/// </summary>
public interface IUserLocaleResolver
{
    /// <summary>
    /// The user's <c>ui.language</c>, or the instance default when they have never chosen. Never
    /// null and never unsupported.
    /// </summary>
    Task<string> ResolveAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// The instance-wide fallback (<c>ui.defaultlanguage</c>), for the cases with no user at all:
    /// an outbound Discord or webhook message, whose recipient is a chat channel rather than a
    /// person, and an anonymous request.
    /// </summary>
    Task<string> DefaultAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops a cached answer, called when somebody saves a new language. Without it a language
    /// change would not reach background-rendered text for up to the cache lifetime.
    /// </summary>
    void Forget(int userId);
}
