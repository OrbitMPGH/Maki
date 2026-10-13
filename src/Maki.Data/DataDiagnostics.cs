using Microsoft.Extensions.Logging;

namespace Maki.Data;

/// <summary>
/// Where code that runs inside EF value converters reports problems, since a converter has no
/// logger to inject. Set once at startup; unset in tools and tests that do not care.
/// </summary>
public static class DataDiagnostics
{
    public static ILogger? Logger { get; set; }
}
