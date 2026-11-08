// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Services.Internal;
using FluentAssertions;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies when <see cref="UpdateScheduler"/> considers the required hourly update check due and how long it
    /// waits for the next one.
    /// </summary>
    public sealed class UpdateSchedulerTests
    {
        /// <summary>
        /// Verifies that required ASLM and Ollama checks ignore the optional repository-check preference.
        /// </summary>
        [Fact]
        public void Required_check_is_due_when_optional_checks_are_disabled()
        {
            var settings = new AppUpdateSettings
            {
                CheckEnabled = false,
                LastAutoCheckUtc = DateTimeOffset.UtcNow.AddHours(-2).ToString("o")
            };

            UpdateScheduler.IsRequiredCheckDue(settings).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that reopening ASLM inside the hourly window (15 minutes after the last check) does not trigger
        /// another required check and that the remaining wait is longer than 44 minutes.
        /// </summary>
        [Fact]
        public void Required_check_is_not_due_inside_hourly_window()
        {
            var settings = new AppUpdateSettings
            {
                LastAutoCheckUtc = DateTimeOffset.UtcNow.AddMinutes(-15).ToString("o")
            };

            UpdateScheduler.IsRequiredCheckDue(settings).Should().BeFalse();
            UpdateScheduler.GetDelayUntilRequiredCheck(settings)
                .Should().BeGreaterThan(TimeSpan.FromMinutes(44));
        }

        /// <summary>
        /// Verifies that the same scheduler state becomes due once more than an hour has passed since the last check,
        /// as in a long-running application session, with no remaining wait.
        /// </summary>
        [Fact]
        public void Required_check_becomes_due_after_hourly_window()
        {
            var settings = new AppUpdateSettings
            {
                LastAutoCheckUtc = DateTimeOffset.UtcNow.AddMinutes(-61).ToString("o")
            };

            UpdateScheduler.IsRequiredCheckDue(settings).Should().BeTrue();
            UpdateScheduler.GetDelayUntilRequiredCheck(settings).Should().Be(TimeSpan.Zero);
        }
    }
}
