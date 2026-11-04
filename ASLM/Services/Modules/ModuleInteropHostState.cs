// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Thread-safe snapshot of the module interop listener base URL for child process environment injection.
    /// </summary>
    public sealed class ModuleInteropHostState
    {
        // Fields

        /// <summary>
        /// Guards the endpoint fields so a reader never sees a URL from one listener and a port from another.
        /// </summary>
        private readonly object _lock = new();

        /// <summary>
        /// Base URL of the interop listener, or null while it is not listening.
        /// </summary>
        private string? _baseUrl;

        /// <summary>
        /// TCP port of the interop listener, or null while it is not listening.
        /// </summary>
        private int? _port;


        // Listener state

        /// <summary>
        /// Records the listening interop server endpoint while the listener is active.
        /// </summary>
        public void SetListening(string baseUrl, int port)
        {
            lock (_lock)
            {
                _baseUrl = baseUrl;
                _port = port;
            }
        }

        /// <summary>
        /// Clears the listener endpoint after shutdown.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _baseUrl = null;
                _port = null;
            }
        }

        /// <summary>
        /// Returns the active interop base URL and port when the host server is listening.
        /// Returns false with an empty URL and port 0 when it is not listening or the stored endpoint is invalid.
        /// </summary>
        public bool TryGetListening(out string baseUrl, out int port)
        {
            lock (_lock)
            {
                // A usable endpoint needs a non-blank URL and a valid TCP port number.
                if (!string.IsNullOrWhiteSpace(_baseUrl) && _port is >= 1 and <= 65535)
                {
                    baseUrl = _baseUrl;
                    port = _port.Value;
                    return true;
                }
            }

            baseUrl = string.Empty;
            port = 0;
            return false;
        }
    }
}
