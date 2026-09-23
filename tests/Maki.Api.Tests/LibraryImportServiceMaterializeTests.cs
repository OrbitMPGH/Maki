using System.Reflection;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="LibraryImportService.MaterializeComics"/> decides, per comic the scanner finds,
/// whether to leave it where it is or build a CBZ out of it. A PDF must take the same
/// leave-it-in-place branch a CBZ already does: it is placed as it is (see
/// <c>downloads.md</c>), so a nested one that got copied to the folder's top level as well would
/// leave two files backing the same chapter once both get linked.
/// </summary>
public class LibraryImportServiceMaterializeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-materialize-" + Guid.NewGuid().ToString("N")[..8]);

    public LibraryImportServiceMaterializeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Only <c>logger</c> is touched by <c>MaterializeComics</c>; every other constructor
    /// dependency is unused on this path, so the rest can stay null for a focused test of the
    /// method via reflection (it is private, and rightly so, nothing outside the service should
    /// call it directly).
    /// </summary>
    private static List<string> Materialize(string targetDir)
    {
        var service = new LibraryImportService(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<LibraryImportService>.Instance);

        var method = typeof(LibraryImportService)
            .GetMethod("MaterializeComics", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (List<string>)method.Invoke(service, [targetDir])!;
    }

    [Fact]
    public void ANestedPdfIsLeftInPlaceRatherThanCopiedToTheTopLevel()
    {
        var subDir = Path.Combine(_root, "v01");
        Directory.CreateDirectory(subDir);
        var nested = Path.Combine(subDir, "Look Back.pdf");
        PdfFixture.Write(nested, count: 1);

        var files = Materialize(_root);

        var file = Assert.Single(files);
        Assert.Equal(nested, file);
        // Nothing was built at the top level from it.
        Assert.False(File.Exists(Path.Combine(_root, "Look Back.pdf")));
    }
}
