using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maki.Core.Http;
using Maki.Core.Sources;

namespace Maki.Sources.Suwayomi;

/// <summary>A GraphQL error Suwayomi answered with, as a site error so the download queue shows its text.</summary>
public sealed class SuwayomiGraphQlException(string message, IReadOnlyList<string> messages) : SourceErrorException(message)
{
    public IReadOnlyList<string> Messages { get; } = messages;

    /// <summary>
    /// Whether the server's schema lacks <paramref name="field"/>, so the caller can fall back to an
    /// older query. graphql-java names the field in its validation error ("FieldUndefined").
    /// </summary>
    public bool IsUndefinedField(string field) =>
        Messages.Any(m => m.Contains(field, StringComparison.Ordinal) &&
                          (m.Contains("undefined", StringComparison.OrdinalIgnoreCase) ||
                           m.Contains("Validation", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>One GraphQL POST to Suwayomi and the mapping of its failures onto what Maki already handles.</summary>
internal static class SuwayomiGraphQl
{
    /// <summary>Set on a request the auth handler must pass through untouched (the login and refresh calls).</summary>
    public static readonly HttpRequestOptionsKey<bool> SkipAuth = new("Maki.Suwayomi.SkipAuth");

    /// <summary>
    /// Under a login mode Suwayomi answers an unauthenticated query with 200 and this as an
    /// <c>errors[].message</c> (see <see cref="IsUnauthorized"/>), not a 401.
    /// </summary>
    public const string UnauthorizedMarker = "Unauthorized";

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// POSTs <paramref name="query"/> and returns its <c>data</c> object. Throws:
    /// <list type="bullet">
    /// <item>an <see cref="HttpRequestException"/> carrying the status for a non-2xx answer, and with 401 for a GraphQL "Unauthorized";</item>
    /// <item><see cref="RateLimitException"/> when the extension reports the site's own 429, and a 404 <see cref="HttpRequestException"/> for its 404;</item>
    /// <item><see cref="SuwayomiGraphQlException"/> for any other GraphQL error, partial data included,
    /// since a source that failed halfway must not read as an empty chapter list;</item>
    /// <item><see cref="InvalidOperationException"/> when the answer has no <c>data</c>.</item>
    /// </list>
    /// </summary>
    public static async Task<JsonElement> PostAsync(
        HttpClient client,
        Uri endpoint,
        string operationName,
        string query,
        object? variables,
        CancellationToken ct,
        bool skipAuth = false,
        bool skipLimit = false)
    {
        var body = new JsonObject
        {
            ["operationName"] = operationName,
            ["query"] = query,
            ["variables"] = variables is null ? new JsonObject() : JsonSerializer.SerializeToNode(variables)
        };

        // StringContent is re-readable, which is what lets the auth handler send it a second time.
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        if (skipAuth)
        {
            request.Options.Set(SkipAuth, true);
        }

        if (skipLimit)
        {
            request.Options.Set(RateLimitingHandler.Skip, true);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await BufferBodyAsync(response.Content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Suwayomi answered {(int)response.StatusCode} ({response.StatusCode}) to {operationName}",
                null, response.StatusCode);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Suwayomi answered {operationName} with something other than JSON", ex);
        }

        var errors = ErrorMessages(root);
        if (errors.Count > 0)
        {
            if (errors.Any(IsUnauthorized))
            {
                throw new HttpRequestException(
                    $"Suwayomi refused the credentials for {operationName}", null, HttpStatusCode.Unauthorized);
            }

            // Mihon's OkHttp layer reports a site's own status as "HTTP error 429". A 404 is passed on as
            // one too, so a chapter the site took down moves to the next source at once, as a
            // built-in source's 404 does, instead of waiting for the third retry.
            if (errors.Any(m => m.Contains("HTTP error 429", StringComparison.OrdinalIgnoreCase)))
            {
                throw new RateLimitException($"Rate limited by the site behind Suwayomi ({operationName})");
            }

            if (errors.Any(m => m.Contains("HTTP error 404", StringComparison.OrdinalIgnoreCase)))
            {
                throw new HttpRequestException(
                    $"The site behind Suwayomi answered 404 to {operationName}", null, HttpStatusCode.NotFound);
            }

            throw new SuwayomiGraphQlException($"Suwayomi: {string.Join("; ", errors)}", errors);
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Suwayomi answered {operationName} without a data object");
        }

        return data;
    }

    /// <summary>
    /// Reads the body into memory within <see cref="BodyTimeout"/>. The clients have no overall
    /// timeout and the request timeout stops at the headers, so a server that stalls mid-body needs
    /// this bound; expiry reads like the client's own timeout.
    /// </summary>
    public static async Task BufferBodyAsync(HttpContent content, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? BodyTimeout;
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeout.CancelAfter(limit);
        try
        {
            await content.LoadIntoBufferAsync(bodyTimeout.Token);
        }
        catch (OperationCanceledException) when (bodyTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"Reading the response timed out after {limit.TotalSeconds:0.###} seconds.", new TimeoutException());
        }
    }

    /// <summary>True when a buffered GraphQL body is the 200-with-Unauthorized answer.</summary>
    public static bool IsUnauthorizedBody(string text)
    {
        if (!text.Contains(UnauthorizedMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return ErrorMessages(doc.RootElement).Any(IsUnauthorized);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Suwayomi's own rejection: the whole message, or the part after graphql-java's
    /// "Exception while fetching data (/field) : " prefix, is just "Unauthorized". Error text from a
    /// site behind an extension that merely mentions the word must not trigger a token renewal.
    /// </summary>
    internal static bool IsUnauthorized(string message)
    {
        const string fetchPrefix = "Exception while fetching data";
        var text = message.Trim();
        if (text.StartsWith(fetchPrefix, StringComparison.Ordinal) && text.LastIndexOf(" : ", StringComparison.Ordinal) is var at and >= 0)
        {
            text = text[(at + 3)..];
        }

        return text.Trim().TrimEnd('.').Equals(UnauthorizedMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ErrorMessages(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // A source that throws inside Suwayomi can surface as an error with a null message
        // (a Kotlin `!!` on a missing source), which still has to read as a failure.
        return [.. errors.EnumerateArray().Select(e =>
            e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty("message", out var m) &&
            m.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(m.GetString())
                ? m.GetString()!
                : "an error without a message")];
    }

    /// <summary>Suwayomi's <c>LongString</c> arrives as a string; a number is accepted too.</summary>
    public static string? LongString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetInt64().ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    public static long Long(JsonElement parent, string name) =>
        long.TryParse(LongString(parent, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    public static int Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    public static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
