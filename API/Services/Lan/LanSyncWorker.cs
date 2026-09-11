namespace API.Services.Lan;

/// <summary>
/// Polls the LAN bot on the configured interval.
///
/// THE FIRST HOSTED SERVICE IN THIS CODEBASE, so the conventions it sets are worth stating. The
/// abandoned-upload sweep in Program.cs looks similar and is not the same thing: that runs once,
/// before the first request, precisely so it can assume nothing else is in flight. This runs forever,
/// alongside live traffic, which brings four obligations:
///
///  * <b>A scope per tick.</b> The services it needs are scoped and a singleton cannot hold them —
///    capturing a DbContext in a worker that lives for the process is the classic version of this bug.
///  * <b>The interval is re-read every loop.</b> Settings are read per call everywhere in this
///    application (see SettingsService) and a timer constructed once at startup would quietly opt out
///    of that: the admin changes the interval, the page says saved, and nothing happens until a restart.
///  * <b>Nothing escapes.</b> An unhandled exception out of ExecuteAsync takes the host down by
///    default. A LAN bot that is switched off must not be able to stop the library from serving.
///  * <b>It waits before its first run.</b> Starting with a sync would put an outbound call in the
///    startup path of every install, configured or not — and would race the test host, which boots
///    and asserts inside a second.
/// </summary>
public class LanSyncWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<LanSyncWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait before looking again when polling is switched off or unconfigured.
    ///
    /// This is a poll of the SETTINGS, not of the bot — it is what lets an admin configure the
    /// integration and have it start working without restarting the service. A minute is short enough
    /// that "I saved it and nothing happened" never becomes a support question, and costs one
    /// primary-key read against a local file.
    /// </summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await NextDelayAsync(stoppingToken);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown. The only expected way out of this loop.
                return;
            }

            if (stoppingToken.IsCancellationRequested) return;

            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// The configured interval, or <see cref="IdleInterval"/> when there is nothing to do.
    ///
    /// Both "no credentials" and "interval 0" land here, and they are different states worth keeping
    /// distinct elsewhere: zero means an operator deliberately wants manual syncs only, and the
    /// settings screen says so rather than calling it unconfigured.
    /// </summary>
    private async Task<TimeSpan> NextDelayAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();

            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var bot = scope.ServiceProvider.GetRequiredService<LanBotClient>();

            if (!await bot.IsConfiguredAsync()) return IdleInterval;

            var minutes = await settings.GetLanBotSyncIntervalMinutesAsync();
            return minutes <= 0 ? IdleInterval : TimeSpan.FromMinutes(minutes);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Reading a setting failed — a locked database during a migration, most likely. Back off
            // and try again rather than spinning; there is nothing here worth taking the host down for.
            logger.LogWarning("Could not read the LAN bot sync interval ({Type}); backing off.",
                exception.GetType().Name);

            return IdleInterval;
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var suggestions = scope.ServiceProvider.GetRequiredService<LanSuggestionService>();

            var reason = await suggestions.SyncAsync(stoppingToken);

            if (reason != "ok")
                logger.LogInformation("The LAN bot sync finished with reason {Reason}.", reason);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown mid-sync. Nothing is half-written: every phase saves its own transaction.
        }
        catch (Exception exception)
        {
            // SyncAsync already catches its own failures, so reaching here means something outside it
            // broke — resolving a service, or the scope itself. Still not a reason to stop serving the
            // library, so it is logged and the loop continues.
            logger.LogError(exception, "The LAN bot sync could not be started.");
        }
    }
}
