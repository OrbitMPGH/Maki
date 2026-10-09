using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public sealed class SharedJsonConvertersTests : IDisposable
{
    private enum Phase { Waiting, Running }

    private sealed record Probe(Phase Phase, DateTime At, DateTime? MaybeAt);

    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public SharedJsonConvertersTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_hub_and_MVC_serialise_enums_and_datetimes_identically()
    {
        using var factory = new WebApplicationFactory<Program>();
        var mvc = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
        var hubOptions = factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>();

        var probe = new Probe(
            Phase.Running,
            new DateTime(2026, 10, 9, 12, 30, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 10, 9, 12, 30, 0, DateTimeKind.Utc));

        var rest = JsonSerializer.Serialize(probe, mvc);
        var hub = JsonSerializer.Serialize(probe, hubOptions.Value.PayloadSerializerOptions);

        Assert.Equal(rest, hub);
        Assert.Contains("\"Running\"", hub);
        Assert.Contains("2026-10-09T12:30:00Z", hub);

        var buffer = new ArrayBufferWriter<byte>();
        new JsonHubProtocol(hubOptions).WriteMessage(new InvocationMessage("probe", [probe]), buffer);
        Assert.Contains(rest, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
