using System.Net;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Core.Sources;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A <see cref="NotificationService"/> that records dispatches instead of sending them — the real
/// <c>Dispatch</c> is fire-and-forget, which a test can't await.
/// </summary>
internal sealed class RecordingNotifications() : NotificationService(
    new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
    [],
    NullLogger<NotificationService>.Instance)
{
    public List<(NotificationEventType Type, NotificationMessage Message)> Sent { get; } = [];

    public override void Dispatch(NotificationEventType type, NotificationMessage message) =>
        Sent.Add((type, message));
}

/// <summary>
/// An <see cref="InboxService"/> that records raises instead of writing them — the real
/// <c>Raise</c> is fire-and-forget over its own scope, which a test can neither await nor observe.
/// </summary>
internal sealed class RecordingInbox() : InboxService(
    new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
    null!,
    null!,
    TimeProvider.System,
    NullLogger<InboxService>.Instance)
{
    public List<(InboxEventType Type, InboxMessage Message, InboxAudience Audience)> Raised { get; } = [];

    /// <summary>Series raises land here with the audience unresolved — the resolution is a DB concern.</summary>
    public List<(InboxEventType Type, InboxMessage Message, int SeriesId)> RaisedForSeries { get; } = [];

    public override void Raise(InboxEventType type, InboxMessage message, InboxAudience audience) =>
        Raised.Add((type, message, audience));

    public override void RaiseForSeries(InboxEventType type, InboxMessage message, int seriesId) =>
        RaisedForSeries.Add((type, message, seriesId));

    public override Task RaiseAsync(
        InboxEventType type, InboxMessage message, InboxAudience audience, CancellationToken ct = default)
    {
        Raised.Add((type, message, audience));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="ICurrentUser"/> for controllers that take one. Several older test files carry their
/// own private copy of this; new tests should use this one.
/// </summary>
internal sealed class TestCurrentUser(
    int userId, string userName = "test", MakiPermission permissions = MakiPermission.Admin) : ICurrentUser
{
    public bool IsAuthenticated => true;
    public int UserId { get; } = userId;
    public string UserName { get; } = userName;
    public MakiPermission Permissions { get; } = permissions;
    public bool AllRootFolders => true;
    public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
    public string MaxContentRating => "erotica";
}

/// <summary>A hand-wound clock for services that take a <see cref="TimeProvider"/>.</summary>
internal sealed class StoppedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// <see cref="SourceAvailability"/> over a settings store with nothing disabled — the default
/// for tests that don't exercise the global source switch. Pass a seeded
/// <see cref="FakeAppSettings"/> to <c>new SourceAvailability(...)</c> to disable one.
/// </summary>
internal static class Sources
{
    public static SourceAvailability AllEnabled => new(new FakeAppSettings());

    public static SourceAvailability Disabled(params string[] names) =>
        new(new FakeAppSettings().Set(SettingKeys.SourcesDisabled, string.Join(',', names)));

    /// <summary>
    /// A resolver whose named sources all report a single chapter numbered 1 — enough for tests that
    /// only care which mapping got picked, not real per-chapter matching against a source's catalog.
    /// </summary>
    public static ChapterSourceResolver SingleChapterResolver(SourceAvailability? availability, params string[] sourceNames)
    {
        var fakes = sourceNames.Select(name => new FakeSource
        {
            Name = name,
            OnListChapters = _ => [new(name, "s", "1", "1", 1m, null, null, "en", null)]
        });
        return Resolver(new SourceRegistry(fakes), availability);
    }

    /// <summary>
    /// A resolver over the given registry, with a private chapter-list cache. Tests that count
    /// <see cref="FakeSource.ListCalls"/> need their own cache instance or one test's listing
    /// satisfies the next one's.
    /// </summary>
    public static ChapterSourceResolver Resolver(SourceRegistry registry, SourceAvailability? availability = null) =>
        new(registry, availability ?? AllEnabled,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance));
}

/// <summary>In-memory <see cref="IAppSettings"/> — a dictionary, no DB.</summary>
internal sealed class FakeAppSettings : IAppSettings
{
    private readonly Dictionary<string, string> _values = new();

    public FakeAppSettings Set(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// An <see cref="IHubContext{EventsHub}"/> that swallows every send — for services under test that take
/// an <see cref="EventBroadcaster"/> as a dependency they don't otherwise care about.
/// </summary>
internal sealed class NoopHubContext : IHubContext<EventsHub>
{
    public IHubClients Clients { get; } = new NoopHubClients();
    public IGroupManager Groups => throw new NotSupportedException("Not used by EventBroadcaster");

    private sealed class NoopHubClients : IHubClients
    {
        private static readonly IClientProxy Proxy = new NoopClientProxy();
        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class NoopClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> whose clients answer every request with one canned body.</summary>
internal sealed class StubHttpClientFactory(string body, HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
{
    public string? LastRequestUri { get; private set; }

    public HttpClient CreateClient(string name) => new(new Handler(this, body, status));

    private sealed class Handler(StubHttpClientFactory owner, string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
