using System.Runtime.CompilerServices;

namespace Maki.Api.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Every WebApplicationFactory host here boots on an empty config dir, which would otherwise
    /// start the MangaBaka dump download five seconds in.
    /// </summary>
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("MAKI_DUMP_BOOT_DOWNLOAD", "0");
}
