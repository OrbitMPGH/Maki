using System.Net;
using Mangarr.Core.Configuration;

namespace Mangarr.Api.Tests;

/// <summary>A hand-wound clock for services that take a <see cref="TimeProvider"/>.</summary>
internal sealed class StoppedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
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
