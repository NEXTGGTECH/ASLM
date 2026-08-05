// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    // Ollama persistent settings

    /// <summary>
    /// Represents the current Ollama CLI account state visible to the settings UI.
    /// </summary>
    public sealed class OllamaPersistentSettings
    {
        // Indicates whether an Ollama CLI executable is currently available.
        public bool IsCliAvailable { get; set; }

        // Indicates whether ASLM has a verified signed-in Ollama account state.
        public bool IsSignedIn { get; set; }

        // Display name returned by Ollama for the authenticated account.
        public string UserName { get; set; } = string.Empty;
    }


    // Ollama account command result

    /// <summary>
    /// Captures the result of one Ollama account command such as <c>signin</c> or <c>signout</c>.
    /// </summary>
    public sealed class OllamaAccountActionResult
    {
        // Indicates whether the command completed successfully.
        public bool Success { get; init; }

        // Human-readable message returned to the settings UI.
        public string Message { get; init; } = string.Empty;

        // Indicates that the browser-based sign-in flow started and should be verified asynchronously.
        public bool IsPendingVerification { get; init; }
    }
}
