// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Polling helpers for asynchronous conditions that have no completion task to await.
    /// </summary>
    public static class TestWait
    {
        // Condition polling

        /// <summary>
        /// Polls <paramref name="predicate"/> until it returns true or the timeout expires.
        /// The timeout defaults to ten seconds and a <see cref="TimeoutException"/> carries the optional
        /// <paramref name="because"/> text. The predicate is checked once before the first delay.
        /// </summary>
        public static async Task UntilAsync(Func<bool> predicate, TimeSpan? timeout = null,
            string? because = null, int pollMilliseconds = 20)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (!predicate())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"Condition was not met within {timeout ?? TimeSpan.FromSeconds(10)}.{(because == null ? null : " " + because)}");
                }

                await Task.Delay(pollMilliseconds);
            }
        }

        /// <summary>
        /// Polls <paramref name="producer"/> until it returns a non-null value and returns that value.
        /// Timeout and failure behavior are those of the predicate overload.
        /// </summary>
        public static async Task<T> UntilAsync<T>(Func<T?> producer, TimeSpan? timeout = null,
            string? because = null, int pollMilliseconds = 20) where T : class
        {
            T? value = null;
            await UntilAsync(() => (value = producer()) != null, timeout, because, pollMilliseconds);
            return value!;
        }


        // Cleanup

        /// <summary>
        /// Deletes a directory tree, briefly retrying while another process still holds it open.
        /// Only <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/> are retried (every 50 ms,
        /// ten seconds by default); once the deadline passes, the last error is thrown.
        /// </summary>
        /// <remarks>
        /// Killed bridge or stub processes (and their transient console hosts) can keep their
        /// working directory locked for a short moment after termination on Windows.
        /// </remarks>
        public static async Task DeleteDirectoryAsync(string directory, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           && DateTime.UtcNow < deadline)
                {
                    // Still locked by a dying process: wait briefly and try again until the deadline.
                    await Task.Delay(50);
                }
            }
        }
    }
}
