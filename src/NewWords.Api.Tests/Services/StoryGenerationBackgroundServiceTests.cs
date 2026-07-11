using System;
using FluentAssertions;
using NewWords.Api.Services;
using Xunit;

namespace NewWords.Api.Tests.Services
{
    public class StoryGenerationBackgroundServiceTests
    {
        // The exact instant from issue #10: on a UTC-8 host the old code computed a -23.5h delay
        // (Task.Delay throws -> hourly error-retry loop). UTC-only math must yield a small positive delay.
        [Fact]
        public void ComputeDelayUntilNextRun_IssueReproInstant_IsNonNegativeAndNear2Am()
        {
            var now = new DateTime(2026, 7, 8, 1, 30, 0, DateTimeKind.Utc);

            var delay = StoryGenerationBackgroundService.ComputeDelayUntilNextRun(now);

            delay.Should().Be(TimeSpan.FromMinutes(30));
            (now + delay).Should().Be(new DateTime(2026, 7, 8, 2, 0, 0, DateTimeKind.Utc));
        }

        [Fact]
        public void ComputeDelayUntilNextRun_MidnightUtc_IsExactlyTwoHours()
        {
            var now = new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);

            var delay = StoryGenerationBackgroundService.ComputeDelayUntilNextRun(now);

            delay.Should().Be(TimeSpan.FromHours(2));
        }

        [Fact]
        public void ComputeDelayUntilNextRun_ExactlyAtTargetHour_SchedulesNextDay()
        {
            var now = new DateTime(2026, 7, 8, 2, 0, 0, DateTimeKind.Utc);

            var delay = StoryGenerationBackgroundService.ComputeDelayUntilNextRun(now);

            delay.Should().Be(TimeSpan.FromHours(24));
            (now + delay).Should().Be(new DateTime(2026, 7, 9, 2, 0, 0, DateTimeKind.Utc));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(6)]
        [InlineData(12)]
        [InlineData(18)]
        [InlineData(23)]
        public void ComputeDelayUntilNextRun_AcrossTheDay_DelayNonNegativeAndLandsAt2AmUtc(int hour)
        {
            var now = new DateTime(2026, 7, 8, hour, 37, 0, DateTimeKind.Utc);

            var delay = StoryGenerationBackgroundService.ComputeDelayUntilNextRun(now);

            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);

            var nextRun = now + delay;
            nextRun.Hour.Should().Be(2);
            nextRun.Minute.Should().Be(0);
            nextRun.Second.Should().Be(0);
            // Next run is always within (0, 24h] of now and strictly in the future for any minute offset.
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(24));
        }

        [Fact]
        public void ComputeDelayUntilNextRun_RespectsCustomTargetHour()
        {
            var now = new DateTime(2026, 7, 8, 5, 0, 0, DateTimeKind.Utc);

            var delay = StoryGenerationBackgroundService.ComputeDelayUntilNextRun(now, targetHourUtc: 4);

            // 04:00 already passed today -> next 04:00 is tomorrow (23h away).
            delay.Should().Be(TimeSpan.FromHours(23));
        }
    }
}
