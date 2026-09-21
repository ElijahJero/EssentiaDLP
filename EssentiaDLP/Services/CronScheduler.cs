using Cronos;

namespace EssentiaDLP.Services;

public sealed class CronScheduler : BackgroundService
{
    private readonly SettingsStore _settings;
    private readonly AnalysisRunner _runner;
    private readonly ILogger<CronScheduler> _log;

    public CronScheduler(SettingsStore settings, AnalysisRunner runner, ILogger<CronScheduler> log)
    {
        _settings = settings;
        _runner = runner;
        _log = log;
    }

    public static string? Validate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;
        try
        {
            CronExpression.Parse(expression.Trim(), CronFormat.Standard);
            return null;
        }
        catch (CronFormatException ex)
        {
            return ex.Message;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? lastFired = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            var cronText = _settings.Get().Cron;
            if (string.IsNullOrWhiteSpace(cronText))
                continue;
            CronExpression cron;
            try
            {
                cron = CronExpression.Parse(cronText.Trim(), CronFormat.Standard);
            }
            catch (CronFormatException ex)
            {
                _log.LogWarning("Invalid cron '{Cron}': {Error}", cronText, ex.Message);
                continue;
            }
            var now = DateTime.UtcNow;
            var next = cron.GetNextOccurrence(now.AddSeconds(-20), TimeZoneInfo.Utc);
            if (next is null || next.Value > now || next == lastFired)
                continue;
            lastFired = next;
            if (!_runner.TryStartGalaxy())
                _log.LogInformation("Cron tick skipped; analysis already running.");
            else
                _log.LogInformation("Cron started a Galaxy analysis run.");
        }
    }
}
