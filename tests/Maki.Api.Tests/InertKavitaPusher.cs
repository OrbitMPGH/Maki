using Maki.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A <see cref="KavitaProgressPusher"/> that queues nothing, for tests that exercise the reader
/// rather than the push-back.
/// <para>
/// The real <c>QueuePush</c> is fire-and-forget, and it runs two queries — resolving the bound
/// Kavita user, then reading that user's setting — <em>before</em> it discovers pushtokavita is
/// unset and gives up. On a background thread those outlive the test method, so they race
/// <c>TestDb.Dispose</c> closing the connection. SqliteConnection is not thread-safe, so the close
/// corrupted its own internals and threw whatever fitted the interleaving (NullReferenceException
/// from Close, ArgumentOutOfRangeException elsewhere) roughly one run in thirteen.
/// </para>
/// </summary>
internal sealed class InertKavitaPusher : KavitaProgressPusher
{
    private InertKavitaPusher(IServiceScopeFactory scopeFactory, SettingsService settings)
        : base(
            scopeFactory,
            settings,
            new UserSettingsStoreService(scopeFactory),
            new KavitaUserResolver(scopeFactory, settings),
            null!,
            NullLogger<KavitaProgressPusher>.Instance)
    {
    }

    public static KavitaProgressPusher For(IServiceScopeFactory scopeFactory) =>
        new InertKavitaPusher(scopeFactory, new SettingsService(scopeFactory));

    public override void QueuePush(int userId, int seriesId, decimal? chapterNumber)
    {
    }
}
