using Maki.Api.Dtos;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The release groups behind credit notices: the groups view, the user's decisions on a notice, and applying a
/// notice's group to its chapters. A notice is the group's identity; the name read off its text is only a
/// suggestion and reaches a chapter only through a name the user confirmed or one propagation found.
/// </summary>
public sealed class CreditGroupsService(
    MakiDbContext db,
    CreditNoticeStore store,
    NamingService naming,
    SeriesRenameService renames,
    IAppSettings settings,
    ILogger<CreditGroupsService> logger)
{
    /// <summary>Widest notice image served, in pixels.</summary>
    public const int ImageWidth = 640;

    /// <summary>Linked chapters tried for a notice's image before giving up.</summary>
    private const int ImageTries = 5;

    private const int ExcerptLength = 200;

    public const string NoticeNotFound = "error.creditGroups.noticeNotFound";
    public const string NameTooLong = "error.creditGroups.nameTooLong";
    public const string NameRequired = "error.creditGroups.nameRequired";
    public const string SiteHasNoGroup = "error.creditGroups.siteHasNoGroup";
    public const string InvalidValue = "error.creditGroups.invalidValue";
    public const string NoNotices = "error.creditGroups.noNotices";

    private sealed record NoticeRow(
        int Id, bool Accepted, CreditNoticeOverride Override, CreditNoticeKind Kind, string? GroupName,
        CreditGroupSource GroupSource, string? SuggestedName, string? Invite, string? Text, bool Whole, double Share);

    private sealed record LinkRow(int NoticeId, int ChapterId, int SeriesId, bool Tail, int Page, int Share, int? FileId, string? Group)
    {
        public bool Ungrouped => FileId is not null && string.IsNullOrWhiteSpace(Group);
    }

    /// <summary>Whether apply gives a notice's group to its chapters: a Group notice with a confirmed or propagated name.</summary>
    private static bool Applicable(CreditNoticeKind kind, CreditGroupSource source, string? name) =>
        kind == CreditNoticeKind.Group && source != CreditGroupSource.None && !string.IsNullOrWhiteSpace(name);

    /// <summary>
    /// Files two applicable notices name different groups for, from every applicable notice linked to each file.
    /// Apply leaves them alone whichever notices it was asked for.
    /// </summary>
    private static HashSet<int> Conflicted(IEnumerable<(int FileId, string Name)> named) =>
        named.GroupBy(n => n.FileId)
            .Where(g => g.Select(n => n.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(g => g.Key)
            .ToHashSet();

    private static bool Hidden(CreditNoticeOverride value, bool accepted) =>
        value == CreditNoticeOverride.Hide || (value == CreditNoticeOverride.None && accepted);

    public async Task<CreditGroupListDto> ListAsync(bool all, CancellationToken ct)
    {
        var notices = await NoticesAsync(null, ct);
        var allLinks = await LinksAsync(null, ct);
        var links = allLinks.ToLookup(l => l.NoticeId);
        var applicable = notices.Where(n => Applicable(n.Kind, n.GroupSource, n.GroupName)).ToDictionary(n => n.Id);
        var conflicted = Conflicted(allLinks
            .Where(l => l.FileId is not null && applicable.ContainsKey(l.NoticeId))
            .Select(l => (l.FileId!.Value, applicable[l.NoticeId].GroupName!)));
        var shown = notices.Where(n => links.Contains(n.Id) &&
            (all || Hidden(n.Override, n.Accepted) || n.Kind != CreditNoticeKind.Unknown || n.GroupSource != CreditGroupSource.None));

        var groups = new List<CreditGroupDto>();
        foreach (var bucket in shown.GroupBy(BucketOf))
        {
            var members = bucket.ToList();
            var memberLinks = members.SelectMany(n => links[n.Id]).ToList();
            var chapters = memberLinks.DistinctBy(l => l.ChapterId).ToList();
            // What apply would change for these notices, by the same rule ApplyAsync follows.
            var applies = memberLinks
                .Where(l => applicable.ContainsKey(l.NoticeId) && l.Ungrouped && !conflicted.Contains(l.FileId!.Value))
                .Select(l => l.ChapterId).Distinct().Count();
            var (name, source) = bucket.Key is "site" or "unnamed" ? (null, "none") : NameOf(members);
            var kind = bucket.Key == "site" ? "site" : members.Any(n => n.Kind == CreditNoticeKind.Group) ? "group" : "unknown";
            groups.Add(new CreditGroupDto(
                bucket.Key, name, source, kind, members.Select(n => n.Id).Order().ToList(), members.Count,
                chapters.Select(l => l.SeriesId).Distinct().Count(), chapters.Count, applies));
        }

        return new CreditGroupListDto(groups
            .OrderBy(g => g.Key is "site" or "unnamed" ? 1 : 0)
            .ThenByDescending(g => g.Chapters)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>The bucket a notice is listed under: its Site kind, else the key of its name, else Unnamed.</summary>
    private static string BucketOf(NoticeRow notice)
    {
        if (notice.Kind == CreditNoticeKind.Site)
        {
            return "site";
        }

        var key = NameFor(notice) is { } name ? CreditGroupName.Key(name) : string.Empty;
        return key.Length == 0 ? "unnamed" : "group:" + key;
    }

    private static string? NameFor(NoticeRow notice) =>
        notice.GroupSource != CreditGroupSource.None && !string.IsNullOrWhiteSpace(notice.GroupName) ? notice.GroupName : notice.SuggestedName;

    /// <summary>The bucket's name from its strongest source, the most common spelling there.</summary>
    private static (string? Name, string Source) NameOf(List<NoticeRow> members)
    {
        foreach (var (source, label) in new[] { (CreditGroupSource.Confirmed, "confirmed"), (CreditGroupSource.Propagated, "propagated") })
        {
            var names = members.Where(n => n.GroupSource == source && !string.IsNullOrWhiteSpace(n.GroupName)).Select(n => n.GroupName!).ToList();
            if (names.Count > 0)
            {
                return (MostCommon(names), label);
            }
        }

        var suggested = members.Select(n => n.SuggestedName).OfType<string>().ToList();
        return suggested.Count > 0 ? (MostCommon(suggested), "suggested") : (null, "none");
    }

    private static string MostCommon(List<string> names) =>
        names.GroupBy(n => n, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First().Key;

    public async Task<CreditGroupDetailDto> DetailAsync(IReadOnlyCollection<int> noticeIds, CancellationToken ct)
    {
        var notices = await NoticesAsync(noticeIds, ct);
        var links = await LinksAsync(notices.Select(n => n.Id).ToList(), ct);
        var byNotice = links.ToLookup(l => l.NoticeId);

        var chapterIds = links.Select(l => l.ChapterId).Distinct().ToList();
        var chapters = new List<(int Id, int SeriesId, string SeriesTitle, decimal? Number, int? Volume, string? Title, bool IsOneShot, string? Group)>();
        foreach (var batch in chapterIds.Chunk(500))
        {
            var rows = await db.Chapters.AsNoTracking()
                .Where(c => batch.Contains(c.Id))
                .Select(c => new
                {
                    c.Id, c.SeriesId, SeriesTitle = c.Series!.Title, c.Number, c.Volume, c.Title, c.IsOneShot,
                    Group = c.ChapterFile != null ? c.ChapterFile.Group : null,
                })
                .ToListAsync(ct);
            chapters.AddRange(rows.Select(r => (r.Id, r.SeriesId, r.SeriesTitle, r.Number, r.Volume, r.Title, r.IsOneShot,
                string.IsNullOrWhiteSpace(r.Group) ? null : r.Group)));
        }

        var noticesOf = links.ToLookup(l => l.ChapterId, l => l.NoticeId);
        var series = chapters
            .GroupBy(c => (c.SeriesId, c.SeriesTitle))
            .OrderBy(g => g.Key.SeriesTitle, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CreditGroupSeriesDto(g.Key.SeriesId, g.Key.SeriesTitle, g
                .OrderBy(c => c.Number ?? decimal.MaxValue).ThenBy(c => c.Volume).ThenBy(c => c.Id)
                .Select(c => new CreditGroupChapterDto(
                    c.Id, ChapterLabel.For(c.Number, c.Volume, c.Title, c.IsOneShot), c.Number, c.Volume, c.Title, c.IsOneShot, c.Group,
                    noticesOf[c.Id].Distinct().Order().ToList()))
                .ToList()))
            .ToList();

        var dtos = notices
            .Where(n => byNotice.Contains(n.Id))
            .OrderByDescending(n => byNotice[n.Id].Select(l => l.ChapterId).Distinct().Count())
            .ThenBy(n => n.Id)
            .Select(n => ToDto(n, byNotice[n.Id].ToList()))
            .ToList();
        return new CreditGroupDetailDto(dtos, series);
    }

    private static CreditNoticeDto ToDto(NoticeRow n, List<LinkRow> links)
    {
        var excerpt = n.Text is { Length: > ExcerptLength } text ? text[..ExcerptLength] : n.Text;
        var tails = links.Count(l => l.Tail);
        return new CreditNoticeDto(
            n.Id, Hidden(n.Override, n.Accepted), n.Accepted, Wire(n.Override), Wire(n.Kind), n.GroupName, Wire(n.GroupSource),
            n.SuggestedName, n.Invite, excerpt, n.Whole, n.Whole ? 1 : n.Share, tails * 2 > links.Count,
            links.Select(l => l.ChapterId).Distinct().Count(), links.Select(l => l.SeriesId).Distinct().Count());
    }

    private static string Wire<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    /// <summary>
    /// Applies the user's changes to one notice. Returns an error key when the request cannot be applied:
    /// <see cref="NoticeNotFound"/> or a validation key.
    /// </summary>
    public async Task<(CreditNoticeDto? Notice, string? Error)> UpdateAsync(int id, UpdateCreditNoticeRequest request, CancellationToken ct)
    {
        if (request.Kind is { } kindValue && !Enum.IsDefined(kindValue) ||
            request.Override is { } overrideValue && !Enum.IsDefined(overrideValue))
        {
            return (null, InvalidValue);
        }

        var name = request.GroupName?.Trim();
        if (name is { Length: > CreditNotice.MaxNameLength })
        {
            return (null, NameTooLong);
        }

        var notice = await db.CreditNotices.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (notice is null)
        {
            return (null, NoticeNotFound);
        }

        var kind = request.Kind ?? notice.Kind;
        if (kind == CreditNoticeKind.Site && name is { Length: > 0 })
        {
            return (null, SiteHasNoGroup);
        }

        notice.Kind = kind;
        if (kind == CreditNoticeKind.Site)
        {
            // A site's ad never names a chapter's group.
            notice.GroupName = null;
            notice.GroupSource = CreditGroupSource.None;
        }

        if (name is { Length: 0 })
        {
            notice.GroupName = null;
            notice.GroupSource = CreditGroupSource.None;
        }
        else if (name is not null)
        {
            notice.GroupName = name;
            notice.GroupSource = CreditGroupSource.Confirmed;
            notice.Kind = CreditNoticeKind.Group;
        }

        notice.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (request.Override is { } decision && decision != notice.Override)
        {
            await store.SetOverrideAsync(id, decision, ct);
        }

        if (name is not null)
        {
            await ResuggestAsync(ct);
        }

        var detail = await DetailAsync([id], ct);
        return (detail.Notices.FirstOrDefault() ?? ToDto((await NoticesAsync([id], ct))[0], []), null);
    }

    /// <summary>
    /// Confirms <paramref name="name"/> as the group of every listed notice that is not a Site ad. Returns how many
    /// notices took it, or an error key.
    /// </summary>
    public async Task<(int Named, string? Error)> NameAsync(IReadOnlyCollection<int> noticeIds, string? name, CancellationToken ct)
    {
        name = name?.Trim();
        if (noticeIds.Count == 0)
        {
            return (0, NoNotices);
        }

        if (string.IsNullOrEmpty(name))
        {
            return (0, NameRequired);
        }

        if (name.Length > CreditNotice.MaxNameLength)
        {
            return (0, NameTooLong);
        }

        var ids = noticeIds.Distinct().ToList();
        if (!await db.CreditNotices.AnyAsync(n => ids.Contains(n.Id), ct))
        {
            return (0, NoticeNotFound);
        }

        var named = await db.CreditNotices
            .Where(n => ids.Contains(n.Id) && n.Kind != CreditNoticeKind.Site)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.GroupName, name)
                .SetProperty(n => n.GroupSource, CreditGroupSource.Confirmed)
                .SetProperty(n => n.Kind, CreditNoticeKind.Group)
                .SetProperty(n => n.UpdatedAtUtc, DateTime.UtcNow), ct);
        await ResuggestAsync(ct);
        return (named, null);
    }

    /// <summary>A newly confirmed name settles the spelling of close guesses on other notices.</summary>
    private async Task ResuggestAsync(CancellationToken ct)
    {
        try
        {
            await store.SuggestAsync(null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "Could not refresh the suggested group names");
        }
    }

    /// <summary>
    /// Gives every chapter linked to the listed notices the notice's group, where the chapter's file has none.
    /// Only Group notices with a confirmed or propagated name apply; a group a file already carries is never
    /// replaced, and a file two applicable notices name different groups for is left alone, whichever of them
    /// were asked for. Then renames the files whose name the chapter format makes depend on the group, through
    /// <see cref="SeriesRenameService.RenameFilesAsync"/> (the folder stays, as after an import). Each series is
    /// done under its lock in one transaction: when the rename is refused (a download in flight, a name collision,
    /// no root folder) the series keeps no group and the reason comes back as a warning, and a file the rename had
    /// to skip loses its group again, so a later apply does both. ComicInfo is not rewritten: a rewrite swaps a new
    /// archive over the name and breaks a hardlink to a seeding copy, and nothing else that changes a file's group
    /// does it either. Safe to repeat: a second run finds every file grouped and changes nothing.
    /// </summary>
    public async Task<ApplyCreditGroupsResult> ApplyAsync(IReadOnlyCollection<int> noticeIds, CancellationToken ct)
    {
        var ids = noticeIds.Distinct().ToList();
        var notices = (await db.CreditNotices.AsNoTracking()
                .Where(n => ids.Contains(n.Id))
                .Select(n => new { n.Id, n.Kind, n.GroupSource, n.GroupName })
                .ToListAsync(ct))
            .Where(n => Applicable(n.Kind, n.GroupSource, n.GroupName))
            .ToDictionary(n => n.Id, n => n.GroupName!.Trim());
        if (notices.Count == 0)
        {
            return new ApplyCreditGroupsResult(0, 0, 0, []);
        }

        var noticeList = notices.Keys.ToList();
        var links = await db.ChapterCreditNotices.AsNoTracking()
            .Where(l => noticeList.Contains(l.NoticeId))
            .Select(l => new { l.NoticeId, l.ChapterId })
            .Distinct()
            .ToListAsync(ct);
        var chapterIds = links.Select(l => l.ChapterId).Distinct().ToList();
        var chapters = new List<(int Id, int SeriesId, int FileId)>();
        foreach (var batch in chapterIds.Chunk(500))
        {
            chapters.AddRange((await db.Chapters.AsNoTracking()
                    .Where(c => batch.Contains(c.Id) && c.ChapterFileId != null)
                    .Select(c => new { c.Id, c.SeriesId, FileId = c.ChapterFileId!.Value })
                    .ToListAsync(ct))
                .Select(c => (c.Id, c.SeriesId, c.FileId)));
        }

        var fileOf = chapters.ToDictionary(c => c.Id, c => c.FileId);
        var names = new Dictionary<int, string>();
        foreach (var link in links)
        {
            if (fileOf.TryGetValue(link.ChapterId, out var fileId))
            {
                names.TryAdd(fileId, notices[link.NoticeId]);
            }
        }

        var conflicted = Conflicted(await NamedFilesAsync(names.Keys.ToList(), ct));
        var renameImported = await settings.GetAsync(SettingKeys.LibraryRenameImportedFiles, ct) != "false";
        var (updated, renamed, conflicts) = (0, 0, 0);
        var warnings = new List<string>();
        foreach (var series in chapters.GroupBy(c => c.SeriesId))
        {
            var fileIds = series.Select(c => c.FileId).Distinct().ToList();
            using var seriesLock = await SeriesLocks.SeriesAsync(series.Key, ct);
            var files = await db.ChapterFiles.Where(f => fileIds.Contains(f.Id) && f.SeriesId == series.Key).ToListAsync(ct);
            var changed = new List<ChapterFile>();
            foreach (var file in files.Where(f => string.IsNullOrWhiteSpace(f.Group)))
            {
                if (conflicted.Contains(file.Id))
                {
                    conflicts++;
                    continue;
                }

                changed.Add(file);
            }

            if (changed.Count == 0)
            {
                continue;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var file in changed)
            {
                file.Group = names[file.Id];
            }

            await db.SaveChangesAsync(ct);
            var rename = await RenameAsync(series.Key, changed.Where(f => renameImported || f.NamedByMaki).ToList(), warnings, ct);
            if (rename.Refused)
            {
                await transaction.RollbackAsync(ct);
                foreach (var file in changed)
                {
                    await db.Entry(file).ReloadAsync(ct);
                }

                continue;
            }

            if (rename.Skipped.Count > 0)
            {
                foreach (var file in rename.Skipped)
                {
                    file.Group = null;
                }

                await db.SaveChangesAsync(ct);
                changed.RemoveAll(rename.Skipped.Contains);
            }

            await transaction.CommitAsync(ct);
            var changedIds = changed.Select(f => f.Id).ToHashSet();
            updated += series.Count(c => changedIds.Contains(c.FileId));
            renamed += rename.Renamed;
        }

        return new ApplyCreditGroupsResult(updated, renamed, conflicts, warnings);
    }

    /// <summary>The group every applicable notice linked to one of <paramref name="fileIds"/> names for that file.</summary>
    private async Task<List<(int FileId, string Name)>> NamedFilesAsync(List<int> fileIds, CancellationToken ct)
    {
        var named = new List<(int, string)>();
        foreach (var batch in fileIds.Chunk(500))
        {
            var rows = await (
                    from l in db.ChapterCreditNotices.AsNoTracking()
                    join c in db.Chapters on l.ChapterId equals c.Id
                    join n in db.CreditNotices on l.NoticeId equals n.Id
                    where c.ChapterFileId != null && batch.Contains(c.ChapterFileId.Value) &&
                        n.Kind == CreditNoticeKind.Group && n.GroupSource != CreditGroupSource.None && n.GroupName != null
                    select new { FileId = c.ChapterFileId!.Value, n.GroupName })
                .Distinct()
                .ToListAsync(ct);
            named.AddRange(rows.Where(r => !string.IsNullOrWhiteSpace(r.GroupName)).Select(r => (r.FileId, r.GroupName!)));
        }

        return named;
    }

    /// <summary>
    /// Renames the files whose name changes with their new group, under the series lock and transaction the
    /// caller holds. Refused when the rename did not run at all; Skipped are files it had to leave where they were.
    /// </summary>
    private async Task<(int Renamed, bool Refused, List<ChapterFile> Skipped)> RenameAsync(
        int seriesId, List<ChapterFile> files, List<string> warnings, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null || files.Count == 0)
        {
            return (0, false, []);
        }

        var due = new Dictionary<ChapterFile, string>();
        foreach (var file in files)
        {
            var chapter = await db.Chapters.AsNoTracking().Where(c => c.ChapterFileId == file.Id)
                .OrderBy(c => c.Volume).ThenBy(c => c.Id).FirstOrDefaultAsync(ct);
            // The format names the group only when the name with it differs from the name without it.
            if (chapter is not null &&
                await naming.BuildChapterFileNameAsync(series, chapter, ct, file.Group) != await naming.BuildChapterFileNameAsync(series, chapter, ct))
            {
                due[file] = file.RelativePath;
            }
        }

        if (due.Count == 0)
        {
            return (0, false, []);
        }

        var result = await renames.RenameFilesAsync(seriesId, due.Keys.Select(f => f.Id).ToList(), ct);
        warnings.AddRange(result.Warnings);
        if (!result.Applied)
        {
            if (result.Error is { } error)
            {
                warnings.Add(error);
            }

            return (0, true, []);
        }

        // The rename updates these same tracked rows, so a path that did not move is a file it skipped. A skip can
        // still rewrite the row with the host's separator, so separators are not a move.
        var skipped = due.Where(d => string.Equals(Separators(d.Key.RelativePath), Separators(d.Value), StringComparison.Ordinal))
            .Select(d => d.Key).ToList();
        return (due.Count - skipped.Count, false, skipped);
    }

    private static string Separators(string path) => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    /// <summary>
    /// The notice's region as a JPEG at most <see cref="ImageWidth"/> wide, cut from a linked chapter's page (the
    /// top share for a head, the bottom one for a tail's partial). Null when no linked chapter can be read.
    /// Pages are read unmarked: this is not a reader, and must not hold the file's page conversion.
    /// </summary>
    public async Task<byte[]?> ImageAsync(int noticeId, ReaderService reader, ReaderArchiveCache archives, CancellationToken ct)
    {
        var notice = await db.CreditNotices.AsNoTracking().Where(n => n.Id == noticeId)
            .Select(n => new { n.Whole, n.Share }).FirstOrDefaultAsync(ct);
        if (notice is null)
        {
            return null;
        }

        var links = await db.ChapterCreditNotices.AsNoTracking()
            .Where(l => l.NoticeId == noticeId)
            .OrderByDescending(l => l.ChapterId)
            .Select(l => new { l.ChapterId, l.Tail, l.Page })
            .Take(ImageTries)
            .ToListAsync(ct);
        foreach (var link in links)
        {
            if (await reader.SliceAsync(link.ChapterId, ct) is not { } slice || link.Page >= slice.PageCount)
            {
                continue;
            }

            var index = slice.StartPage + (link.Tail ? slice.PageCount - 1 - link.Page : link.Page);
            if (index < 0 || index >= slice.Pages.Count)
            {
                continue;
            }

            byte[] bytes;
            await using (var source = await archives.OpenPageUnmarkedAsync(slice.ArchivePath, slice.Pages[index], ct))
            {
                if (source is null)
                {
                    continue;
                }

                using var copy = new MemoryStream();
                await source.CopyToAsync(copy, ct);
                bytes = copy.ToArray();
            }

            var share = notice.Whole ? 1 : notice.Share;
            try
            {
                return await ImageWorkGate.RunAsync(
                    () => Task.FromResult(ImageCodec.JpegRegion(bytes, share, link.Tail && share < 1, ImageWidth, 80)), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogDebug(e, "Could not render credit notice {Id} from chapter {ChapterId}", noticeId, link.ChapterId);
            }
        }

        return null;
    }

    private async Task<List<NoticeRow>> NoticesAsync(IReadOnlyCollection<int>? ids, CancellationToken ct)
    {
        var query = db.CreditNotices.AsNoTracking();
        if (ids is not null)
        {
            var list = ids.Distinct().ToList();
            query = query.Where(n => list.Contains(n.Id));
        }

        return await query
            .Select(n => new NoticeRow(n.Id, n.Accepted, n.Override, n.Kind, n.GroupName, n.GroupSource, n.SuggestedName, n.Invite, n.Text,
                n.Whole, n.Share))
            .ToListAsync(ct);
    }

    /// <summary>The links of the listed notices (every notice when null), for the series the caller can see.</summary>
    private async Task<List<LinkRow>> LinksAsync(IReadOnlyCollection<int>? noticeIds, CancellationToken ct)
    {
        var query = db.ChapterCreditNotices.AsNoTracking();
        if (noticeIds is not null)
        {
            var list = noticeIds.ToList();
            query = query.Where(l => list.Contains(l.NoticeId));
        }

        var rows = await (
                from l in query
                join c in db.Chapters on l.ChapterId equals c.Id
                select new
                {
                    l.NoticeId, l.ChapterId, l.SeriesId, l.Tail, l.Page, l.Share, c.ChapterFileId,
                    Group = c.ChapterFile != null ? c.ChapterFile.Group : null,
                })
            .ToListAsync(ct);
        return rows.Select(r => new LinkRow(r.NoticeId, r.ChapterId, r.SeriesId, r.Tail, r.Page, r.Share, r.ChapterFileId, r.Group)).ToList();
    }
}
