// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Limits concurrent module launch operations shared by the shell and the interop HTTP API.
    /// </summary>
    public sealed class ModuleStartThrottle
    {
        /// <summary>
        /// Default maximum concurrent module starts (matches historical shell startup throttling).
        /// </summary>
        public const int DefaultMaxConcurrentStarts = 2;

        private readonly SemaphoreSlim _semaphore;


        // Initialization

        /// <summary>
        /// Creates the shared throttle.
        /// </summary>
        public ModuleStartThrottle()
        {
            _semaphore = new SemaphoreSlim(DefaultMaxConcurrentStarts, DefaultMaxConcurrentStarts);
        }


        // Launch slots

        /// <summary>
        /// Waits until a launch slot is available.
        /// </summary>
        public Task WaitAsync(CancellationToken cancellationToken = default) =>
            _semaphore.WaitAsync(cancellationToken);

        /// <summary>
        /// Releases one launch slot.
        /// </summary>
        public void Release() => _semaphore.Release();
    }
}
