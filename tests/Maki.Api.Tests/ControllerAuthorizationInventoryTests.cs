using System.Reflection;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Maki.Api.Tests;

/// <summary>
/// Pins who can reach every controller action. A mutating action with no permission policy falls
/// back to "any signed-in user", and an action marked anonymous needs no sign-in at all; both are
/// legitimate in places and both are exactly what a forgotten attribute looks like. A new action that
/// lands in either group fails here until someone reads it and adds it to the list below.
/// </summary>
public sealed class ControllerAuthorizationInventoryTests
{
    private static readonly string[] ReadVerbs = ["GET", "HEAD", "OPTIONS"];

    private sealed record Action(string Name, string[] Verbs, bool Anonymous, bool HasPolicy);

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
                    all.OfType<AuthorizeAttribute>().Any(a => !string.IsNullOrEmpty(a.Policy)));
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
    public void Settings_reads_and_writes_are_admin_except_the_reviewed_per_user_ones()
    {
        var found = Names(Actions().Where(a => a.Name.StartsWith("SettingsController.") && !a.HasPolicy));

        Assert.True(found.SequenceEqual(SettingsWithoutPolicy), Diff(found, SettingsWithoutPolicy));
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
        "ProgressController.Seen",
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

    /// <summary>Per-user settings and the reads the shell needs before it knows whether the caller is an admin.</summary>
    private static readonly string[] SettingsWithoutPolicy =
    [
        "SettingsController.GetAnnouncements",
        "SettingsController.GetDiscover",
        "SettingsController.GetLibrary",
        "SettingsController.GetMetadata",
        "SettingsController.GetNamingTokens",
        "SettingsController.GetReader",
        "SettingsController.GetSetup",
        "SettingsController.GetUi",
        "SettingsController.SeenAppearanceAnnouncement",
        "SettingsController.SeenLanguageAnnouncement",
        "SettingsController.SetReader",
        "SettingsController.SetUi",
    ];
}
