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
/// from whatever file this returns, which is why swapping the file is enough. <see cref="Apply"/>
/// then names the encoding on the response. Anything without a sibling falls through to the plain
/// file.
/// </para>
/// </summary>
public sealed class PrecompressedFileProvider(IFileProvider inner, IHttpContextAccessor accessor) : IFileProvider
{
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
        foreach (var (token, extension) in Encodings)
        {
            if (!accepted.Any(e => e.Quality != 0 && string.Equals(e.Value.Value, token, StringComparison.OrdinalIgnoreCase)))
                continue;

            var sibling = inner.GetFileInfo(subpath + extension);
            if (sibling.Exists)
                return sibling;
        }

        return plain;
    }

    public static void Apply(StaticFileResponseContext context)
    {
        var headers = context.Context.Response.Headers;
        headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);

        var name = context.File.Name;
        if (name.EndsWith(".br", StringComparison.Ordinal))
            headers.ContentEncoding = "br";
        else if (name.EndsWith(".gz", StringComparison.Ordinal))
            headers.ContentEncoding = "gzip";
    }
}
