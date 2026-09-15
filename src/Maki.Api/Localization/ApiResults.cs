using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Localization;

/// <summary>
/// The shape every failure this API reports comes back in.
/// <para>
/// <c>error</c> keeps the name and the meaning it has always had, so
/// <c>errorMessage()</c> in the client's single error path needs no change and neither do the
/// roughly 150 mutation call sites whose failures run through it. It is simply localized now.
/// </para>
/// <para>
/// <c>code</c> is new and purely additive: the stable dotted key behind the message. Nothing
/// consumes it yet. It exists so a caller that wants to branch on a specific failure, or attach an
/// error to a specific form field, can do so later without a second round of touching 150 call
/// sites, and so logs and bug reports stay greppable in English whatever language the user saw.
/// </para>
/// <para>
/// Deliberately not <c>ProblemDetails</c>. That would change the wire shape at every call site and
/// in the client's error path, to buy content negotiation and <c>type</c> URIs nothing here
/// consumes. It is also orthogonal to localization: adopting it would be neither easier nor harder
/// after this. If it is ever wanted it is its own change, and an easier one once every error has a
/// code.
/// </para>
/// </summary>
public static class ApiResults
{
    /// <summary>
    /// 400 with a localized message. <paramref name="args"/> is an anonymous object filling the
    /// message's ICU placeholders: <c>this.Fail(L, "error.settings.urlInvalid", new { service })</c>.
    /// </summary>
    public static IActionResult Fail(
        this ControllerBase controller, ILocalizer localizer, string key, object? args = null) =>
        controller.BadRequest(Body(localizer, key, args));

    /// <summary>
    /// 404 with a localized message. Named so it cannot collide with <c>ControllerBase.NotFound</c>,
    /// which callers still use for the many cases that carry no message at all.
    /// </summary>
    public static IActionResult NotFoundMessage(
        this ControllerBase controller, ILocalizer localizer, string key, object? args = null) =>
        controller.NotFound(Body(localizer, key, args));

    /// <summary>409, for a request that conflicts with the current state rather than being malformed.</summary>
    public static IActionResult Conflict(
        this ControllerBase controller, ILocalizer localizer, string key, object? args = null) =>
        controller.Conflict(Body(localizer, key, args));

    private static object Body(ILocalizer localizer, string key, object? args) =>
        new { code = key, error = localizer.Get(key, args) };
}
