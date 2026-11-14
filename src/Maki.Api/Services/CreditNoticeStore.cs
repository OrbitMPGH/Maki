using Maki.Api.Services.Ocr;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One verdict per credit notice (<see cref="CreditNotice"/>), so the search reads a credit page once
/// instead of in every chapter that carries it, and again in every predecessor a new chapter sends back.
/// <para>
/// A candidate is the notice it has the same bytes as (zip CRC, length, and share and end of a page read before),
/// else the notice it matches (<see cref="CreditNotices"/>: same kind, fingerprint within
/// <see cref="CreditNotices.MaxDistance"/>, then the rows confirmed). An accepted notice stays accepted.
/// A rejected one is read again on its next member and only then trusted, because OCR is noisy and a
/// first read that misses a keyword must not settle it; one accepting read accepts it for every member,
/// and the chapters judged under it before are searched again. On the 4,981-chapter scan (2026-11-14,
/// git-ignored <c>research/credit-pages/2026-11-14-efficiency</c>) this cut a backfill's reads by 64% and a
/// new chapter's by 93%, with no chapter hiding less.
/// </para>
/// <para>
/// Every notice was accepted on text OCR read, never on the colour rule: a read that returned nothing
/// stores nothing. That is what keeps a bank proposal hidden only on read text. Keep it that way.
/// </para>
/// <para>
/// Only fingerprints stay in memory; a notice's rows load from the database when its fingerprint passes,
/// and the last few hundred are kept.
/// </para>
/// </summary>
public sealed class CreditNoticeStore(IServiceScopeFactory scopes, CreditStore credits, ILogger<CreditNoticeStore> logger)
{
    /// <summary>Notices kept at most; past it nothing new is added and candidates are simply read.</summary>
    public const int MaxNotices = 20_000;

    /// <summary>Reads a rejected notice needs before its rejection is taken without another.</summary>
    public const int TrustedRejection = 2;

    /// <summary>Notice rows kept decoded, from the notices whose fingerprints passed most recently.</summary>
    private const int MaxRows = 256;

    /// <summary>How long a notice no chapter links to is kept: a search may be about to link it.</summary>
    public static readonly TimeSpan UnlinkedLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// One candidate to judge: its region (<see cref="CreditNotices.RegionOf"/>), whether it is a whole
    /// page and what share, the page's zip CRC and length (0 when unknown), the colour rule's verdict,
    /// whether the bank proposed it, the chapter it is in, and whether the share is read from the bottom
    /// of the page (a tail's partial region), which makes it a different read of the same bytes.
    /// </summary>
    public sealed record Query(
        byte[] Rows, int Height, bool Whole, double Share, uint Crc, long Length, bool Colour, bool FromBank, int ChapterId,
        bool Bottom = false);

    /// <summary>
    /// The verdict on a candidate: whether it is a scanlator's, the text it rests on (null only when there
    /// is none: the page could not be read, and the caller falls back to the colour rule), the notice it
    /// is (null when there is none), and whether a page was read for it.
    /// <para>
    /// <paramref name="ByUser"/> is a user's <see cref="CreditNoticeOverride"/> standing in for a read: its
    /// text is the notice's own (never null), so a Hide satisfies the callers that require read text before
    /// a bank proposal is hidden. A user's decision is not a colour verdict, which is what that rule keeps out.
    /// </para>
    /// </summary>
    public sealed record Verdict(bool Accepted, string? Text, int? NoticeId, bool Read, bool ByUser = false)
    {
        public static readonly Verdict Unread = new(false, null, null, true);
    }

    private sealed class Notice
    {
        public required int Id;
        public required bool Whole;
        public required double Share;
        public required float[] Fingerprint;
        public bool Accepted;
        public int Reads;
        public string? Text;
        public CreditNoticeOverride Override;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, CreditRegion> _rows = [];
    private Dictionary<int, Notice>? _notices;
    private int _generation;

    /// <summary>
    /// Moves on whenever a notice turns from rejected to accepted or its override changes, so an in-memory
    /// search result from before it is not served to the chapters that change sends back.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Judges one candidate, calling <paramref name="read"/> (the page's text, or null when it could not
    /// be read) only when no stored verdict answers for it.
    /// </summary>
    public async Task<Verdict> JudgeAsync(Query query, Func<CancellationToken, Task<string?>> read, CancellationToken ct)
    {
        var share = Thousandths(query.Share);
        var region = new CreditRegion(query.Rows, query.Height);
        await _gate.WaitAsync(ct);
        try
        {
            var notices = _notices ??= await LoadAsync(ct);

            // The same bytes were read before: their text, judged with this candidate's colour, is no new read.
            if (query.Length > 0 && await ReadOfAsync(query.Crc, query.Length, share, query.Bottom && !query.Whole, ct) is { } known &&
                notices.TryGetValue(known.NoticeId, out var twin))
            {
                if (Overridden(twin) is { } decided)
                {
                    return decided;
                }

                if (!twin.Accepted && CreditText.IsScanlator(known.Text, query.Colour))
                {
                    await AcceptAsync(twin, known.Text, query.ChapterId, countRead: false, ct);
                }

                return new Verdict(twin.Accepted, twin.Accepted ? twin.Text : known.Text, twin.Id, false);
            }

            var fingerprint = CreditNotices.Fingerprint(region);
            var match = await FindAsync(notices, query, region, fingerprint, ct);
            if (match is not null && Overridden(match) is { } overridden)
            {
                return overridden;
            }

            if (match is { Accepted: true } || match is { Reads: >= TrustedRejection })
            {
                return new Verdict(match.Accepted, match.Text, match.Id, false);
            }

            var text = await read(ct);
            if (text is null)
            {
                return Verdict.Unread;
            }

            var accepted = CreditText.IsScanlator(text, query.Colour);
            if (match is null)
            {
                if (notices.Count >= MaxNotices)
                {
                    return new Verdict(accepted, text, null, true);
                }

                match = await CreateAsync(query, region, fingerprint, text, accepted, ct);
                if (match is null)
                {
                    return new Verdict(accepted, text, null, true);
                }
            }
            else if (accepted)
            {
                await AcceptAsync(match, text, query.ChapterId, countRead: true, ct);
            }
            else
            {
                match.Reads++;
                match.Text = text;
                await UpdateAsync(match, ct);
            }

            await AddReadAsync(query, share, text, match.Id, ct);
            await TryAsync(() => SuggestAsync([match.Id], ct), match.Id);
            return new Verdict(match.Accepted, match.Text, match.Id, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records which notice each judged candidate of <paramref name="chapterId"/> was, replacing what the
    /// chapter's last search recorded.
    /// </summary>
    public async Task SaveLinksAsync(int chapterId, int seriesId, IEnumerable<JudgedCandidate> judged, CancellationToken ct)
    {
        var links = judged
            .Where(j => j.NoticeId is not null)
            .Select(j => new ChapterCreditNotice
            {
                ChapterId = chapterId, SeriesId = seriesId, Tail = j.Tail, Page = j.Page, Share = Thousandths(j.Share),
                NoticeId = j.NoticeId!.Value, Accepted = j.Accepted,
            })
            .DistinctBy(l => (l.Tail, l.Page, l.Share, l.NoticeId))
            .ToList();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.ChapterCreditNotices.IgnoreQueryFilters().Where(l => l.ChapterId == chapterId).ExecuteDeleteAsync(ct);
        db.ChapterCreditNotices.AddRange(links);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // The chapter went while the search ran, or housekeeping took a notice.
            logger.LogDebug(e, "Could not record the credit notices of chapter {ChapterId}", chapterId);
            return;
        }

        if (links.Count > 0)
        {
            await PropagateAsync(links.Select(l => l.NoticeId).Distinct().ToList(), ct);
        }
    }

    /// <summary>Deletes notices no chapter links to any more, once they are old enough that no search is about to.</summary>
    public async Task<int> PruneAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var cutoff = DateTime.UtcNow - UnlinkedLifetime;
            var gone = await db.CreditNotices
                .Where(n => n.UpdatedAtUtc < cutoff && !db.ChapterCreditNotices.IgnoreQueryFilters().Any(l => l.NoticeId == n.Id))
                .Select(n => n.Id)
                .ToListAsync(ct);
            foreach (var batch in gone.Chunk(500))
            {
                await db.CreditNoticeReads.Where(r => batch.Contains(r.NoticeId)).ExecuteDeleteAsync(ct);
                await db.CreditNotices.Where(n => batch.Contains(n.Id)).ExecuteDeleteAsync(ct);
            }

            foreach (var id in gone)
            {
                _notices?.Remove(id);
                _rows.Remove(id);
            }

            return gone.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sets the user's decision on a notice. Every chapter that carries it goes back for a search and
    /// <see cref="Generation"/> moves, as when a notice is accepted. False when there is no such notice.
    /// </summary>
    public async Task<bool> SetOverrideAsync(int noticeId, CreditNoticeOverride value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var notices = _notices ??= await LoadAsync(ct);
            List<int> chapters;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                var changed = await db.CreditNotices.Where(n => n.Id == noticeId).ExecuteUpdateAsync(u => u
                    .SetProperty(n => n.Override, value)
                    .SetProperty(n => n.UpdatedAtUtc, DateTime.UtcNow), ct);
                if (changed == 0)
                {
                    return false;
                }

                chapters = await db.ChapterCreditNotices.AsNoTracking().IgnoreQueryFilters()
                    .Where(l => l.NoticeId == noticeId).Select(l => l.ChapterId).Distinct().ToListAsync(ct);
            }

            if (notices.TryGetValue(noticeId, out var notice))
            {
                notice.Override = value;
            }

            Interlocked.Increment(ref _generation);
            foreach (var chapter in chapters)
            {
                await credits.RequeueAsync(chapter, ct);
            }

            if (chapters.Count > 0)
            {
                CreditScanWorker.Signal();
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Fills <see cref="CreditNotice.SuggestedName"/> and <see cref="CreditNotice.Invite"/> from every text read
    /// for each notice (its stored reads and its own text), all notices when <paramref name="ids"/> is null.
    /// Names the user confirmed elsewhere settle the spelling of a close guess.
    /// </summary>
    public async Task SuggestAsync(IReadOnlyCollection<int>? ids, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var known = await KnownNamesAsync(db, ct);
        var notices = ids is null ? db.CreditNotices.AsNoTracking() : db.CreditNotices.AsNoTracking().Where(n => ids.Contains(n.Id));
        await SuggestAsync(db, await notices.Select(n => new NoticeText(n.Id, n.Text, n.SuggestedName, n.Invite, n.NameVersion))
            .ToListAsync(ct), known, ct);
    }

    private sealed record NoticeText(int Id, string? Text, string? SuggestedName, string? Invite, int NameVersion);

    private static async Task<List<string>> KnownNamesAsync(MakiDbContext db, CancellationToken ct) =>
        await db.CreditNotices.AsNoTracking()
            .Where(n => n.GroupSource == CreditGroupSource.Confirmed && n.GroupName != null)
            .Select(n => n.GroupName!).Distinct().ToListAsync(ct);

    private static async Task SuggestAsync(MakiDbContext db, List<NoticeText> notices, List<string> known, CancellationToken ct)
    {
        if (notices.Count == 0)
        {
            return;
        }

        var ids = notices.Select(n => n.Id).ToList();
        var reads = new Dictionary<int, List<string>>();
        foreach (var batch in ids.Chunk(500))
        {
            foreach (var read in await db.CreditNoticeReads.AsNoTracking().Where(r => batch.Contains(r.NoticeId))
                         .OrderBy(r => r.Id).Select(r => new { r.NoticeId, r.Text }).ToListAsync(ct))
            {
                (reads.TryGetValue(read.NoticeId, out var list) ? list : reads[read.NoticeId] = []).Add(read.Text);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var notice in notices)
        {
            var texts = (reads.TryGetValue(notice.Id, out var list) ? list : []).Prepend(notice.Text);
            var guess = CreditGroupName.Suggest(texts, known);
            if (guess.Name == notice.SuggestedName && guess.Invite == notice.Invite && notice.NameVersion == CreditGroupName.Version)
            {
                continue;
            }

            await db.CreditNotices.Where(n => n.Id == notice.Id).ExecuteUpdateAsync(u => u
                .SetProperty(n => n.SuggestedName, guess.Name)
                .SetProperty(n => n.Invite, guess.Invite)
                .SetProperty(n => n.NameVersion, CreditGroupName.Version), ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Gives each hidden notice of kind Unknown or Group whose group the user did not confirm the group most
    /// of its chapters already carry. A tie, or no chapter carrying one, leaves the notice as it is; a Site
    /// notice never takes one. On the 2026-11-14 scan this was right for 554 of 554 known chapters.
    /// </summary>
    public async Task PropagateAsync(IReadOnlyCollection<int> noticeIds, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var notices = await db.CreditNotices.AsNoTracking()
                .Where(n => noticeIds.Contains(n.Id) && n.Kind != CreditNoticeKind.Site && n.GroupSource != CreditGroupSource.Confirmed &&
                    (n.Override == CreditNoticeOverride.Hide || (n.Override == CreditNoticeOverride.None && n.Accepted)))
                .Select(n => new { n.Id, n.GroupName, n.GroupSource, n.Kind })
                .ToListAsync(ct);
            if (notices.Count == 0)
            {
                return;
            }

            var ids = notices.Select(n => n.Id).ToList();
            var carried = await (
                    from l in db.ChapterCreditNotices.IgnoreQueryFilters()
                    join c in db.Chapters.IgnoreQueryFilters() on l.ChapterId equals c.Id
                    join f in db.ChapterFiles.IgnoreQueryFilters() on c.ChapterFileId equals (int?)f.Id
                    where ids.Contains(l.NoticeId) && f.Group != null && f.Group != ""
                    select new { l.NoticeId, l.ChapterId, f.Group })
                .Distinct()
                .ToListAsync(ct);

            foreach (var notice in notices)
            {
                var groups = carried.Where(c => c.NoticeId == notice.Id)
                    .DistinctBy(c => c.ChapterId)
                    .Select(c => c.Group!.Trim())
                    .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Name: g.GroupBy(x => x, StringComparer.Ordinal).OrderByDescending(x => x.Count()).First().Key, Count: g.Count()))
                    .OrderByDescending(g => g.Count)
                    .ToList();
                if (groups.Count == 0 || (groups.Count > 1 && groups[1].Count == groups[0].Count))
                {
                    continue;
                }

                var name = groups[0].Name.Length <= CreditNotice.MaxNameLength ? groups[0].Name : groups[0].Name[..CreditNotice.MaxNameLength];
                if (name == notice.GroupName && notice.GroupSource == CreditGroupSource.Propagated && notice.Kind == CreditNoticeKind.Group)
                {
                    continue;
                }

                // Re-checked in the update, so a confirmation or a Site mark made meanwhile is never overwritten.
                await db.CreditNotices
                    .Where(n => n.Id == notice.Id && n.Kind != CreditNoticeKind.Site && n.GroupSource != CreditGroupSource.Confirmed)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(n => n.GroupName, name)
                        .SetProperty(n => n.GroupSource, CreditGroupSource.Propagated)
                        .SetProperty(n => n.Kind, CreditNoticeKind.Group), ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "Could not propagate release groups onto credit notices");
        }
    }

    private async Task TryAsync(Func<Task> write, int noticeId)
    {
        try
        {
            await write();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "Could not suggest a group name for credit notice {Id}", noticeId);
        }
    }

    /// <summary>The user's decision standing in for a verdict, or null when there is none.</summary>
    private static Verdict? Overridden(Notice notice) => notice.Override switch
    {
        CreditNoticeOverride.Hide => new Verdict(true, notice.Text ?? string.Empty, notice.Id, false, ByUser: true),
        CreditNoticeOverride.Keep => new Verdict(false, notice.Text ?? string.Empty, notice.Id, false, ByUser: true),
        _ => null,
    };

    private static int Thousandths(double share) => (int)Math.Round(Math.Min(1, share) * 1000);

    private async Task<Notice?> FindAsync(
        Dictionary<int, Notice> notices, Query query, CreditRegion region, float[] fingerprint, CancellationToken ct)
    {
        var closest = notices.Values
            .Where(n => CreditNotices.SameKind(n.Whole, n.Share, query.Whole, query.Share))
            .Select(n => (Notice: n, Distance: CreditNotices.Distance(fingerprint, n.Fingerprint)))
            .Where(x => x.Distance <= CreditNotices.MaxDistance)
            .OrderBy(x => x.Distance)
            .Take(CreditNotices.Closest);
        foreach (var (notice, _) in closest)
        {
            if (await RowsAsync(notice.Id, ct) is { } rows && CreditNotices.SameRows(region, rows))
            {
                return notice;
            }
        }

        return null;
    }

    private async Task<CreditRegion?> RowsAsync(int id, CancellationToken ct)
    {
        if (_rows.TryGetValue(id, out var cached))
        {
            return cached;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var row = await db.CreditNotices.AsNoTracking().Where(n => n.Id == id)
            .Select(n => new { n.Rows, n.Height }).FirstOrDefaultAsync(ct);
        if (row is null || row.Height <= 0 || row.Rows.Length != row.Height * CreditDetector.Width)
        {
            return null;
        }

        var region = new CreditRegion(row.Rows, row.Height);
        Keep(id, region);
        return region;
    }

    private void Keep(int id, CreditRegion region)
    {
        if (_rows.Count >= MaxRows)
        {
            _rows.Clear();
        }

        _rows[id] = region;
    }

    private async Task<CreditNoticeRead?> ReadOfAsync(uint crc, long length, int share, bool bottom, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        return await db.CreditNoticeReads.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Crc == crc && r.Length == length && r.Share == share && r.Bottom == bottom, ct);
    }

    private async Task<Notice?> CreateAsync(
        Query query, CreditRegion region, float[] fingerprint, string text, bool accepted, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = new CreditNotice
        {
            Whole = query.Whole, Share = query.Whole ? 1 : query.Share, Height = query.Height, Rows = query.Rows,
            Fingerprint = CreditNotices.ToBytes(fingerprint), Accepted = accepted, Reads = 1, Text = text,
            OcrVersion = PageTextReader.Version, TextVersion = CreditText.Version, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.CreditNotices.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            logger.LogDebug(e, "Could not store a credit notice for chapter {ChapterId}", query.ChapterId);
            return null;
        }

        var notice = new Notice
        {
            Id = row.Id, Whole = row.Whole, Share = row.Share, Fingerprint = fingerprint, Accepted = accepted, Reads = 1, Text = text,
        };
        _notices![notice.Id] = notice;
        Keep(notice.Id, region);
        return notice;
    }

    private async Task AddReadAsync(Query query, int share, string text, int noticeId, CancellationToken ct)
    {
        if (query.Length <= 0)
        {
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.CreditNoticeReads.Add(new CreditNoticeRead
            {
                Crc = query.Crc, Length = query.Length, Share = share, Bottom = query.Bottom && !query.Whole, Text = text, Colour = query.Colour,
                NoticeId = noticeId, CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            logger.LogDebug(e, "Could not store a credit notice read for chapter {ChapterId}", query.ChapterId);
        }
    }

    /// <summary>
    /// Accepts a notice on <paramref name="text"/>, and sends back the chapters judged under it before,
    /// whose answer this changes (not <paramref name="chapterId"/>, which is being judged now).
    /// </summary>
    private async Task AcceptAsync(Notice notice, string text, int chapterId, bool countRead, CancellationToken ct)
    {
        var was = notice.Accepted;
        notice.Accepted = true;
        notice.Text = text;
        notice.Reads += countRead ? 1 : 0;
        await UpdateAsync(notice, ct);
        if (was)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        List<int> chapters;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            chapters = await db.ChapterCreditNotices.AsNoTracking().IgnoreQueryFilters()
                .Where(l => l.NoticeId == notice.Id && !l.Accepted && l.ChapterId != chapterId)
                .Select(l => l.ChapterId).Distinct().ToListAsync(ct);
        }

        foreach (var chapter in chapters)
        {
            await credits.RequeueAsync(chapter, ct);
        }

        if (chapters.Count > 0)
        {
            logger.LogDebug("Credit notice {Id} accepted; {Count} chapters judged under it go back for a search", notice.Id, chapters.Count);
            CreditScanWorker.Signal();
        }
    }

    private async Task UpdateAsync(Notice notice, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        await db.CreditNotices.Where(n => n.Id == notice.Id).ExecuteUpdateAsync(u => u
            .SetProperty(n => n.Accepted, notice.Accepted)
            .SetProperty(n => n.Reads, notice.Reads)
            .SetProperty(n => n.Text, notice.Text)
            .SetProperty(n => n.UpdatedAtUtc, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// The notices in memory. Those read by another OCR version go, with their reads and links (their
    /// chapters are searched again anyway); those judged under another text rule are judged again from
    /// their stored reads.
    /// </summary>
    private async Task<Dictionary<int, Notice>> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        if (await db.CreditNotices.AnyAsync(n => n.OcrVersion != PageTextReader.Version, ct))
        {
            await db.ChapterCreditNotices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
            await db.CreditNoticeReads.ExecuteDeleteAsync(ct);
            await db.CreditNotices.ExecuteDeleteAsync(ct);
            logger.LogInformation("Credit notices were read by another OCR version; cleared");
        }

        var stale = await db.CreditNotices.Where(n => n.TextVersion != CreditText.Version)
            .Select(n => new { n.Id, n.Text }).ToListAsync(ct);
        foreach (var notice in stale)
        {
            var reads = await db.CreditNoticeReads.AsNoTracking().Where(r => r.NoticeId == notice.Id).OrderBy(r => r.Id)
                .Select(r => new { r.Text, r.Colour }).ToListAsync(ct);
            // A notice read from a page whose bytes were unknown has no stored read: its own text, without the colour hint.
            if (reads.Count == 0 && notice.Text is not null)
            {
                reads.Add(new { Text = notice.Text, Colour = false });
            }

            var accepting = reads.FirstOrDefault(r => CreditText.IsScanlator(r.Text, r.Colour));
            var text = accepting?.Text ?? reads.LastOrDefault()?.Text;
            await db.CreditNotices.Where(n => n.Id == notice.Id).ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Accepted, accepting != null)
                .SetProperty(n => n.Text, text)
                .SetProperty(n => n.TextVersion, CreditText.Version), ct);
        }

        // Suggestions from before this version of the name reader, or from before there was one.
        var unnamed = await db.CreditNotices.AsNoTracking().Where(n => n.NameVersion != CreditGroupName.Version)
            .Select(n => new NoticeText(n.Id, n.Text, n.SuggestedName, n.Invite, n.NameVersion)).ToListAsync(ct);
        if (unnamed.Count > 0)
        {
            await SuggestAsync(db, unnamed, await KnownNamesAsync(db, ct), ct);
        }

        var rows = await db.CreditNotices.AsNoTracking()
            .Select(n => new { n.Id, n.Whole, n.Share, n.Fingerprint, n.Accepted, n.Reads, n.Text, n.Override })
            .ToListAsync(ct);
        var notices = new Dictionary<int, Notice>();
        foreach (var row in rows)
        {
            if (CreditNotices.FromBytes(row.Fingerprint) is { } fingerprint)
            {
                notices[row.Id] = new Notice
                {
                    Id = row.Id, Whole = row.Whole, Share = row.Share, Fingerprint = fingerprint, Accepted = row.Accepted,
                    Reads = row.Reads, Text = row.Text, Override = row.Override,
                };
            }
        }

        return notices;
    }
}
