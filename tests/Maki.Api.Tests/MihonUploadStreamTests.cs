using System.IO.Compression;
using System.Net.Http.Headers;
using Maki.Core.Mihon;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Maki.Api.Tests;

/// <summary>
/// <c>MihonImportController.Parse</c> hands the form file's stream to the parser, which reads it
/// synchronously. That is only safe because form binding buffers the whole body first, so the reads
/// never reach Kestrel's request stream, which refuses synchronous IO. This pins that.
/// </summary>
public class MihonUploadStreamTests
{
    [Fact]
    public async Task A_bound_form_file_can_be_parsed_with_synchronous_reads()
    {
        // Over the 64 KB a form section keeps in memory, so the upload is read back from a temp file.
        byte[] manga = [0x1A, 0x05, (byte)'T', (byte)'i', (byte)'t', (byte)'l', (byte)'e', 0x2A, 0xA0, 0x8D, 0x06,
            .. Enumerable.Repeat((byte)'x', 100_000)];
        byte[] raw = [0x0A, 0xAB, 0x8D, 0x06, .. manga];
        var backup = Gzip(raw);
        var filler = new byte[200_000];
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(filler), "padding", "padding.bin" },
            { new ByteArrayContent(backup), "file", "backup.tachibk" },
            { new ByteArrayContent(raw), "raw", "raw.tachibk" },
        };
        form.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("charset", "utf-8"));

        var context = new DefaultHttpContext();
        context.Request.ContentType = form.Headers.ContentType.ToString();
        context.Request.Body = new AsyncOnlyStream(await form.ReadAsByteArrayAsync());

        var files = (await context.Request.ReadFormAsync()).Files;
        foreach (var name in new[] { "file", "raw" })
        {
            await using var upload = files.GetFile(name)!.OpenReadStream();
            Assert.Equal("Title", Assert.Single(MihonBackupReader.Read(upload).Manga).Title);
        }
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>A request body that, like Kestrel's, refuses synchronous reads.</summary>
    private sealed class AsyncOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, count, ct);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
