using System.Diagnostics;
using System.Text;
using Maki.Core.Images;
using Maki.Core.Reading;
using Microsoft.Data.Sqlite;

namespace Maki.Api;

/// <summary>
/// <c>Maki.Api --selftest</c>: loads every native library Maki ships and exits non-zero if one does
/// not. The release smoke tests run it against each archive, because pdfium and libvips load lazily:
/// a build missing them still starts and serves pages, and only fails on the first PDF or image.
/// </summary>
public static class SelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = 0;
        void Check(string name, Func<string> probe)
        {
            try
            {
                output.WriteLine($"ok    {name}: {probe()}");
            }
            catch (Exception ex)
            {
                failures++;
                output.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Check("sqlite", () =>
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select sqlite_version()";
            return $"version {command.ExecuteScalar()}";
        });

        Check("pdfium + libvips", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"maki-selftest-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(path, OnePagePdf());
            try
            {
                if (PdfReader.PageCount(path) != 1)
                    throw new InvalidOperationException("pdfium did not read the one-page test document");

                using var jpeg = PdfReader.RenderPage(path, 0, 64);
                var raster = Raster.Decode(jpeg.ToArray());
                return $"rendered {raster.Width}x{raster.Height}";
            }
            finally
            {
                File.Delete(path);
            }
        });

        Check("onnxruntime", () =>
        {
            var providers = Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetAvailableProviders();
            return string.Join(", ", providers);
        });

        Check("playwright driver", () =>
        {
            var nodeRoot = Path.Combine(AppContext.BaseDirectory, ".playwright", "node");
            var node = Directory.Exists(nodeRoot)
                ? Directory.GetFiles(nodeRoot, OperatingSystem.IsWindows() ? "node.exe" : "node", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (node is null)
                throw new FileNotFoundException("no Node driver under .playwright/node");

            using var process = Process.Start(new ProcessStartInfo(node, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var version = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0)
                throw new InvalidOperationException($"node exited with {process.ExitCode}");
            return $"node {version}";
        });

        Check("web ui", () => File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"))
            ? "wwwroot/index.html present"
            : throw new FileNotFoundException("wwwroot/index.html is missing"));

        output.WriteLine(failures == 0 ? "selftest passed" : $"selftest failed: {failures} check(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The smallest PDF pdfium accepts without repairing it: one blank page, with a real
    /// cross-reference table so the parse takes the normal path rather than the recovery one.</summary>
    private static byte[] OnePagePdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
