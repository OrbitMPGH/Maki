using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Maki.Core.Http;

namespace Maki.Sources.Suwayomi;

/// <summary>
/// Outermost handler on both Suwayomi clients. Those clients skip <see cref="PublicAddressGuard"/>
/// because Suwayomi lives on the LAN, so this is what keeps them from reaching anything else: every
/// request must target the configured origin, and a redirect is refused rather than followed
/// (the primary handler has <c>AllowAutoRedirect = false</c>), or this would only check the first hop.
/// </summary>
public sealed class SuwayomiOriginHandler(SuwayomiSettings settings) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var connection = await settings.GetAsync(ct)
            ?? throw new InvalidOperationException("Suwayomi is not configured");
        if (request.RequestUri is not { } target || !connection.IsSameOrigin(target))
        {
            throw new BlockedDestinationException(
                $"Refusing to fetch '{request.RequestUri}': not the configured Suwayomi server");
        }

        var response = await base.SendAsync(request, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location;
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(
                $"Suwayomi redirected to '{location}'; check that the configured URL includes any subpath",
                null, status);
        }

        return response;
    }
}

/// <summary>
/// Attaches Suwayomi credentials to every request, GraphQL and image alike, and on a rejection
/// renews them once and sends the request once more. Holds no state: handler chains are rebuilt
/// every couple of minutes, so the tokens live in <see cref="SuwayomiTokenStore"/>.
/// <para>
/// A GraphQL POST under a login mode is rejected with 200 and "Unauthorized" in its errors, so
/// that body is buffered and read here. Nothing else is: page and thumbnail GETs are sent
/// headers-read and judged on status alone, or reading them would defeat the downloader's stall
/// timeout.
/// </para>
/// </summary>
public sealed class SuwayomiAuthHandler(SuwayomiTokenStore tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Options.TryGetValue(SuwayomiGraphQl.SkipAuth, out var skip) && skip)
        {
            return await base.SendAsync(request, ct);
        }

        var sent = await tokens.CurrentAsync(ct);
        request.Headers.Authorization = sent;
        var response = await base.SendAsync(request, ct);
        if (!tokens.CanRenew(sent) || !await IsRejectedAsync(request, response, ct))
        {
            return response;
        }

        response.Dispose();
        request.Headers.Authorization = await tokens.RenewAsync(sent, ct);
        return await base.SendAsync(request, ct);
    }

    private static async Task<bool> IsRejectedAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return true;
        }

        if (request.Method != HttpMethod.Post || !response.IsSuccessStatusCode ||
            !request.RequestUri!.AbsolutePath.EndsWith("/api/graphql", StringComparison.Ordinal))
        {
            return false;
        }

        // Buffered, so the caller still reads the body after this.
        await SuwayomiGraphQl.BufferBodyAsync(response.Content, ct);
        return SuwayomiGraphQl.IsUnauthorizedBody(await response.Content.ReadAsStringAsync(ct));
    }
}

/// <summary>
/// The credential every Suwayomi request carries, shared by both named clients, the connection
/// test and the health probe. For <c>ui_login</c> it holds the access and refresh tokens, minted
/// for one <see cref="SuwayomiConnection"/>: a changed URL, user or password discards them on the
/// next request without anyone having to clear them.
/// <para>
/// Renewal is single-flight. Concurrent requests rejected with the same token renew once; one that
/// arrives after another caller already swapped in a newer token just uses it.
/// </para>
/// </summary>
public sealed class SuwayomiTokenStore(
    SuwayomiSettings settings, IHttpClientFactory httpClientFactory, TimeProvider? time = null)
{
    private sealed record Tokens(SuwayomiConnection For, string Access, string? Refresh);

    private sealed record Refusal(SuwayomiConnection For, DateTimeOffset Until);

    /// <summary>
    /// How long a refused login is answered from memory. Without it every request, each page of a
    /// download included, would sign in again with the same wrong password.
    /// </summary>
    private static readonly TimeSpan RefusalHold = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Written under _gate, read by every request without taking it.
    private volatile Tokens? _tokens;
    private volatile Refusal? _refused;

    private const string LoginQuery =
        "mutation Login($username: String!, $password: String!) { login(input: {username: $username, password: $password}) { accessToken refreshToken } }";

    private const string RefreshQuery =
        "mutation RefreshToken($refreshToken: String!) { refreshToken(input: {refreshToken: $refreshToken}) { accessToken } }";

    public async Task<AuthenticationHeaderValue?> CurrentAsync(CancellationToken ct = default)
    {
        var connection = await settings.GetAsync(ct)
            ?? throw new InvalidOperationException("Suwayomi is not configured");
        switch (connection.AuthMode)
        {
            case SuwayomiAuthMode.Basic:
                var pair = Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}");
                return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(pair));
            case SuwayomiAuthMode.UiLogin:
                var tokens = _tokens;
                return tokens is not null && tokens.For == connection
                    ? Bearer(tokens.Access)
                    : await RenewAsync(null, ct);
            default:
                return null;
        }
    }

    /// <summary>Only a bearer token can be renewed; a rejected basic credential is simply wrong.</summary>
    public bool CanRenew(AuthenticationHeaderValue? sent) =>
        sent?.Scheme == "Bearer";

    /// <param name="rejected">What the rejected request carried, null when nothing was minted yet.</param>
    public async Task<AuthenticationHeaderValue?> RenewAsync(AuthenticationHeaderValue? rejected, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connection = await settings.GetAsync(ct)
                ?? throw new InvalidOperationException("Suwayomi is not configured");
            if (connection.AuthMode != SuwayomiAuthMode.UiLogin)
            {
                return await CurrentAsync(ct);
            }

            var current = _tokens;
            if (current is not null && current.For == connection && current.Access != rejected?.Parameter)
            {
                return Bearer(current.Access);
            }

            // The refresh token is long-lived but not rotated; when it is refused too, sign in again.
            if (current is not null && current.For == connection && current.Refresh is { } refresh)
            {
                try
                {
                    var data = await PostAsync(connection, "RefreshToken", RefreshQuery, new { refreshToken = refresh }, ct);
                    if (SuwayomiGraphQl.String(data.GetProperty("refreshToken"), "accessToken") is { Length: > 0 } access)
                    {
                        _tokens = current with { Access = access };
                        return Bearer(access);
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
                {
                }
                catch (SuwayomiGraphQlException)
                {
                }
            }

            var minted = await LoginAsync(connection, ct);
            _tokens = minted;
            return Bearer(minted.Access);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Clear()
    {
        _tokens = null;
        _refused = null;
    }

    /// <summary>
    /// Suwayomi refuses a login by throwing ("Incorrect username or password."), which reaches the
    /// client as an ordinary GraphQL error, not as "Unauthorized". It is turned into a 401 here so it
    /// reads as refused credentials everywhere else, rather than as a server that did not answer.
    /// </summary>
    private async Task<Tokens> LoginAsync(SuwayomiConnection connection, CancellationToken ct)
    {
        if (_refused is { } refused && refused.For == connection && _time.GetUtcNow() < refused.Until)
        {
            throw Refused();
        }

        System.Text.Json.JsonElement data;
        try
        {
            data = await PostAsync(connection, "Login", LoginQuery,
                new { username = connection.Username ?? "", password = connection.Password ?? "" }, ct);
        }
        catch (SuwayomiGraphQlException ex) when (!ex.IsUndefinedField("login"))
        {
            throw Refuse(connection);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw Refuse(connection);
        }

        var login = data.TryGetProperty("login", out var l) ? l : default;
        var access = login.ValueKind == System.Text.Json.JsonValueKind.Object ? SuwayomiGraphQl.String(login, "accessToken") : null;
        if (string.IsNullOrEmpty(access))
        {
            throw Refuse(connection);
        }

        _refused = null;
        return new Tokens(connection, access, SuwayomiGraphQl.String(login, "refreshToken"));
    }

    private HttpRequestException Refuse(SuwayomiConnection connection)
    {
        _refused = new Refusal(connection, _time.GetUtcNow() + RefusalHold);
        return Refused();
    }

    private static HttpRequestException Refused() =>
        new("Suwayomi refused the username or password", null, HttpStatusCode.Unauthorized);

    private Task<System.Text.Json.JsonElement> PostAsync(
        SuwayomiConnection connection, string operation, string query, object variables, CancellationToken ct) =>
        SuwayomiGraphQl.PostAsync(
            httpClientFactory.CreateClient(SuwayomiClient.HttpClientName),
            connection.GraphQlUri, operation, query, variables, ct, skipAuth: true);

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);
}
