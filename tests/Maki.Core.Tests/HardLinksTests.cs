using Maki.Core.Paths;
using Maki.Core.Storage;

namespace Maki.Core.Tests;

public class HardLinksTests
{
    [Fact]
    public void A_hardlinked_file_reports_as_shared()
    {
        var dir = Directory.CreateTempSubdirectory("maki-hardlinks-").FullName;
        try
        {
            var original = Path.Combine(dir, "a.cbz");
            File.WriteAllText(original, "bytes");
            if (HardLinks.Count(original) is null)
            {
                return;
            }

            Assert.False(HardLinks.IsShared(original));
            if (!FileLinker.TryHardlink(original, Path.Combine(dir, "b.cbz")))
            {
                return;
            }

            Assert.Equal(2, HardLinks.Count(original));
            Assert.True(HardLinks.IsShared(original));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
