namespace Maki.Api.Services.Upscaling;

public record EnhanceServerModelDto(string Id, bool Ready);

public record EnhanceServerDto(bool Enabled, IReadOnlyList<EnhanceServerModelDto> Models);

/// <summary>What the reader manifest tells the client about server-side page enhancement.</summary>
public class EnhanceAvailability
{
    public Task<EnhanceServerDto> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(new EnhanceServerDto(false, []));
}
