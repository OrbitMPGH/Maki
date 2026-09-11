using Maki.Api.Configuration;
using Serilog.Events;

namespace Maki.Api.Logging;

/// <summary>
/// How much of the inbound request pipeline reaches the log.
/// </summary>
public enum HttpRequestLogMode
{
    /// <summary>
    /// Only requests that failed or ran slow. Successful ones are never written, including
    /// mutations, so the log stops being a record of who changed what.
    /// </summary>
    Off,

    /// <summary>
    /// The default. Mutations, client errors, server errors and anything slower than
    /// <see cref="LoggingOptions.SlowRequestMs"/> are written; successful reads drop to Debug and
    /// static assets, images and hub traffic drop below that again. See
    /// <see cref="HttpRequestLogPolicy"/> for the exact tiers.
    /// </summary>
    Minimal,

    /// <summary>One Information line per request, the way it behaved before this existed.</summary>
    Full
}

/// <summary>
/// The logging half of <c>config.json</c>, parsed once at startup.
/// <para>
/// These are startup-read like <c>auth.*</c>, so a change needs a restart. That is deliberate: the
/// sinks are built once, before the host exists, because the restore bootstrap and the migration
/// backup both need somewhere to log and both run before any of it.
/// </para>
/// </summary>
public sealed record LoggingOptions(
    LogEventLevel Level,
    HttpRequestLogMode HttpRequests,
    int SlowRequestMs,
    int FileSizeMb,
    int RetainedFiles)
{
    /// <summary>What the logger runs at before <c>config.json</c> has been read.</summary>
    public static LoggingOptions Defaults { get; } = new(
        LogEventLevel.Information,
        HttpRequestLogMode.Minimal,
        SlowRequestMs: 1000,
        FileSizeMb: 20,
        RetainedFiles: 7);

    public static LoggingOptions From(ConfigFile config)
    {
        var level = Enum.TryParse<LogEventLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : Defaults.Level;

        var mode = Enum.TryParse<HttpRequestLogMode>(config.HttpRequestLogging, ignoreCase: true, out var parsedMode)
            ? parsedMode
            : Defaults.HttpRequests;

        // A zero or negative threshold would mark every request slow and undo the whole point of
        // the tiering, so it reads as "leave it at the default" rather than as a value.
        var slow = config.SlowRequestMs > 0 ? config.SlowRequestMs : Defaults.SlowRequestMs;

        // Both file limits clamp rather than reject. The failure mode of a bad number here is a log
        // directory that eats the disk, and there is nowhere to report a validation error to yet.
        var sizeMb = Math.Clamp(config.LogFileSizeMb, 1, 1024);
        var retained = Math.Clamp(config.RetainedLogFiles, 1, 365);

        return new LoggingOptions(level, mode, slow, sizeMb, retained);
    }
}
