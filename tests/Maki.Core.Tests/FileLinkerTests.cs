using Maki.Core.Storage;

namespace Maki.Core.Tests;

public class FileLinkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-linker-" + Guid.NewGuid().ToString("N"));

    public FileLinkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Place_WithoutHardlink_Copies()
    {
        var source = Write("source.cbz", "payload");
        var target = Path.Combine(_dir, "target.cbz");

        Assert.Equal(FilePlacement.Copied, FileLinker.Place(source, target, preferHardlink: false));
        Assert.Equal("payload", File.ReadAllText(target));

        // Independent files: writing one leaves the other alone.
        File.WriteAllText(target, "changed");
        Assert.Equal("payload", File.ReadAllText(source));
    }

    [Fact]
    public void Place_WithHardlink_SharesContentInSameFolder()
    {
        var source = Write("source.cbz", "payload");
        var target = Path.Combine(_dir, "linked.cbz");

        // Both paths are in one temp folder, so the link is expected to succeed; a filesystem
        // that can't do it (a mounted share under CI) falls back to a copy, which is also correct.
        var placement = FileLinker.Place(source, target, preferHardlink: true);
        Assert.Equal("payload", File.ReadAllText(target));

        if (placement == FilePlacement.Hardlinked)
        {
            File.WriteAllText(source, "rewritten in place");
            Assert.Equal("rewritten in place", File.ReadAllText(target));
        }
    }

    [Fact]
    public void Hardlink_SurvivesAnAtomicRewriteOfTheOtherName()
    {
        // How ComicInfoUpdater rewrites an archive: build a sibling, then move it over the name.
        // The bytes behind the other link (the still-seeding torrent) must not change.
        var source = Write("seeding.cbz", "original");
        var target = Path.Combine(_dir, "library.cbz");
        if (FileLinker.Place(source, target, preferHardlink: true) != FilePlacement.Hardlinked)
        {
            return;
        }

        var partial = target + ".partial";
        File.WriteAllText(partial, "standardized");
        File.Move(partial, target, overwrite: true);

        Assert.Equal("standardized", File.ReadAllText(target));
        Assert.Equal("original", File.ReadAllText(source));
    }

    [Fact]
    public void TryHardlink_FailsWhenTargetExists()
    {
        var source = Write("source.cbz", "payload");
        var target = Write("taken.cbz", "existing");

        Assert.False(FileLinker.TryHardlink(source, target));
        Assert.Equal("existing", File.ReadAllText(target));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
