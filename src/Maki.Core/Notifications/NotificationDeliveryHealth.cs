using System.Globalization;

namespace Maki.Core.Notifications;

public static class NotificationDeliveryHealth
{
    /// <summary>Consecutive failed sends after which a connection is reported as a health warning.</summary>
    public const int FailingThreshold = 5;

    public const string Timeout = "timeout";
    public const string Network = "network";
    public const string Other = "error";
    public const string StatusPrefix = "status:";

    /// <summary>
    /// Reduces a failure to a short code. Exception text is never stored: it can carry a response
    /// body or a request URL, and webhook URLs and bot tokens are secrets.
    /// </summary>
    public static string Classify(Exception ex) => ex switch
    {
        NotificationDeliveryException { StatusCode: { } status } =>
            StatusPrefix + status.ToString(CultureInfo.InvariantCulture),
        TaskCanceledException or TimeoutException => Timeout,
        HttpRequestException { StatusCode: { } status } =>
            StatusPrefix + ((int)status).ToString(CultureInfo.InvariantCulture),
        HttpRequestException => Network,
        _ => Other
    };
}
