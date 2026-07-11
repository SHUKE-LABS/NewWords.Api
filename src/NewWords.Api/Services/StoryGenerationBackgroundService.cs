using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services
{
    public class StoryGenerationBackgroundService : BackgroundService
    {
        private readonly ILogger<StoryGenerationBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public StoryGenerationBackgroundService(
            ILogger<StoryGenerationBackgroundService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Story Generation Background Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var delay = ComputeDelayUntilNextRun(now);
                    var nextRun = now + delay;
                    _logger.LogInformation($"Next story generation scheduled for: {nextRun:yyyy-MM-dd HH:mm:ss} UTC (in {delay.TotalHours:F1} hours)");

                    await Task.Delay(delay, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await GenerateStoriesAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Story generation background service cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in story generation background service");
                    // Wait 1 hour before retrying on error
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                }
            }
        }

        /// <summary>
        /// Computes the delay until the next scheduled daily run. All arithmetic is done in UTC
        /// so the result is independent of the host's local timezone: <paramref name="nowUtc"/> is
        /// expected to be a UTC instant, and the target-hour anchor is built from <c>nowUtc.Date</c>
        /// (a UTC calendar day). Mixing <c>DateTime.UtcNow</c> with the local-time <c>DateTime.Today</c>
        /// previously produced a wrong or negative delay on non-UTC hosts (issue #10).
        /// </summary>
        internal static TimeSpan ComputeDelayUntilNextRun(DateTime nowUtc, int targetHourUtc = 2)
        {
            var today = nowUtc.Date; // UTC calendar-day anchor (Kind matches nowUtc)
            var nextRun = nowUtc.Hour >= targetHourUtc
                ? today.AddDays(1).AddHours(targetHourUtc) // already past target hour today -> tomorrow
                : today.AddHours(targetHourUtc);           // before target hour today -> today

            var delay = nextRun - nowUtc;
            // Defensive clamp: keep Task.Delay from throwing if a future change reintroduces a negative span.
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        private async Task GenerateStoriesAsync()
        {
            try
            {
                _logger.LogInformation("Starting daily story generation");

                using var scope = _serviceProvider.CreateScope();
                var storyService = scope.ServiceProvider.GetRequiredService<IStoryService>();

                await storyService.GenerateStoriesForEligibleUsersAsync();

                _logger.LogInformation("Daily story generation completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during daily story generation");
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Story Generation Background Service is stopping");
            await base.StopAsync(stoppingToken);
        }
    }
}