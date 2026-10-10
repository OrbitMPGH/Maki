using System.Reflection;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// Pins who can reach every controller action. A mutating action with no permission policy falls
/// back to "any signed-in user", and an action marked anonymous needs no sign-in at all; both are
/// legitimate in places and both are exactly what a forgotten attribute looks like. A new action that
/// lands in either group fails here until someone reads it and adds it to the list below.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public sealed class ControllerAuthorizationInventoryTests
{
    private static readonly string[] ReadVerbs = ["GET", "HEAD", "OPTIONS"];

    private sealed record Action(string Name, string[] Verbs, bool Anonymous, string[] Policies)
    {
        public bool HasPolicy => Policies.Length > 0;
    }

    private static IEnumerable<Action> Actions()
    {
        var controllers = typeof(AccountController).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t));

        foreach (var controller in controllers)
        {
            var classAttributes = controller.GetCustomAttributes(inherit: true);
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.DeclaringType == typeof(object) || method.DeclaringType == typeof(ControllerBase)
                    || method.IsDefined(typeof(NonActionAttribute), inherit: true))
                {
                    continue;
                }

                var verbs = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .SelectMany(a => a.HttpMethods)
                    .ToArray();
                if (verbs.Length == 0)
                {
                    continue;
                }

                var all = classAttributes.Concat(method.GetCustomAttributes(inherit: true)).ToList();
                yield return new Action(
                    $"{controller.Name}.{method.Name}",
                    verbs,
                    all.OfType<AllowAnonymousAttribute>().Any(),
                    all.OfType<AuthorizeAttribute>().Select(a => a.Policy).OfType<string>()
                        .Where(p => p.Length > 0).Distinct().ToArray());
            }
        }
    }

    private static string[] Names(IEnumerable<Action> actions) =>
        actions.Select(a => a.Name).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Every_mutating_action_without_a_permission_policy_is_a_reviewed_self_service_action()
    {
        var found = Names(Actions().Where(a =>
            !a.Anonymous && !a.HasPolicy && a.Verbs.Any(v => !ReadVerbs.Contains(v))));

        Assert.True(found.SequenceEqual(SelfService), Diff(found, SelfService));
    }

    [Fact]
    public void Every_anonymous_action_is_a_reviewed_one()
    {
        var found = Names(Actions().Where(a => a.Anonymous));

        Assert.True(found.SequenceEqual(Anonymous), Diff(found, Anonymous));
    }

    [Fact]
    public void Settings_actions_are_admin_except_the_reviewed_ones()
    {
        var settings = Actions().Where(a => a.Name.StartsWith("SettingsController.")).ToList();

        var notAdmin = settings
            .Where(a => !a.Policies.SequenceEqual([Auth.Policies.Admin]))
            .Select(a => $"{a.Name}={(a.HasPolicy ? string.Join("+", a.Policies) : "none")}")
            .Distinct().Order(StringComparer.Ordinal).ToArray();

        Assert.True(notAdmin.SequenceEqual(SettingsNotAdmin), Diff(notAdmin, SettingsNotAdmin));
    }

    [Fact]
    public void Endpoints_outside_the_controllers_are_the_reviewed_ones()
    {
        var previous = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        var configDir = Path.Combine(Path.GetTempPath(), "maki-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        CookieSession.EnsureWebRoot();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", configDir);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var found = factory.Services.GetServices<EndpointDataSource>()
                .SelectMany(d => d.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is null)
                .Select(e =>
                {
                    var verbs = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                    var access = e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                        ? "anonymous"
                        : e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any() ? "authorize" : "fallback policy";
                    return $"{(verbs is null ? "ANY" : string.Join("/", verbs))} {e.RoutePattern.RawText} ({access})";
                })
                .Distinct().Order(StringComparer.Ordinal).ToArray();

            Assert.True(found.SequenceEqual(OtherEndpoints), Diff(found, OtherEndpoints));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", previous);
            try
            {
                Directory.Delete(configDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string Diff(string[] found, string[] expected) =>
        "Authorization inventory changed. Review each new action and add it to the list if it is meant to be "
        + $"reachable that way.\nNew: [{string.Join(", ", found.Except(expected))}]\n"
        + $"Gone: [{string.Join(", ", expected.Except(found))}]";

    /// <summary>Acts only on the caller's own data, or is gated by a check inside the action.</summary>
    private static readonly string[] SelfService =
    [
        "AccountController.ChangePassword",
        "AccountController.CreateApiKey",
        "AccountController.DisableTwoFactor",
        "AccountController.EnableTwoFactor",
        "AccountController.RegenerateRecoveryCodes",
        "AccountController.RevokeApiKey",
        "AccountController.RevokeSessions",
        "AccountController.SetContentRating",
        "AccountController.SetupTwoFactor",
        "AccountController.UnlinkOidc",
        "AnimeResumeController.Apply",
        "AnimeResumeController.Dismiss",
        "AnimeResumeController.Undismiss",
        "AnimeSignalsController.Settings",
        "AnimeSignalsController.Sync",
        "AuthController.Logout",
        "AuthController.OidcLinkConfirm",
        "CustomRailsController.Count",
        "CustomRailsController.Create",
        "CustomRailsController.Delete",
        "CustomRailsController.Update",
        "DiscoverFiltersController.Create",
        "DiscoverFiltersController.Delete",
        "DiscoverFiltersController.Update",
        "HomeController.HideFromReading",
        "HomeController.UnhideFromReading",
        "InboxController.Clear",
        "InboxController.Dismiss",
        "InboxController.MarkAllRead",
        "InboxController.MarkRead",
        "InboxController.SavePrefs",
        "LibraryFiltersController.Create",
        "LibraryFiltersController.Delete",
        "LibraryFiltersController.Update",
        "PreviewController.Release",
        "PreviewController.Start",
        "ProgressController.DeleteGoal",
        "ProgressController.SaveGoal",
        "ProgressController.SaveSettings",
        "ReaderController.MarkRead",
        "ReaderController.MarkUnread",
        "ReaderController.SaveProgress",
        "ReaderController.SetChaptersState",
        "ReaderController.SetSeriesPrefs",
        "ReaderController.SetSeriesProfile",
        "ReaderController.StartKavitaImport",
        "ReaderController.ToggleBookmark",
        "ReadingProfilesController.Create",
        "ReadingProfilesController.Delete",
        "ReadingProfilesController.Update",
        "RecommendationController.Creator",
        "RecommendationController.DiscoverCohort",
        "RecommendationController.DiscoverCount",
        "RecommendationController.DiscoverFeed",
        "RecommendationController.DiscoverSearch",
        "RecommendationController.Get",
        "RecommendationController.SetDefaults",
        "RecommendationController.SetFollowing",
        "RecommendationController.SetHidden",
        "RecommendationController.SetSearchDefaults",
        "RecommendationFeedbackController.ClearSignalOverride",
        "RecommendationFeedbackController.Mutate",
        "RecommendationFeedbackController.MutateFranchise",
        "RecommendationFeedbackController.SetSignalOverride",
        "RecommendationFeedbackController.Undo",
        "SeriesController.SetNotificationMode",
        "SeriesController.SetNotificationModeBulk",
        "SeriesController.SetRating",
        "SeriesRequestsController.Create",
        "SeriesRequestsController.Delete",
        "SettingsController.SeenAppearanceAnnouncement",
        "SettingsController.SeenLanguageAnnouncement",
        "SettingsController.SetReader",
        "SettingsController.SetUi",
    ];

    /// <summary>Sign-in, OIDC and OAuth callbacks, and the OPDS feed (its path token authenticates it).</summary>
    private static readonly string[] Anonymous =
    [
        "AuthController.Login",
        "AuthController.OidcCallback",
        "AuthController.OidcChallenge",
        "AuthController.Setup",
        "AuthController.TwoFactor",
        "OpdsController.Cover",
        "OpdsController.Download",
        "OpdsController.OnDeck",
        "OpdsController.Page",
        "OpdsController.Recent",
        "OpdsController.Root",
        "OpdsController.Search",
        "OpdsController.SearchDescription",
        "OpdsController.SeriesChapters",
        "OpdsController.SeriesList",
        "ScrobbleController.OAuthCallback",
    ];

    /// <summary>
    /// Every SettingsController action that is not admin-only, with the policy it has instead. The
    /// per-user settings and the reads the shell needs before it knows whether the caller is an admin.
    /// </summary>
    private static readonly string[] SettingsNotAdmin =
    [
        "SettingsController.GetAnnouncements=none",
        "SettingsController.GetDiscover=none",
        "SettingsController.GetLibrary=none",
        "SettingsController.GetMetadata=none",
        "SettingsController.GetNamingTokens=none",
        "SettingsController.GetOpds=UseOpds",
        "SettingsController.GetReader=none",
        "SettingsController.GetScrobble=UseTrackers",
        "SettingsController.GetSetup=none",
        "SettingsController.GetUi=none",
        "SettingsController.RotateOpdsToken=UseOpds",
        "SettingsController.SeenAppearanceAnnouncement=none",
        "SettingsController.SeenLanguageAnnouncement=none",
        "SettingsController.SetDiscover=ChangeContentRating",
        "SettingsController.SetOpds=UseOpds",
        "SettingsController.SetReader=none",
        "SettingsController.SetScrobble=UseTrackers",
        "SettingsController.SetUi=none",
    ];

    /// <summary>Everything mapped outside the controllers: the SPA bootstrap, the SPA fallback and the hub.</summary>
    private static readonly string[] OtherEndpoints =
    [
        "ANY /signalr/events (authorize)",
        "ANY /signalr/events/negotiate (authorize)",
        "GET /initialize.json (anonymous)",
        "GET/HEAD {*path:nonfile} (anonymous)",
    ];
}
