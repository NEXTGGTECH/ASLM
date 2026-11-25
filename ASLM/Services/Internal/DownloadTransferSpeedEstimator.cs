// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Measures the recent average transfer speed of one file or response.
    /// </summary>
    internal sealed class DownloadTransferSpeedEstimator(TimeProvider? timeProvider = null)
    {
        // Sampling state

        /// <summary>
        /// Shortest interval between speed measurements, independent of the progress report rate.
        /// </summary>
        private static readonly TimeSpan MeasurementInterval = TimeSpan.FromMilliseconds(350);

        /// <summary>
        /// Age at which a measurement becomes eligible as the start of the averaging window.
        /// </summary>
        private static readonly TimeSpan AveragingWindow = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Clock used to measure elapsed time without depending on the wall clock.
        /// </summary>
        private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

        /// <summary>
        /// Recent measured byte counts newer than the start of the averaging window.
        /// </summary>
        private readonly Queue<(long Timestamp, long Bytes)> _measurements = new();

        /// <summary>
        /// Oldest retained measurement, or null before the first progress report.
        /// </summary>
        private (long Timestamp, long Bytes)? _windowStart;

        /// <summary>
        /// Byte counter from the latest report, including those between measurements.
        /// </summary>
        private long _lastBytes;

        /// <summary>
        /// Timestamp of the last computed speed or the initial byte count.
        /// </summary>
        private long _lastMeasurementAt;

        /// <summary>
        /// Most recent rate, retained between measurements.
        /// </summary>
        private double? _bytesPerSecond;


        // Sampling

        /// <summary>
        /// Clears the previous transfer's measurements before starting another file or attempt.
        /// </summary>
        public void Reset()
        {
            _windowStart = null;
            _measurements.Clear();
            _bytesPerSecond = null;
        }

        /// <summary>
        /// Returns the recent average in bytes per second after enough time has elapsed to measure it.
        /// </summary>
        public double? Sample(long downloadedBytes)
        {
            var now = _clock.GetTimestamp();

            // Start from the current byte count, including any bytes already present before a resumption.
            if (_windowStart == null || downloadedBytes < _lastBytes)
            {
                Reset();
                _windowStart = (now, downloadedBytes);
                _lastMeasurementAt = now;
                _lastBytes = downloadedBytes;
                return null;
            }

            _lastBytes = downloadedBytes;
            if (_clock.GetElapsedTime(_lastMeasurementAt, now) < MeasurementInterval)
            {
                return _bytesPerSecond;
            }

            // Keep the sample nearest to the start of the window without dropping a whole measurement interval.
            while (_measurements.TryPeek(out var next) && _clock.GetElapsedTime(next.Timestamp, now) >= AveragingWindow)
            {
                _windowStart = _measurements.Dequeue();
            }

            var (timestamp, bytes) = _windowStart.Value;
            _bytesPerSecond = (downloadedBytes - bytes) / _clock.GetElapsedTime(timestamp, now).TotalSeconds;
            _measurements.Enqueue((now, downloadedBytes));
            _lastMeasurementAt = now;
            return _bytesPerSecond;
        }


        // Display formatting

        /// <summary>
        /// Formats a known rate with a shared unit and precision for all download surfaces.
        /// </summary>
        internal static string FormatSpeed(double? bytesPerSecond) => bytesPerSecond switch
        {
            >= 1_073_741_824 => $"{bytesPerSecond / 1_073_741_824.0:F1} GB/s",
            >= 1_048_576 => $"{bytesPerSecond / 1_048_576.0:F1} MB/s",
            >= 1024 => $"{bytesPerSecond / 1024.0:F1} KB/s",
            >= 0 => $"{bytesPerSecond:F0} B/s",
            _ => string.Empty
        };

        /// <summary>
        /// Adds a known rate to an existing progress line, preserving the line before a measurement is available.
        /// </summary>
        internal static string AppendSpeed(string detail, double? bytesPerSecond)
        {
            var speed = FormatSpeed(bytesPerSecond);
            return speed.Length > 0 ? $"{detail} - {speed}" : detail;
        }
    }
}
