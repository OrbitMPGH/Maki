using System.Reflection;
using System.Security.Cryptography;
using Maki.Api.Controllers;
using Maki.Core.Reading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="HealthController"/>'s archive-preview path branches on file type: a CBZ opens the
/// zip entry the finding names, a PDF renders the page instead. Both compare the fresh bytes
/// against the finding's stored hash before serving anything, so a page whose content changed
/// since the last scan is refused rather than served stale.
/// </summary>
public sealed class HealthPdfPreviewTests
{
    /// <summary><c>VerifiedPreview</c> is private and rightly so; everything it touches besides
    /// <c>HttpContext</c> is unused on the PDF branch, so the rest of the controller's
    /// dependencies can stay null for a focused test of the fix.</summary>
    private static async Task<IActionResult> VerifiedPreview(string path, string? hash, PageFingerprint page)
    {
        var controller = new HealthController(null!, null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var method = typeof(HealthController).GetMethod("VerifiedPreview", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<IActionResult>)method.Invoke(controller, [path, hash, page, CancellationToken.None])!;
    }

    [Fact]
    public async Task AVerifiedPdfPagePreviewServesTheRenderedImage()
    {
        var path = Path.Combine(Path.GetTempPath(), "maki-health-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        PdfFixture.Write(path, count: 1);
        try
        {
            var fileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            using var rendered = await PdfReader.RenderPageAsync(path, 0, PdfReader.FingerprintEdge);
            var pageHash = Convert.ToHexString(SHA256.HashData(rendered.ToArray()));
            var page = new PageFingerprint("0001.jpg", pageHash, 0, 0);

            var result = await VerifiedPreview(path, fileHash, page);

            var file = Assert.IsAssignableFrom<FileResult>(result);
            Assert.Equal("image/jpeg", file.ContentType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task APdfPagePreviewWithAMismatchedHashIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "maki-health-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        PdfFixture.Write(path, count: 1);
        try
        {
            var fileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var page = new PageFingerprint("0001.jpg", "not-the-real-hash", 0, 0);

            var result = await VerifiedPreview(path, fileHash, page);

            Assert.IsType<ConflictResult>(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task APdfPagePreviewForAnUnparsablePageNameIsNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), "maki-health-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
        PdfFixture.Write(path, count: 1);
        try
        {
            var fileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var page = new PageFingerprint("not-a-page-name.png", "irrelevant", 0, 0);

            var result = await VerifiedPreview(path, fileHash, page);

            Assert.IsType<NotFoundResult>(result);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
