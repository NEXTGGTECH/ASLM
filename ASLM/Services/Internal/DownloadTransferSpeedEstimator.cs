// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Tracks per-operation byte deltas to produce a smoothed transfer speed for download notifications.
    /// </summary>
    internal sealed class DownloadTransferSpeedEstimator
    {
        private const double SmoothingAlpha = 0.38;

        private readonly object _gate = new();
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
        /// <returns>A non-empty speed string, or <c>null</c> until the second sample arrives.</returns>
        public string? Sample(string operationKey, long downloadedBytes)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;

                if (!_states.TryGetValue(operationKey, out var state))
                {
                    _states[operationKey] = new SampleState(now, downloadedBytes, 0);
                    return null;
                }

                var deltaBytes = downloadedBytes - state.LastBytes;
                var deltaSeconds = (now - state.LastAt).TotalSeconds;

                if (deltaBytes < 0)
                {
                    state.LastAt = now;
                    state.LastBytes = downloadedBytes;
                    state.SmoothedBytesPerSecond = 0;
                    return FormatSpeed(state.SmoothedBytesPerSecond);
                }

                if (deltaSeconds < 0.04)
                {
                    return FormatSpeed(state.SmoothedBytesPerSecond);
                }

                if (deltaBytes == 0)
                {
                    state.LastAt = now;
                    return FormatSpeed(state.SmoothedBytesPerSecond);
                }

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
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            {
                return string.Empty;
            }

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

        /// <summary>
        /// Stores the last sample used to smooth transfer speed for one download operation.
        /// </summary>
        private sealed class SampleState
        {
            public SampleState(DateTimeOffset lastAt, long lastBytes, double smoothedBytesPerSecond)
            {
                LastAt = lastAt;
                LastBytes = lastBytes;
                SmoothedBytesPerSecond = smoothedBytesPerSecond;
            }

            public DateTimeOffset LastAt { get; set; }
            public long LastBytes { get; set; }
            public double SmoothedBytesPerSecond { get; set; }
        }
    }
}
