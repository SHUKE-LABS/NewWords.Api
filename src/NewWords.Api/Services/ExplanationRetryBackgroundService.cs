using NewWords.Api.Services.interfaces;

namespace NewWords.Api.Services
{
    /// <summary>
    /// Periodically fills pending word explanations (placeholders left when every LLM agent failed at
    /// add-word time). Mirrors the scheduling pattern of <see cref="StoryGenerationBackgroundService"/>;
    /// the actual fill logic lives in <see cref="IVocabularyService.RetryPendingExplanationsAsync"/>.
    /// </summary>
    public class ExplanationRetryBackgroundService : BackgroundService
    {
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
        private const int BatchSize = 20;
        private const int MaxRetryCount = 20;

        private readonly ILogger<ExplanationRetryBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public ExplanationRetryBackgroundService(
            ILogger<ExplanationRetryBackgroundService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Explanation Retry Background Service started (every {Minutes} min, batch {Batch}, cap {Cap})",
                RetryInterval.TotalMinutes, BatchSize, MaxRetryCount);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RetryInterval, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await RetryPendingExplanationsAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Explanation retry background service cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in explanation retry background service");
                    // Wait before retrying on unexpected error to avoid a tight failure loop.
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
        }

        private async Task RetryPendingExplanationsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var vocabularyService = scope.ServiceProvider.GetRequiredService<IVocabularyService>();

                var filled = await vocabularyService.RetryPendingExplanationsAsync(BatchSize, MaxRetryCount);

                if (filled > 0)
                {
                    _logger.LogInformation("Explanation retry cycle filled {Filled} pending explanation(s)", filled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during explanation retry cycle");
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Explanation Retry Background Service is stopping");
            await base.StopAsync(stoppingToken);
        }
    }
}
