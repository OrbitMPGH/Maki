using System.Text;

namespace Maki.Core.Tests;

/// <summary>
/// Writes a minimal but structurally valid PDF 1.4 with <c>count</c> blank pages, so the reader
/// tests exercise real PDFium parsing without dragging in a PDF-writing dependency.
/// </summary>
internal static class PdfFixture
{
    public static string Write(string path, int count, int width = 300, int height = 450)
    {
        var body = new MemoryStream();
        var offsets = new List<long> { 0 };

        void Obj(int number, string content)
        {
            offsets.Add(body.Position);
            var bytes = Encoding.ASCII.GetBytes($"{number} 0 obj\n{content}\nendobj\n");
            body.Write(bytes, 0, bytes.Length);
        }

        var header = Encoding.ASCII.GetBytes("%PDF-1.4\n%âãÏÓ\n");
        body.Write(header, 0, header.Length);

        var kids = string.Join(" ", Enumerable.Range(0, count).Select(i => $"{i + 3} 0 R"));
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Count {count} /Kids [{kids}] >>");
        for (var i = 0; i < count; i++)
            Obj(i + 3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << >> >>");

        var xref = body.Position;
        var trailer = new StringBuilder();
        trailer.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++) trailer.Append($"{offsets[i]:D10} 00000 n \n");
        trailer.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var tail = Encoding.ASCII.GetBytes(trailer.ToString());
        body.Write(tail, 0, tail.Length);

        File.WriteAllBytes(path, body.ToArray());
        return path;
    }
}
