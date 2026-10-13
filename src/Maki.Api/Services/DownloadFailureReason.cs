using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Maki.Core.Http;
using Maki.Core.Sources;

namespace Maki.Api.Services;

/// <summary>
/// Turns an exception out of a download into the catalogue key the queue row is worded from, plus
/// the detail shown after it. Everything used to land on <c>error.download.unexpected</c> with no
/// detail, so a source answering "Invalid user" read as "Something went wrong" in Activity.
/// </summary>
public static class DownloadFailureReason
{
    public const string SourceRejected = "error.download.sourceRejected";
    public const string SourceError = "error.download.sourceError";
    public const string Network = "error.download.network";
    public const string Disk = "error.download.disk";
    public const string Unexpected = "error.download.unexpected";
    public const string ChallengeNotSolved = "error.download.challengeNotSolved";

    private const int MaxDetailLength = 300;

    public static (string Key, string? Detail) Classify(Exception ex)
    {
        switch (ex)
        {
            case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } http:
                return (SourceRejected, $"HTTP {(int)http.StatusCode!}");
            case HttpRequestException { StatusCode: { } status }:
                return (SourceError, $"HTTP {(int)status}");
            case ChallengeNotSolvedException challenge:
                return (ChallengeNotSolved, challenge.Host);
            case SourceErrorException:
                return (SourceError, Trim(ex.Message));
            case ChapterUnavailableException unavailable:
                return (unavailable.Key, unavailable.Key == ChapterUnavailableException.NotListed ? Trim(ex.Message) : null);
            case HttpRequestException or HttpIOException or SocketException or AuthenticationException:
                return (Network, Trim(Innermost(ex).Message));
            case TimeoutException or TaskCanceledException:
                return (Network, Trim(ex.Message));
            case IOException or UnauthorizedAccessException:
                return (Disk, Trim(ex.Message));
            default:
                return (Unexpected, Trim(ex.Message));
        }
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner)
        {
            ex = inner;
        }

        return ex;
    }

    private static string? Trim(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        message = message.Trim();
        return message.Length <= MaxDetailLength ? message : message[..MaxDetailLength] + "...";
    }
}
