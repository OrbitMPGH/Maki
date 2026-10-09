using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Maki.Api;

/// <summary>
/// Serves the <c>.br</c> or <c>.gz</c> file the Vite build wrote beside an asset when the request
/// accepts that encoding, so a static file is compressed once at build time instead of on every
/// response.
/// <para>
/// The static file middleware picks the content type from the requested path and reads the bytes
/// from whatever file this returns, which is why swapping the file is enough. The swapped file is
/// wrapped in <see cref="EncodedFileInfo"/> so <see cref="Apply"/> knows a swap happened, rather than
/// guessing from a file name. A sibling older than its source is ignored, so a hand-edited file
/// never serves stale bytes. Anything without a usable sibling falls through to the plain file.
/// </para>
/// </summary>
public sealed class PrecompressedFileProvider(IFileProvider inner, IHttpContextAccessor accessor) : IFileProvider
{
    private const string HasSiblingKey = "Maki.Precompressed.HasSibling";

    private static readonly (string Token, string Extension)[] Encodings = [("br", ".br"), ("gzip", ".gz")];

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) => inner.Watch(filter);

    public IFileInfo GetFileInfo(string subpath)
    {
        var plain = inner.GetFileInfo(subpath);
        if (!plain.Exists || accessor.HttpContext is not { } context ||
            !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return plain;
        }

        var accepted = context.Request.GetTypedHeaders().AcceptEncoding;
        IFileInfo? chosen = null;
        foreach (var (token, extension) in Encodings)
        {
            var sibling = inner.GetFileInfo(subpath + extension);
            if (!sibling.Exists)
                continue;

            context.Items[HasSiblingKey] = true;
            if (chosen is null && sibling.LastModified >= plain.LastModified &&
                accepted.Any(e => e.Quality != 0 && string.Equals(e.Value.Value, token, StringComparison.OrdinalIgnoreCase)))
            {
                chosen = new EncodedFileInfo(sibling, token);
            }
        }

        return chosen ?? plain;
    }

    public static void Apply(StaticFileResponseContext context)
    {
        var http = context.Context;
        var response = http.Response;
        if (http.Items.ContainsKey(HasSiblingKey))
            response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        if (context.File is EncodedFileInfo encoded &&
            response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status206PartialContent)
        {
            response.Headers.ContentEncoding = encoded.Encoding;
        }
    }

    private sealed class EncodedFileInfo(IFileInfo file, string encoding) : IFileInfo
    {
        public string Encoding { get; } = encoding;
        public bool Exists => file.Exists;
        public long Length => file.Length;
        public string? PhysicalPath => file.PhysicalPath;
        public string Name => file.Name;
        public DateTimeOffset LastModified => file.LastModified;
        public bool IsDirectory => file.IsDirectory;
        public Stream CreateReadStream() => file.CreateReadStream();
    }
}
