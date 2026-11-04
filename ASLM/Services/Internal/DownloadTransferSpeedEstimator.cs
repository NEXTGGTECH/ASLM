// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Tracks per-operation byte deltas to produce a smoothed transfer speed for download notifications.
    /// </summary>
    internal sealed class DownloadTransferSpeedEstimator
    {
        // Fields and constants

        /// <summary>
        /// Weight of the newest instantaneous speed in the exponential moving average; higher reacts faster.
        /// </summary>
        private const double SmoothingAlpha = 0.38;

        /// <summary>
        /// Guards <see cref="_states"/> against concurrent progress reports.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Last sample and smoothed speed of every operation, by operation key.
        /// </summary>
        private readonly Dictionary<string, SampleState> _states = new(StringComparer.OrdinalIgnoreCase);


        // Sampling

        /// <summary>
        /// Clears all samples for one operation so the next report starts a fresh speed window.
        /// </summary>
        public void Reset(string operationKey)
        {
            lock (_gate)
            {
                _states.Remove(operationKey);
            }
        }

        /// <summary>
        /// Records one progress sample and returns a formatted speed label when enough data exists.
        /// </summary>
        /// <param name="operationKey">Key of the download operation the sample belongs to.</param>
        /// <param name="downloadedBytes">Total bytes transferred so far by that operation.</param>
        /// <returns>A formatted speed (empty while no speed is known), or <c>null</c> for the first sample of an operation.</returns>
        public string? Sample(string operationKey, long downloadedBytes)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;

                // The first sample only establishes the baseline; no speed can be derived from it.
                if (!_states.TryGetValue(operationKey, out var state))
                {
                    _states[operationKey] = new SampleState(now, downloadedBytes, 0);
                    return null;
                }

                var deltaBytes = downloadedBytes - state.LastBytes;
                var deltaSeconds = (now - state.LastAt).TotalSeconds;

                // A shrinking counter means the transfer restarted: begin a new window with no speed.
                if (deltaBytes < 0)
                {
                    state.LastAt = now;
                    state.LastBytes = downloadedBytes;
                    state.SmoothedBytesPerSecond = 0;
                    return FormatSpeed(state.SmoothedBytesPerSecond);
                }

                // Reports arriving faster than 40 ms give a noisy delta, so keep the previous estimate.
                if (deltaSeconds < 0.04)
                {
                    return FormatSpeed(state.SmoothedBytesPerSecond);
                }

                // No progress since the last sample: move the time baseline but keep the estimate.
                if (deltaBytes == 0)
                {
                    state.LastAt = now;
                    return FormatSpeed(state.SmoothedBytesPerSecond);
                }

                // Blend the instantaneous speed into the moving average; the first measurement seeds it.
                var instant = deltaBytes / deltaSeconds;
                state.SmoothedBytesPerSecond = state.SmoothedBytesPerSecond <= 0
                    ? instant
                    : (state.SmoothedBytesPerSecond * (1.0 - SmoothingAlpha)) + (instant * SmoothingAlpha);

                state.LastAt = now;
                state.LastBytes = downloadedBytes;

                return FormatSpeed(state.SmoothedBytesPerSecond);
            }
        }


        // Speed formatting

        /// <summary>
        /// Formats one smoothed bytes-per-second estimate for notification detail text.
        /// </summary>
        private static string FormatSpeed(double bytesPerSecond)
        {
            // An unknown or non-positive speed is shown as nothing instead of "0 B/s".
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            {
                return string.Empty;
            }

            // Pick the largest binary unit that keeps the value at or above 1.
            if (bytesPerSecond >= 1_073_741_824)
            {
                return $"{bytesPerSecond / 1_073_741_824.0:F1} GB/s";
            }

            if (bytesPerSecond >= 1_048_576)
            {
                return $"{bytesPerSecond / 1_048_576.0:F1} MB/s";
            }

            if (bytesPerSecond >= 1024)
            {
                return $"{bytesPerSecond / 1024.0:F1} KB/s";
            }

            return $"{bytesPerSecond:F0} B/s";
        }


        // Nested types

        /// <summary>
        /// Stores the last sample used to smooth transfer speed for one download operation.
        /// </summary>
        private sealed class SampleState
        {
            /// <summary>
            /// Creates a sample state from the baseline of a new measurement window.
            /// </summary>
            public SampleState(DateTimeOffset lastAt, long lastBytes, double smoothedBytesPerSecond)
            {
                LastAt = lastAt;
                LastBytes = lastBytes;
                SmoothedBytesPerSecond = smoothedBytesPerSecond;
            }

            /// <summary>
            /// UTC time of the sample that serves as the start of the next measurement interval.
            /// </summary>
            public DateTimeOffset LastAt { get; set; }

            /// <summary>
            /// Total transferred bytes at <see cref="LastAt"/>.
            /// </summary>
            public long LastBytes { get; set; }

            /// <summary>
            /// Exponentially smoothed transfer speed in bytes per second; zero until one interval was measured.
            /// </summary>
            public double SmoothedBytesPerSecond { get; set; }
        }
    }
}
