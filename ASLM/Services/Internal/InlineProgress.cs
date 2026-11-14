// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Reports progress synchronously on the calling thread, without marshaling or queuing.
    /// <see cref="Progress{T}"/> posts every value to the context captured at construction, or to the thread pool
    /// when there is none, where values can be handled out of order and after the reporting operation finished.
    /// This reporter hands every value to the handler before <see cref="Report"/> returns, so the handler sees
    /// them in order; the handler must therefore be thread-safe and must not block.
    /// </summary>
    /// <typeparam name="T">Type of the reported progress values.</typeparam>
    /// <param name="handler">Called synchronously with every reported value.</param>
    internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        /// <summary>
        /// Passes the reported value to the handler on the calling thread.
        /// </summary>
        public void Report(T value)
        {
            handler(value);
        }
    }
}
