namespace Maki.Core.Notifications;

/// <summary>
/// A provider's endpoint answered with a failure. <paramref name="detail"/> is the service's own
/// error text when it gave one. The message never carries the request URL, because several
/// providers keep their token in it.
/// </summary>
public sealed class NotificationDeliveryException(string provider, int? statusCode, string? detail)
    : Exception(Format(provider, statusCode, detail))
{
    public string Provider { get; } = provider;
    public int? StatusCode { get; } = statusCode;
    public string? Detail { get; } = detail;

    public static void ThrowIfFailed(string provider, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationDeliveryException(provider, (int)response.StatusCode, null);
        }
    }

    private static string Format(string provider, int? statusCode, string? detail)
    {
        var head = statusCode is { } status ? $"{provider} returned {status}" : $"{provider} failed";
        return string.IsNullOrWhiteSpace(detail) ? head : $"{head}: {detail}";
    }
}
