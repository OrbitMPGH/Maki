using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class NotificationsControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class StubProvider(NotificationType type, bool throws = false) : INotificationProvider
    {
        public NotificationType Type => type;

        public Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default) =>
            throws ? throw new InvalidOperationException("unreachable") : Task.CompletedTask;
    }

    private NotificationsController Controller(params INotificationProvider[] providers)
    {
        var service = new NotificationService(_db.ScopeFactory(), providers, NullLogger<NotificationService>.Instance);
        return new NotificationsController(_db.NewContext(), service);
    }

    private static NotificationsController.NotificationRequest DiscordRequest(
        string name = "test", string? webhookUrl = "https://discord.com/api/webhooks/abc") =>
        new(name, NotificationType.Discord, true,
            new NotificationsController.ConfigDto(webhookUrl, null, null),
            new NotificationsController.EventsDto(true, false, false, false, false));

    [Fact]
    public async Task Create_persists_and_returns_the_connection()
    {
        var result = await Controller().Create(DiscordRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<NotificationsController.NotificationDto>(ok.Value);
        Assert.Equal("test", dto.Name);
        Assert.Equal("https://discord.com/api/webhooks/abc", dto.Config.WebhookUrl);

        using var db = _db.NewContext();
        Assert.Equal(1, db.Notifications.Count());
    }

    [Fact]
    public async Task Create_rejects_a_missing_url()
    {
        var result = await Controller().Create(DiscordRequest(webhookUrl: null), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_rejects_a_non_http_url()
    {
        var result = await Controller().Create(DiscordRequest(webhookUrl: "ftp://nope"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_missing_returns_not_found()
    {
        var result = await Controller().Update(999, DiscordRequest(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_changes_fields()
    {
        var created = (NotificationsController.NotificationDto)
            ((OkObjectResult)await Controller().Create(DiscordRequest(), CancellationToken.None)).Value!;

        var result = await Controller().Update(created.Id, DiscordRequest(name: "renamed"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("renamed", ((NotificationsController.NotificationDto)ok.Value!).Name);
    }

    [Fact]
    public async Task Delete_removes_the_connection()
    {
        var created = (NotificationsController.NotificationDto)
            ((OkObjectResult)await Controller().Create(DiscordRequest(), CancellationToken.None)).Value!;

        var result = await Controller().Delete(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        using var db = _db.NewContext();
        Assert.Empty(db.Notifications);
    }

    [Fact]
    public async Task Delete_missing_returns_not_found()
    {
        Assert.IsType<NotFoundResult>(await Controller().Delete(999, CancellationToken.None));
    }

    [Fact]
    public async Task Test_returns_ok_when_the_provider_succeeds()
    {
        var controller = Controller(new StubProvider(NotificationType.Discord));

        var result = await controller.Test(DiscordRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Test_returns_bad_gateway_when_the_provider_throws()
    {
        var controller = Controller(new StubProvider(NotificationType.Discord, throws: true));

        var result = await controller.Test(DiscordRequest(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }
}
