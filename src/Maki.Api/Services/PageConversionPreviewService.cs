using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using Maki.Api.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Paths;
using Maki.Core.Quality;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using NetVips;

namespace Maki.Api.Services;

/// <param name="Page">0-based, in reading order.</param>
/// <param name="Reason">"greyscale", "colour", "heaviest" or "custom".</param>
/// <param name="Tier">The file's <see cref="QualityTier"/>, lowercase.</param>
public sealed record PageConversionSample(
    int FileId, int ChapterId, int SeriesId, string SeriesTitle, string ChapterLabel,
    int Page, int PageCount, string Reason, string Tier, long FileSize);

/// <summary>
/// A preview page on disk. When <see cref="Kept"/>, <see cref="Path"/> is the stored page as it is
/// and <see cref="KeptReason"/> is the lowercase <see cref="PageSkip"/> name, or "unreadable".
/// </summary>
public sealed record PageConversionPreview(
    string Path, string ContentType, long OriginalBytes, long EncodedBytes, bool Kept, string Key, string? KeptReason = null);

/// <summary>
/// The page conversion wizard's sample pages and previews. A preview goes through the same
/// <see cref="PageConversion.Convert"/> call as the library pass, so what the wizard shows is what
/// the pass would write.
/// </summary>
public class PageConversionPreviewService(MakiDbContext db, AppPaths paths, ILogger<PageConversionPreviewService> logger)
{
    /// <summary>Archives one samples call may open.</summary>
    private const int MaxProbes = 24;

    private const int ColourTries = 12;

    private const int PagesPerArchive = 3;

    private const int MinSampleWidth = 400;

    private const int MinSampleHeight = 600;

    private static readonly PageConversionSettings SampleSettings =
        new(PageConversionFormat.Webp, PageConversionStop.High, false);

    private static readonly string[] ColourTypes = [SeriesTypes.Manhwa, SeriesTypes.Manhua];

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> InFlight = new(StringComparer.Ordinal);

    private sealed record Candidate(int Id, int SeriesId, string RelativePath, long Size, QualityTier Tier, string Root, string SeriesTitle);

    private sealed record Probe(int Page, int PageCount);

    private enum PageKind { Any, Colour, Grey }

    private sealed record Encoded(byte[]? Bytes, string Extension, string? KeptReason);

    /// <summary>
    /// Up to three pages that show what a stop does: a greyscale manga page, a colour page and one
    /// from the tier that holds the most bytes. Fewer when the library has nothing that fits.
    /// </summary>
    public async Task<IReadOnlyList<PageConversionSample>> SamplesAsync(CancellationToken ct)
    {
        var budget = MaxProbes;
        var usedSeries = new HashSet<int>();
        PageConversionSample? heaviest = null, colour = null, greyscale = null;

        var tiers = await Candidates()
            .GroupBy(f => f.Tier)
            .Select(g => new { Tier = g.Key, Bytes = g.Sum(f => f.Size), Count = g.Count() })
            .ToListAsync(ct);
        if (tiers.OrderByDescending(t => t.Bytes).FirstOrDefault() is { } heavy)
        {
            var median = await Project(Candidates().Where(f => f.Tier == heavy.Tier)
                    .OrderBy(f => f.Size).ThenBy(f => f.Id)
                    .Skip(heavy.Count / 2)
                    .Take(3))
                .ToListAsync(ct);
            foreach (var candidate in median)
            {
                if (budget-- <= 0) break;
                if (await ProbeAsync(candidate, PageKind.Any, ct) is { } probe)
                {
                    heaviest = await SampleAsync(candidate, probe, "heaviest", ct);
                    usedSeries.Add(candidate.SeriesId);
                    break;
                }
            }
        }

        var colourTries = Math.Min(ColourTries, budget);
        var colourFiles = await SpreadAsync(OfSeries(s => s.Type != null && ColourTypes.Contains(s.Type)), usedSeries, colourTries, ct);
        if (colourFiles.Count < colourTries)
        {
            colourFiles.AddRange(await SpreadAsync(
                OfSeries(s => s.Type == null || (s.Type != SeriesTypes.Manga && !ColourTypes.Contains(s.Type))),
                usedSeries, colourTries - colourFiles.Count, ct));
        }

        foreach (var candidate in colourFiles)
        {
            budget--;
            if (await ProbeAsync(candidate, PageKind.Colour, ct) is { } probe)
            {
                colour = await SampleAsync(candidate, probe, "colour", ct);
                usedSeries.Add(candidate.SeriesId);
                break;
            }
        }

        var manga = OfSeries(s => s.Type == SeriesTypes.Manga);
        var greyFiles = await SpreadAsync(manga.Where(f => f.Tier == QualityTier.Volume || f.Tier == QualityTier.Scanlator),
            usedSeries, budget, ct);
        if (greyFiles.Count < budget)
        {
            var seen = greyFiles.Select(f => f.SeriesId).Concat(usedSeries).ToHashSet();
            greyFiles.AddRange(await SpreadAsync(manga, seen, budget - greyFiles.Count, ct));
        }

        foreach (var candidate in greyFiles)
        {
            if (budget-- <= 0) break;
            if (await ProbeAsync(candidate, PageKind.Grey, ct) is { } probe)
            {
                greyscale = await SampleAsync(candidate, probe, "greyscale", ct);
                break;
            }
        }

        return new[] { greyscale, colour, heaviest }.OfType<PageConversionSample>().ToList();
    }

    /// <summary>The file the admin picked, with the page the wizard would suggest. Null when it is not a readable CBZ.</summary>
    public async Task<PageConversionSample?> SampleAsync(int fileId, CancellationToken ct)
    {
        var candidate = await Project(db.ChapterFiles.IgnoreQueryFilters().Where(f => f.Id == fileId)).FirstOrDefaultAsync(ct);
        if (candidate is null || !IsArchive(candidate.RelativePath))
        {
            return null;
        }

        return await ProbeAsync(candidate, null, ct) is { } probe
            ? await SampleAsync(candidate, probe, "custom", ct)
            : null;
    }

    /// <summary>
    /// One page encoded with <paramref name="settings"/>, or the stored page when they are null.
    /// Null when the file, its archive or the page is missing.
    /// </summary>
    public async Task<PageConversionPreview?> PreviewAsync(int fileId, int page, PageConversionSettings? settings,
        CancellationToken ct)
    {
        if (page < 0)
        {
            return null;
        }

        var row = await db.ChapterFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.RelativePath,
                Root = db.Series.IgnoreQueryFilters().Where(s => s.Id == f.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);
        if (row?.Root is null || !IsArchive(row.RelativePath) || LibraryPaths.Resolve(row.Root, row.RelativePath) is not { } path)
        {
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        var (prefix, key) = KeysFor(fileId, info, page, settings);
        if (Find(prefix, key, settings is null) is { } cached)
        {
            return cached;
        }

        if (!await SingleFlight(key, () => BuildAsync(path, page, prefix, key, settings)).WaitAsync(ct))
        {
            return null;
        }

        return Find(prefix, key, settings is null);
    }

    private IQueryable<ChapterFile> Candidates() =>
        db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.OriginalImageFormat == null && f.PageCount >= 3 &&
                        (f.ImageFormat == "jpg" || f.ImageFormat == "png" || f.ImageFormat == "mixed") &&
                        (f.RelativePath.ToLower().EndsWith(".cbz") || f.RelativePath.ToLower().EndsWith(".zip")) &&
                        db.Series.IgnoreQueryFilters().Any(s => s.Id == f.SeriesId && s.PendingImportLink == PendingImportLink.None) &&
                        db.Chapters.IgnoreQueryFilters().Any(c => c.ChapterFileId == f.Id));

    private IQueryable<ChapterFile> OfSeries(System.Linq.Expressions.Expression<Func<Series, bool>> series)
    {
        var matching = db.Series.IgnoreQueryFilters().Where(series).Select(s => s.Id);
        return Candidates().Where(f => matching.Contains(f.SeriesId));
    }

    private IQueryable<Candidate> Project(IQueryable<ChapterFile> files) =>
        files.Join(db.Series.IgnoreQueryFilters(), f => f.SeriesId, s => s.Id,
            (f, s) => new Candidate(f.Id, f.SeriesId, f.RelativePath, f.Size, f.Tier, s.RootFolder!.Path, s.Title));

    /// <summary>One file per series, the series holding the most bytes first.</summary>
    private async Task<List<Candidate>> SpreadAsync(IQueryable<ChapterFile> files, IReadOnlySet<int> skipSeries, int take,
        CancellationToken ct)
    {
        if (take <= 0)
        {
            return [];
        }

        var picks = await files
            .GroupBy(f => f.SeriesId)
            .Select(g => new { SeriesId = g.Key, Bytes = g.Sum(f => f.Size), FileId = g.Min(f => f.Id) })
            .OrderByDescending(g => g.Bytes).ThenBy(g => g.SeriesId)
            .Take(take + skipSeries.Count)
            .ToListAsync(ct);
        var ids = picks.Where(p => !skipSeries.Contains(p.SeriesId)).Take(take).Select(p => p.FileId).ToList();
        var rows = await Project(db.ChapterFiles.IgnoreQueryFilters().Where(f => ids.Contains(f.Id))).ToListAsync(ct);
        return ids.Select(id => rows.FirstOrDefault(r => r.Id == id)).OfType<Candidate>().ToList();
    }

    private static (string Prefix, string Key) KeysFor(int fileId, FileInfo info, int page, PageConversionSettings? settings)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture,
            $"{fileId}-{info.Length}-{info.LastWriteTimeUtc.Ticks}-{page}");
        return (prefix, $"{prefix}-{(settings is null ? "original" : settings.Name().Replace(':', '-').Replace('+', '-'))}");
    }

    /// <summary>
    /// The page the wizard would open on, or with a <paramref name="kind"/> the first of up to three
    /// middle pages that decodes, is at least 400 by 600 and that the High stop converts. Its original
    /// and High encode go into the preview cache, so the wizard's first preview is a hit.
    /// </summary>
    private async Task<Probe?> ProbeAsync(Candidate candidate, PageKind? kind, CancellationToken ct)
    {
        if (LibraryPaths.Resolve(candidate.Root, candidate.RelativePath) is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            if (kind is not { } wanted)
            {
                using var zip = ZipFile.OpenRead(path);
                var names = CbzReader.PageNames(zip);
                return names.Count == 0 ? null : new Probe(SuggestPage(zip, names), names.Count);
            }

            return await ImageWorkGate.RunAsync(() => Task.FromResult(Qualify(candidate.Id, path, wanted)), ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read {Path} for a conversion sample", path);
            return null;
        }
    }

    private Probe? Qualify(int fileId, string path, PageKind kind)
    {
        using var zip = ZipFile.OpenRead(path);
        var names = CbzReader.PageNames(zip);
        var info = new FileInfo(path);
        foreach (var page in MiddlePages(zip, names).Take(PagesPerArchive))
        {
            if (ReadPage(zip, names[page]) is not { } bytes ||
                ImageCodec.TryIdentify(bytes) is not { } header ||
                header.Width < MinSampleWidth || header.Height < MinSampleHeight)
            {
                continue;
            }

            if (kind != PageKind.Any && (ImageCodec.IsColour(bytes) is not { } colour || colour != (kind == PageKind.Colour)))
            {
                continue;
            }

            var extension = Path.GetExtension(names[page]);
            var encoded = Encode(bytes, extension, SampleSettings);
            if (encoded.Bytes is null)
            {
                continue;
            }

            var (prefix, key) = KeysFor(fileId, info, page, SampleSettings);
            Store(prefix, key, extension, bytes, encoded);
            return new Probe(page, names.Count);
        }

        return null;
    }

    /// <summary>The first page over the 16 KiB floor in the middle third, never the cover.</summary>
    internal static int SuggestPage(ZipArchive zip, List<string> names)
    {
        foreach (var page in MiddlePages(zip, names))
        {
            return page;
        }

        return names.Count < 2 ? 0 : Math.Max(1, names.Count / 3);
    }

    private static IEnumerable<int> MiddlePages(ZipArchive zip, List<string> names)
    {
        var start = names.Count < 2 ? 0 : Math.Max(1, names.Count / 3);
        var end = Math.Min(names.Count, Math.Max(start + 1, names.Count * 2 / 3));
        for (var i = start; i < end; i++)
        {
            if (zip.GetEntry(names[i])?.Length > PageConversion.MinPageBytes)
            {
                yield return i;
            }
        }
    }

    private async Task<PageConversionSample> SampleAsync(Candidate candidate, Probe probe, string reason, CancellationToken ct)
    {
        var chapters = await db.Chapters.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.ChapterFileId == candidate.Id)
            .Select(c => new { c.Id, c.Number, c.Volume, c.Title, c.IsOneShot })
            .ToListAsync(ct);
        var first = chapters.OrderBy(c => c.Number is null ? 0 : 1).ThenBy(c => c.Number).ThenBy(c => c.Id).FirstOrDefault();
        return new PageConversionSample(candidate.Id, first?.Id ?? 0, candidate.SeriesId, candidate.SeriesTitle,
            first is null
                ? Path.GetFileNameWithoutExtension(candidate.RelativePath)
                : ChapterLabel.For(first.Number, first.Volume, first.Title, first.IsOneShot),
            probe.Page, probe.PageCount, reason, candidate.Tier.ToString().ToLowerInvariant(), candidate.Size);
    }

    private static bool IsArchive(string relativePath) =>
        relativePath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads one page within the health scan's limits, since the header of an imported archive can
    /// declare anything. Null when the entry is missing, too large or damaged.
    /// </summary>
    private static byte[]? ReadPage(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null || entry.Length > ArchiveHealthAnalyzer.MaxEntryBytes)
        {
            return null;
        }

        try
        {
            var bytes = new byte[entry.Length];
            using var stream = entry.Open();
            stream.ReadExactly(bytes);
            return stream.ReadByte() == -1 ? bytes : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static Task<bool> SingleFlight(string key, Func<Task<bool>> work)
    {
        Lazy<Task<bool>>? mine = null;
        mine = new Lazy<Task<bool>>(() => Task.Run(Run));
        return InFlight.GetOrAdd(key, mine).Value;

        async Task<bool> Run()
        {
            try
            {
                return await work();
            }
            finally
            {
                InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<bool>>>(key, mine!));
            }
        }
    }

    /// <summary>
    /// Reads the page and writes the stored copy (<c>{prefix}-original.ext</c>) and, for a setting,
    /// the encoded one (<c>{key}.webp</c> or <c>.avif</c>, or a <c>{key}.kept</c> holding the reason
    /// when the page is left as it is). Shared by every request for the key, so it never takes their
    /// tokens. The archive is read once the work gate is held, so queued builds hold no page.
    /// </summary>
    private Task<bool> BuildAsync(string path, int page, string prefix, string key, PageConversionSettings? settings) =>
        ImageWorkGate.RunAsync(() => Task.FromResult(Build(path, page, prefix, key, settings)));

    private bool Build(string path, int page, string prefix, string key, PageConversionSettings? settings)
    {
        byte[]? bytes;
        string name;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = CbzReader.PageNames(zip);
            if (page >= names.Count)
            {
                return false;
            }

            name = names[page];
            bytes = ReadPage(zip, name);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read {Path} for a conversion preview", path);
            return false;
        }

        if (bytes is null)
        {
            return false;
        }

        var extension = Path.GetExtension(name);
        Store(prefix, key, extension, bytes, settings is null ? null : Encode(bytes, extension, settings));
        return true;
    }

    private Encoded Encode(byte[] bytes, string extension, PageConversionSettings settings)
    {
        try
        {
            var result = PageConversion.Convert(bytes, extension, settings);
            return result.Converted
                ? new Encoded(result.Bytes, result.Extension, null)
                : new Encoded(null, extension, result.Skipped.ToString()!.ToLowerInvariant());
        }
        catch (VipsException ex)
        {
            logger.LogInformation(ex, "A {Extension} page does not decode; previewed as kept", extension);
            return new Encoded(null, extension, "unreadable");
        }
    }

    private void Store(string prefix, string key, string extension, byte[] original, Encoded? encoded)
    {
        var dir = paths.ConversionPreviewDir;
        Directory.CreateDirectory(dir);
        var stored = Path.Combine(dir, $"{prefix}-original{extension.ToLowerInvariant()}");
        if (!File.Exists(stored))
        {
            WriteAtomic(stored, original);
        }

        if (encoded is null)
        {
            return;
        }

        WriteAtomic(Path.Combine(dir, key + (encoded.Bytes is null ? ".kept" : encoded.Extension)),
            encoded.Bytes ?? System.Text.Encoding.UTF8.GetBytes(encoded.KeptReason!));
    }

    /// <summary>
    /// A hit renews the stored page and the file it returns, so the housekeeping sweep, which goes by
    /// write time, never takes a page that is still in use. A file that vanishes mid-read is a miss.
    /// </summary>
    private PageConversionPreview? Find(string prefix, string key, bool original)
    {
        try
        {
            return Lookup(prefix, key, original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Preview cache entry {Key} vanished while it was read", key);
            return null;
        }
    }

    private PageConversionPreview? Lookup(string prefix, string key, bool original)
    {
        var dir = paths.ConversionPreviewDir;
        if (!Directory.Exists(dir) ||
            Directory.EnumerateFiles(dir, $"{prefix}-original.*").FirstOrDefault() is not { } stored)
        {
            return null;
        }

        var storedLength = new FileInfo(stored).Length;
        var storedType = CbzReader.ContentType(stored);
        if (original)
        {
            Touch(stored);
            return new PageConversionPreview(stored, storedType, storedLength, storedLength, false, key);
        }

        foreach (var (extension, type) in new[] { (".webp", "image/webp"), (".avif", "image/avif") })
        {
            var encoded = new FileInfo(Path.Combine(dir, key + extension));
            if (encoded.Exists)
            {
                var length = encoded.Length;
                Touch(stored, encoded.FullName);
                return new PageConversionPreview(encoded.FullName, type, storedLength, length, false, key);
            }
        }

        var kept = Path.Combine(dir, key + ".kept");
        if (!File.Exists(kept))
        {
            return null;
        }

        var reason = File.ReadAllText(kept).Trim();
        Touch(stored, kept);
        return new PageConversionPreview(stored, storedType, storedLength, storedLength, true, key,
            reason.Length == 0 ? PageSkip.NotSmaller.ToString().ToLowerInvariant() : reason);
    }

    private static void Touch(params string[] files)
    {
        var now = DateTime.UtcNow;
        foreach (var file in files)
        {
            try
            {
                File.SetLastWriteTimeUtc(file, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, $"{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(path))
        {
            File.Delete(tmp);
        }
    }
}
