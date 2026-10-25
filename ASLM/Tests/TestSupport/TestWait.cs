// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.TestSupport;

/// <summary>
/// Polling helpers for asynchronous conditions that have no completion task to await.
/// </summary>
public static class TestWait
{
    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns true or the timeout expires.
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
    /// Polls <paramref name="producer"/> until it returns a non-null value.
    /// </summary>
    public static async Task<T> UntilAsync<T>(Func<T?> producer, TimeSpan? timeout = null,
        string? because = null, int pollMilliseconds = 20) where T : class
    {
        T? value = null;
        await UntilAsync(() => (value = producer()) != null, timeout, because, pollMilliseconds);
        return value!;
    }

    /// <summary>
    /// Deletes a directory tree, briefly retrying while another process still holds it open.
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
                await Task.Delay(50);
            }
        }
    }
}
