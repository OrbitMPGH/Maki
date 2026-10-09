using Maki.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Maki.Api.Tests;

public sealed class UntrustedForwardedForWarningTests
{
    private sealed class Capture : ILogger<UntrustedForwardedForWarning>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static Task Run(UntrustedForwardedForWarning middleware, bool forwarded, bool handled)
    {
        var context = new DefaultHttpContext();
        if (forwarded) context.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        if (handled) context.Request.Headers["X-Original-For"] = "10.0.0.2:1234";
        return middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task Warns_once_for_a_forwarded_request_the_middleware_did_not_act_on()
    {
        var log = new Capture();
        var middleware = new UntrustedForwardedForWarning(_ => Task.CompletedTask, log);

        await Run(middleware, forwarded: true, handled: false);
        await Run(middleware, forwarded: true, handled: false);

        Assert.Equal([LogLevel.Warning], log.Levels);
    }

    [Fact]
    public async Task Stays_quiet_without_the_header_or_when_a_trusted_proxy_was_honoured()
    {
        var log = new Capture();
        var middleware = new UntrustedForwardedForWarning(_ => Task.CompletedTask, log);

        await Run(middleware, forwarded: false, handled: false);
        await Run(middleware, forwarded: true, handled: true);

        Assert.Empty(log.Levels);
    }
}
