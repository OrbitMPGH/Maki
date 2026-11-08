using System.Diagnostics;
using Maki.Core.Io;

namespace Maki.Core.Tests;

public class FileIdentityTests
{
    [Fact]
    public void Two_files_differ_and_a_second_name_for_one_folder_is_the_same_file()
    {
        var root = Directory.CreateTempSubdirectory("maki-file-identity-").FullName;
        var link = root + "-link";
        try
        {
            var a = Path.Combine(root, "a.cbz");
            var b = Path.Combine(root, "b.cbz");
            File.WriteAllText(a, "x");
            File.WriteAllText(b, "x");

            Assert.Equal(FileSameness.Same, FileIdentity.Compare(a, a));
            Assert.Equal(FileSameness.Unknown, FileIdentity.Compare(a, Path.Combine(root, "absent.cbz")));
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                return;
            }

            Assert.Equal(FileSameness.Different, FileIdentity.Compare(a, b));
            if (File.Exists(Path.Combine(root, "A.CBZ")))
            {
                Assert.Equal(FileSameness.Same, FileIdentity.Compare(a, Path.Combine(root, "A.CBZ")));
            }

            if (MakeDirectoryLink(link, root))
            {
                Assert.Equal(FileSameness.Same, FileIdentity.Compare(a, Path.Combine(link, "a.cbz")));
            }
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool MakeDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }

        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && Directory.Exists(link);
    }
}
