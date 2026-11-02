using Maki.Api.Configuration;

namespace Maki.Api.Services;

/// <summary>
/// Stops Maki when Maki.Desktop closes its end of standard input. That is how the tray asks for a
/// clean shutdown, since a process with no console window cannot be sent Ctrl+C. It also means Maki
/// never outlives a tray app that crashed or was killed, which would leave the port taken and nothing
/// in the notification area to stop it with.
/// </summary>
public sealed class DesktopParentWatcher(IHostApplicationLifetime lifetime, ILogger<DesktopParentWatcher> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        using var input = Console.OpenStandardInput();
        var buffer = new byte[64];
        try
        {
            while (await input.ReadAsync(buffer, stoppingToken) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException)
        {
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("The desktop app closed, stopping Maki");
            lifetime.StopApplication();
        }
    }, stoppingToken);

    public static bool Applies => HostRuntime.Kind == HostRuntimeKind.Desktop
        && Environment.GetEnvironmentVariable("MAKI_DESKTOP_PARENT") == "1";
}
