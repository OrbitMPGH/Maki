using System.IO.Compression;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.Json;
using Maki.Api.Hubs;
using Maki.Core.Reading;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
namespace Maki.Api.Tests;
public class HealthWorkspaceTests : IDisposable
{
    private readonly TestDb fixture = new();
    private readonly string root = Directory.CreateTempSubdirectory("maki-health-workspace-").FullName;
    public void Dispose() { fixture.Dispose(); Directory.Delete(root,true); }
    private HealthOperationService Operations(MakiDbContext db) => new(db,null!,null!,new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new EventBroadcaster(new NoopHubContext(), fixture.ScopeFactory()), new KavitaScanService(null!,null!,fixture.ScopeFactory(),NullLogger<KavitaScanService>.Instance));
    private async Task<HealthFile> Seed(MakiDbContext db, bool tracked = false)
    {
        var folder = new RootFolder { Path=root }; db.RootFolders.Add(folder); await db.SaveChangesAsync();
        var path = Path.Combine(root,"one.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { using var stream=zip.CreateEntry("ComicInfo.xml").Open(); stream.Write("<ComicInfo/>"u8); }
        var file = new HealthFile { RootFolderId=folder.Id,RelativePath="one.cbz" }; db.HealthFiles.Add(file);
        if(tracked)
        {
            var series=new Series {Title="Health test",SortTitle="health test",RootFolderId=folder.Id,FolderName="Test"}; db.Series.Add(series); await db.SaveChangesAsync();
            var cf=new ChapterFile { SeriesId=series.Id,RelativePath="one.cbz",Size=new FileInfo(path).Length }; db.ChapterFiles.Add(cf); await db.SaveChangesAsync();
            file.ChapterFileId=cf.Id; file.SeriesId=series.Id;
            db.Chapters.AddRange(new Chapter {SeriesId=series.Id,ChapterFileId=cf.Id,Number=1,Wanted=true},new Chapter {SeriesId=series.Id,ChapterFileId=cf.Id,Number=2,Wanted=false});
        }
        await db.SaveChangesAsync(); await new HealthScanService(db).AnalyzeAsync(file,root,true,default); return file;
    }
    private async Task<(RootFolder Folder, Series Series)> SeedSeries(MakiDbContext db)
    {
        var folder = new RootFolder { Path = root }; db.RootFolders.Add(folder); await db.SaveChangesAsync();
        var series = new Series { Title = "Match test", SortTitle = "match test", RootFolderId = folder.Id, FolderName = "Match test" };
        db.Series.Add(series); await db.SaveChangesAsync();
        return (folder, series);
    }
    [Fact] public async Task Unlinked_archive_names_the_rival_file_holding_its_chapter()
    {
        using var db = fixture.NewContext();
        var (folder, series) = await SeedSeries(db);
        var linked = new ChapterFile { SeriesId = series.Id, RelativePath = Path.Combine("Match test", "Match test 003.cbz"), Size = 10, SourceName = "MangaDex" };
        db.ChapterFiles.Add(linked); await db.SaveChangesAsync();
        db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 3, ChapterFileId = linked.Id });
        var rival = new HealthFile { RootFolderId = folder.Id, RelativePath = Path.Combine("Match test", "Match test 003 [dup].cbz") };
        db.HealthFiles.Add(rival); await db.SaveChangesAsync();
        var match = await new HealthMatchService(db).MatchAsync(rival, default);
        Assert.Equal(series.Id, match!.SeriesId);
        Assert.Equal("Chapter 3", match.Label);
        Assert.Equal(linked.Id, Assert.Single(match.Counterparts).ChapterFileId);
    }
    [Fact] public async Task Unlinked_archive_for_a_chapter_with_no_file_offers_no_comparison()
    {
        using var db = fixture.NewContext();
        var (folder, series) = await SeedSeries(db);
        db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 4 });
        var free = new HealthFile { RootFolderId = folder.Id, RelativePath = Path.Combine("Match test", "Match test 004.cbz") };
        db.HealthFiles.Add(free); await db.SaveChangesAsync();
        var match = await new HealthMatchService(db).MatchAsync(free, default);
        Assert.Single(match!.Chapters);
        Assert.Empty(match.Counterparts);
    }
    [Fact] public async Task Archive_outside_every_series_folder_has_no_owner()
    {
        using var db = fixture.NewContext();
        var (folder, _) = await SeedSeries(db);
        var loose = new HealthFile { RootFolderId = folder.Id, RelativePath = Path.Combine("Loose", "Something 001.cbz") };
        db.HealthFiles.Add(loose); await db.SaveChangesAsync();
        var match = await new HealthMatchService(db).MatchAsync(loose, default);
        Assert.Null(match!.SeriesId);
        Assert.Empty(match.Counterparts);
    }
    [Fact] public async Task A_linked_archive_is_never_offered_a_match()
    {
        using var db = fixture.NewContext();
        var file = await Seed(db, true);
        Assert.Null(await new HealthMatchService(db).MatchAsync(file, default));
    }
    /// <summary>An archive of `pages` distinct pages plus `copies` of one repeated page.</summary>
    private async Task<HealthFile> SeedPages(MakiDbContext db, int pages, int copies, bool blank = false)
    {
        var folder = new RootFolder { Path=root }; db.RootFolders.Add(folder); await db.SaveChangesAsync();
        var name = $"{Guid.NewGuid():N}.cbz";
        var path = Path.Combine(root,name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (var i = 0; i < pages; i++)
            {
                using var stream = zip.CreateEntry($"{i:000}.png").Open();
                using var image = new Image<Rgba32>(16, 16);
                // Distinct pages get their own gradient; the copies share one, so they hash alike.
                // 99 is outside the range the distinct pages use, and gives a patterned page - a
                // flat one would be classified blank and never reach the repetition check at all.
                var repeated = i >= pages - copies;
                var seed = repeated ? 99 : i + 1;
                if (!blank || !repeated)
                    for (var y = 0; y < 16; y++)
                    for (var x = 0; x < 16; x++) image[x,y] = new Rgba32((byte)(x*seed), (byte)(y*seed), (byte)(seed*9));
                else
                    for (var y = 0; y < 16; y++)
                    for (var x = 0; x < 16; x++) image[x,y] = new Rgba32(255,255,255);
                image.SaveAsPng(stream);
            }
        }
        var file = new HealthFile { RootFolderId=folder.Id, RelativePath=name };
        db.HealthFiles.Add(file); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(file,root,true,default);
        return file;
    }
    [Fact] public async Task A_reused_page_in_a_long_archive_is_not_a_finding()
    {
        using var db=fixture.NewContext();
        // Two copies of one spread in forty pages is a recap, not a broken download.
        var file=await SeedPages(db,40,2);
        Assert.Contains(HealthScanService.Analysis(file).Groups,g=>g.Kind=="exact");
        Assert.DoesNotContain(db.HealthFindings,f=>f.FileId==file.Id && f.Kind=="pageRepetition");
    }
    [Fact] public async Task An_archive_that_is_mostly_one_page_is_a_finding()
    {
        using var db=fixture.NewContext();
        var file=await SeedPages(db,12,9);
        Assert.Contains(db.HealthFindings,f=>f.FileId==file.Id && f.Kind=="pageRepetition" && f.State=="open");
    }
    [Fact] public async Task Blank_pages_are_evidence_on_the_file_and_never_a_finding()
    {
        using var db=fixture.NewContext();
        var file=await SeedPages(db,12,9,blank:true);
        Assert.Contains(HealthScanService.Analysis(file).Groups,g=>g.Kind=="blank" && g.Pages.Count==9);
        Assert.DoesNotContain(db.HealthFindings,f=>f.FileId==file.Id && f.Kind=="blankRepetition");
        Assert.DoesNotContain(db.HealthFindings,f=>f.FileId==file.Id && f.Kind=="pageRepetition");
    }
    [Fact] public async Task A_replacement_needs_a_source_whether_or_not_one_was_named()
    {
        using var db=fixture.NewContext(); var file=await Seed(db,true); var service=Operations(db);
        // Automatic is not a way past having nothing mapped; it only means "you pick which".
        var automatic=await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(file.Id,file.Version,null,1,default));
        Assert.Equal("This series has no enabled source mappings",automatic.Message);
        var named=await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestAsync(file.Id,file.Version,999,1,default));
        Assert.Equal("Select an enabled source mapped to this series",named.Message);
    }
    [Fact] public void Every_health_action_and_preview_is_admin_only()
    {
        Assert.Equal(Policies.Admin,typeof(HealthController).GetCustomAttribute<AuthorizeAttribute>()?.Policy);
        Assert.DoesNotContain(typeof(HealthController).GetMethods(),m=>m.GetCustomAttribute<AllowAnonymousAttribute>()!=null);
        Assert.Equal(Policies.Admin,typeof(SystemController).GetMethod("Health")!.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
    }
    [Fact] public async Task Ignores_survive_rescan_but_new_content_reopens()
    {
        using var db=fixture.NewContext(); var file=await Seed(db);
        var finding=await db.HealthFindings.FirstAsync(f=>f.Kind=="noPages"); finding.State="ignored"; await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(file,root,true,default);
        Assert.Equal("ignored",finding.State);
        using(var zip=ZipFile.Open(Path.Combine(root,"one.cbz"),ZipArchiveMode.Update)) { using var stream=zip.CreateEntry("new.txt").Open(); stream.WriteByte(42); }
        await new HealthScanService(db).AnalyzeAsync(file,root,true,default);
        Assert.Equal("resolved",finding.State);
        Assert.Contains(db.HealthFindings,f=>f.Version==file.Version && f.Kind=="noPages" && f.State=="open");
    }
    [Fact] public async Task Shared_file_deletion_preserves_chapters_history_and_wanted()
    {
        using var db=fixture.NewContext(); var file=await Seed(db,true);
        var chapters=await db.Chapters.OrderBy(c=>c.Id).ToListAsync();
        db.ReaderBookmarks.Add(new(){UserId=1,SeriesId=chapters[0].SeriesId,ChapterId=chapters[0].Id,PageIndex=2}); await db.SaveChangesAsync();
        var service=Operations(db); var op=await service.PreviewDeleteAsync(file.Id,file.Version,1,default);
        await service.ApplyAsync(op.Id,file.Version,true,false,default);
        Assert.False(File.Exists(Path.Combine(root,"one.cbz"))); Assert.Empty(db.ChapterFiles);
        Assert.Equal(2,await db.Chapters.CountAsync()); Assert.True(chapters[0].Wanted); Assert.False(chapters[1].Wanted);
        Assert.All(chapters,c=>Assert.Null(c.ChapterFileId)); Assert.Single(db.ReaderBookmarks); Assert.Equal("completed",op.Status);
    }
    [Fact] public async Task Deleting_a_missing_archive_frees_its_chapters()
    {
        using var db=fixture.NewContext(); var file=await Seed(db,true);
        File.Delete(Path.Combine(root,"one.cbz"));
        await new HealthScanService(db).AnalyzeAsync(file,root,true,default);
        Assert.Equal(-1,file.Size); Assert.Contains(db.HealthFindings,f=>f.Kind=="missing");
        var service=Operations(db); var op=await service.PreviewDeleteAsync(file.Id,file.Version,1,default);
        // Nothing to unlink is the whole point: the chapters read as downloaded until the record goes.
        await service.ApplyAsync(op.Id,file.Version,true,false,default);
        Assert.Equal("completed",op.Status); Assert.Empty(db.ChapterFiles); Assert.True(file.Removed);
        Assert.All(await db.Chapters.ToListAsync(),c=>Assert.Null(c.ChapterFileId));
        Assert.Equal(2,await db.Chapters.CountAsync());
    }
    [Fact] public async Task Stale_review_cannot_delete_changed_bytes()
    {
        using var db=fixture.NewContext(); var file=await Seed(db);
        var service=Operations(db);var op=await service.PreviewDeleteAsync(file.Id,file.Version,1,default);
        await File.AppendAllTextAsync(Path.Combine(root,"one.cbz"),"changed");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(op.Id,file.Version,true,false,default)); Assert.True(File.Exists(Path.Combine(root,"one.cbz")));
    }
    [Fact] public async Task Confirmation_is_required_and_path_escape_is_rejected()
    {
        using var db=fixture.NewContext();var file=await Seed(db);var service=Operations(db);
        var op=await service.PreviewDeleteAsync(file.Id,file.Version,1,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(op.Id,file.Version,false,false,default));
        Assert.Throws<InvalidOperationException>(()=>HealthPaths.Resolve(root,"../outside.cbz"));
    }
    [Fact] public async Task Interrupted_deletion_finishes_links_only_when_file_is_gone()
    {
        using var db=fixture.NewContext();var file=await Seed(db,true);var service=Operations(db);
        var op=await service.PreviewDeleteAsync(file.Id,file.Version,1,default);op.Status="deleting";await db.SaveChangesAsync();
        File.Delete(Path.Combine(root,"one.cbz"));await service.RecoverAsync(default);
        Assert.Equal("completed",op.Status);Assert.Empty(db.ChapterFiles);Assert.Equal(2,await db.Chapters.CountAsync());
    }
    [Fact] public async Task Unavailable_root_does_not_resolve_prior_findings()
    {
        using var db=fixture.NewContext();var file=await Seed(db); var scan=new HealthScan();db.HealthScans.Add(scan);await db.SaveChangesAsync();
        var folder=await db.RootFolders.SingleAsync();folder.Path=Path.Combine(root,"unavailable");await db.SaveChangesAsync();
        await new HealthScanService(db).RunAsync(scan,default);Assert.Equal("partial",scan.Status);Assert.Contains(db.HealthFindings,f=>f.State=="open");
    }

    private async Task<HealthOperation> Stage(MakiDbContext db, HealthFile file)
    {
        var op = new HealthOperation { FileId=file.Id,Version=file.Version,Status="review",UserId=1 };
        db.HealthOperations.Add(op);await db.SaveChangesAsync();
        var candidates=new List<RepairCandidate>();
        foreach(var chapter in await db.Chapters.ToListAsync())
        {
            var relative=$".maki/health/{op.Id}/chapter-{chapter.Id}.cbz";
            var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
            { using var stream=zip.CreateEntry("001.png").Open(); using var image=new Image<Rgba32>(8,8); image.SaveAsPng(stream); }
            var analysis=await ArchiveHealthAnalyzer.AnalyzeAsync(path);
            candidates.Add(new(chapter.Id,relative,analysis.Hash!,analysis));
        }
        op.JournalJson=JsonSerializer.Serialize(candidates,HealthScanService.Json);await db.SaveChangesAsync();return op;
    }
    [Fact] public async Task Shared_volume_repair_preserves_read_history_and_resets_all_users_positions()
    {
        using var db=fixture.NewContext();var file=await Seed(db,true);var chapters=await db.Chapters.OrderBy(c=>c.Id).ToListAsync();
        var other=fixture.SeedUser("other");
        foreach(var userId in new[]{1,other})
        {
            db.ReaderBookmarks.Add(new(){UserId=userId,SeriesId=chapters[0].SeriesId,ChapterId=chapters[0].Id,PageIndex=4});
            db.ChapterProgress.Add(new(){UserId=userId,SeriesId=chapters[0].SeriesId,ChapterId=chapters[0].Id,PageIndex=4,PageCount=5,Completed=true,ReadSeconds=120});
        }
        await db.SaveChangesAsync();var op=await Stage(db,file);var service=Operations(db);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ApplyAsync(op.Id,file.Version,true,false,default));
        Assert.True(File.Exists(Path.Combine(root,"one.cbz")));
        await service.ApplyAsync(op.Id,file.Version,true,true,default);
        Assert.Equal("completed",op.Status);Assert.Equal(2,await db.ChapterFiles.CountAsync());Assert.Empty(db.ReaderBookmarks);
        Assert.All(db.ChapterProgress,p=>{Assert.Equal(0,p.PageIndex);Assert.True(p.Completed);Assert.Equal(120,p.ReadSeconds);});
        Assert.True(chapters[0].Wanted);Assert.False(chapters[1].Wanted);Assert.Empty(db.StatsEvents);
    }
    [Fact] public async Task Changed_candidate_is_rejected_before_original_is_moved()
    {
        using var db=fixture.NewContext();var file=await Seed(db,true);var op=await Stage(db,file);
        await File.AppendAllTextAsync(Path.Combine(root,HealthOperationService.Candidates(op)[0].RelativePath),"changed");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Operations(db).ApplyAsync(op.Id,file.Version,true,true,default));
        Assert.True(File.Exists(Path.Combine(root,"one.cbz")));Assert.Single(db.ChapterFiles);
    }
    [Fact] public async Task Interrupted_replacement_restores_original_and_keeps_links()
    {
        using var db=fixture.NewContext();var file=await Seed(db,true);var op=await Stage(db,file);
        var rollback=Path.Combine(root,$".maki/health/{op.Id}/original.cbz");File.Move(Path.Combine(root,"one.cbz"),rollback);
        op.Status="applying";await db.SaveChangesAsync();await Operations(db).RecoverAsync(default);
        Assert.True(File.Exists(Path.Combine(root,"one.cbz")));Assert.Equal("failed",op.Status);Assert.Single(db.ChapterFiles);Assert.All(db.Chapters,c=>Assert.NotNull(c.ChapterFileId));
    }
}
