using System.Security.Cryptography;
using System.Text;
using Maki.Core.Configuration;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using NetVips;
using VipsImage = NetVips.Image;

namespace Maki.Api.Services;

/// <summary>The image behind the sign-in card and the tag a browser revalidates it with.</summary>
public sealed record SignInMosaic(byte[] Webp, string ETag);

/// <summary>
/// The blurred cover mosaic behind the sign-in card, when an admin has switched it on.
/// <para>
/// The page is anonymous, so this is the one place library art leaves the instance without a
/// signed-in user. Three things keep that narrow: it is off unless an admin turns it on; only
/// series rated "safe" are drawn (an unrated one is not assumed safe); and the covers are blurred
/// here, into one image, so no clear cover and no title ever reaches a signed-out browser. The
/// per-user library filter is bypassed on purpose: nobody is signed in, and the admin chose to show
/// the instance's covers to everyone who can reach the form.
/// </para>
/// </summary>
public sealed class SignInMosaicService(
    IServiceScopeFactory scopes, IAppSettings settings, CoverService covers, ILogger<SignInMosaicService> logger)
{
    internal const int Columns = 8;
    internal const int MaxCovers = Columns * 3;

    /// <summary>Fewer than this and the band is too sparse to read as a shelf; the page draws its own art instead.</summary>
    internal const int MinCovers = 8;

    /// <summary>
    /// Large enough that a wide screen does not stretch each tile several times over, which blurs
    /// it far past <see cref="BlurSigma"/>.
    /// </summary>
    internal const int TileWidth = 200;
    internal const int TileHeight = 300;
    internal const int Gap = 20;

    internal const double BlurSigma = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SignInMosaic? _cached;

    /// <summary>Null when the setting is off or too few Safe-rated covers exist.</summary>
    public async Task<SignInMosaic?> GetAsync(CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.SignInCoverMosaic, ct) != "true")
        {
            return null;
        }

        List<int> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            ids = await PickAsync(scope.ServiceProvider.GetRequiredService<MakiDbContext>(), ct);
        }

        var files = ids.Select(covers.CoverPathFor).Where(File.Exists).ToList();
        if (files.Count < MinCovers)
        {
            return null;
        }

        // The set of files and when each was last written: a new series, a removed one or a replaced
        // poster changes it, and nothing else should cost a re-render.
        var etag = Tag(files);
        if (_cached?.ETag == etag)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached?.ETag == etag)
            {
                return _cached;
            }

            var webp = await Task.Run(() => Render(files), ct);
            _cached = new SignInMosaic(webp, etag);
            return _cached;
        }
        catch (VipsException ex)
        {
            logger.LogWarning(ex, "Could not build the sign-in cover mosaic");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The newest Safe-rated series that have a poster, across every root folder.</summary>
    internal static Task<List<int>> PickAsync(MakiDbContext db, CancellationToken ct) =>
        db.Series.IgnoreQueryFilters()
            .Where(s => s.ContentRating == "safe" && s.CoverPath != null)
            .OrderByDescending(s => s.Added)
            .Select(s => s.Id)
            .Take(MaxCovers)
            .ToListAsync(ct);

    /// <summary>
    /// The covers as tiles in a grid with transparent gaps, blurred as one image, as WebP with alpha
    /// so the page's own ground shows between them in either theme.
    /// </summary>
    internal static byte[] Render(IReadOnlyList<string> files, double blurSigma = BlurSigma)
    {
        var tiles = new List<VipsImage>(files.Count);
        try
        {
            foreach (var file in files)
            {
                tiles.Add(Tile(file));
            }

            using var grid = VipsImage.Arrayjoin([.. tiles], across: Columns, shim: Gap, background: [0, 0, 0, 0]);
            // Blurred premultiplied, or the transparent gaps bleed black into every cover's edge.
            using var premultiplied = grid.Premultiply();
            using var blurred = premultiplied.Gaussblur(blurSigma);
            using var straight = blurred.Unpremultiply();
            using var bytes = straight.Cast(Enums.BandFormat.Uchar);
            return bytes.WebpsaveBuffer(q: 70, keep: Enums.ForeignKeep.None);
        }
        finally
        {
            foreach (var tile in tiles)
            {
                tile.Dispose();
            }
        }
    }

    private static VipsImage Tile(string file)
    {
        using var thumb = VipsImage.Thumbnail(file, TileWidth, height: TileHeight, size: Enums.Size.Both,
            crop: Enums.Interesting.Centre);
        using var srgb = thumb.Colourspace(Enums.Interpretation.Srgb);
        using var opaque = srgb.HasAlpha() ? srgb.Flatten(background: [0, 0, 0]) : srgb.Copy();
        return opaque.Bandjoin(255);
    }

    private static string Tag(IEnumerable<string> files)
    {
        var key = string.Join('\n', files.Select(f => $"{f}|{File.GetLastWriteTimeUtc(f).Ticks}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    }
}
