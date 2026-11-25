namespace Maki.Api.Komga;

/// <summary>
/// Marks a Komga POST that only reads, such as the list API's searches, so the session cookie may
/// authenticate it like a GET. A forged cross-site request to one changes nothing and its response
/// cannot be read without CORS. Never put it on an action that changes state.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KomgaReadOnlyPostAttribute : Attribute;
