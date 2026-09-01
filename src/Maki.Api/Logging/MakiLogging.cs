using Maki.Api.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Maki.Api.Logging;

/// <summary>
/// Owns the logging pipeline: levels, per-category noise floors, sinks and output shape.
/// <para>
/// Everything in Maki logs through <c>ILogger&lt;T&gt;</c>. This is the one place that decides what
/// happens to those events, so tuning what the log contains never means touching a call site.
/// </para>
/// </summary>
public static class MakiLogging
{
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Component}: {Message:lj}{NewLine}{Exception}";

    private const string ConsoleTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Component}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Brings the logger up on defaults, before <c>config.json</c> can be read.
    /// <para>
    /// A restore is applied ahead of anything reading config (see <see cref="RestoreBootstrap"/>),
    /// and it can replace config.json itself, so the configured level is not knowable yet. Rather
    /// than let that stretch of startup write to the console by hand and vanish from the file, it
    /// runs against this logger and <see cref="Configure"/> rebuilds the pipeline immediately after.
    /// </para>
    /// </summary>
    /// <summary>
    /// The logger <see cref="Bootstrap"/> created, held so <see cref="Configure"/> can dispose that
    /// one specifically rather than calling <c>Log.CloseAndFlush</c>. The static <c>Log.Logger</c>
    /// is process-wide, and the host is booted more than once per process under
    /// <c>WebApplicationFactory</c>; closing whatever happens to be installed would close another
    /// boot's logger and take its file handle with it.
    /// </summary>
    private static Logger? bootstrapLogger;

    public static void Bootstrap(AppPaths paths)
    {
        var logger = Build(paths, LoggingOptions.Defaults);
        bootstrapLogger = logger;
        Log.Logger = logger;
    }

    /// <summary>Rebuilds the pipeline against the configured options, releasing the bootstrap one.</summary>
    public static void Configure(AppPaths paths, LoggingOptions options)
    {
        // Install the replacement before releasing the old one so nothing logging concurrently
        // writes into a disposed sink.
        Log.Logger = Build(paths, options);

        var previous = Interlocked.Exchange(ref bootstrapLogger, null);
        previous?.Dispose();
    }

    /// <summary>
    /// An <c>ILogger</c> for code that runs before the host, and therefore before DI, exists.
    /// Startup work logs under a real category this way instead of through Serilog's static
    /// <c>Log</c>, which produces events with no source at all.
    /// </summary>
    public static Microsoft.Extensions.Logging.ILogger CreateLogger(string category) =>
        new SerilogLoggerFactory(Log.Logger).CreateLogger(category);

    private static Logger Build(AppPaths paths, LoggingOptions options)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Is(options.Level)

            // Framework categories carry their own idea of what is worth an Information line, and it
            // is not ours. Each of these floors exists because the category was measurably drowning
            // the log at Information:
            //
            //   Microsoft.AspNetCore    routing, model binding and authentication chatter per request
            //   Microsoft.EntityFrameworkCore  one line per command, plus the command text
            //   System.Net.Http.HttpClient     four lines per outbound call, and a full stack trace
            //                                  at Information when one fails. Replaced by
            //                                  OutboundHttpLoggingHandler, which logs one line.
            //   Quartz                  scheduler and thread pool boilerplate at every start. The
            //                           jobs themselves log under Maki.Api.Jobs.* and are unaffected.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .MinimumLevel.Override("Quartz", LogEventLevel.Warning)
            .Enrich.With<ComponentEnricher>()
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .WriteTo.File(
                Path.Combine(paths.LogDir, "maki-.log"),
                outputTemplate: FileTemplate,
                rollingInterval: RollingInterval.Day,

                // Rolling by day alone caps nothing. A source that starts failing, or Debug left on
                // overnight, writes until the disk is full; the size limit turns that into a rolled
                // segment that retention can then reclaim.
                fileSizeLimitBytes: options.FileSizeMb * 1024L * 1024L,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFiles)
            .CreateLogger();
    }
}

/// <summary>
/// Adds <c>Component</c>: the last segment of the logging category, or <c>Maki</c> for events with
/// no category at all.
/// <para>
/// The full <c>SourceContext</c> is a namespace-qualified type name, so a template that printed it
/// would spend forty columns on <c>Maki.Api.Services.</c> before every message and push the message
/// itself off the line. The short name is what identifies the component in practice, and the
/// namespaces here are shallow enough that the last segments do not collide.
/// </para>
/// </summary>
internal sealed class ComponentEnricher : ILogEventEnricher
{
    /// <summary>
    /// The one category whose short name reads worse than a label. Request logging is the highest
    /// volume thing in the file and naming it after the middleware class that happens to emit it
    /// tells the reader nothing.
    /// </summary>
    private const string RequestLoggingContext = "Serilog.AspNetCore.RequestLoggingMiddleware";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        var component = "Maki";

        if (logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue { Value: string context }
            && context.Length > 0)
        {
            if (context == RequestLoggingContext)
            {
                component = "Http";
            }
            else
            {
                var lastDot = context.LastIndexOf('.');
                component = lastDot >= 0 && lastDot < context.Length - 1
                    ? context[(lastDot + 1)..]
                    : context;
            }
        }

        logEvent.AddPropertyIfAbsent(factory.CreateProperty("Component", component));
    }
}
