// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    // Ollama persistent settings

    /// <summary>
    /// Represents the current Ollama CLI account state visible to the settings UI.
    /// </summary>
    public sealed class OllamaPersistentSettings
    {
        /// <summary>
        /// Indicates whether an Ollama CLI executable is currently available.
        /// </summary>
        public bool IsCliAvailable { get; set; }

        /// <summary>
        /// Indicates whether ASLM has a verified signed-in Ollama account state.
        /// </summary>
        public bool IsSignedIn { get; set; }

        /// <summary>
        /// Display name returned by Ollama for the authenticated account.
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Stable account identifier returned by Ollama.
        /// </summary>
        public string UserId { get; set; } = string.Empty;

        /// <summary>
        /// Email address associated with the Ollama account.
        /// </summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Public biography associated with the Ollama account.
        /// </summary>
        public string Bio { get; set; } = string.Empty;

        /// <summary>
        /// Public avatar URL associated with the Ollama account.
        /// </summary>
        public string AvatarUrl { get; set; } = string.Empty;

        /// <summary>
        /// First name associated with the Ollama account.
        /// </summary>
        public string FirstName { get; set; } = string.Empty;

        /// <summary>
        /// Last name associated with the Ollama account.
        /// </summary>
        public string LastName { get; set; } = string.Empty;

        /// <summary>
        /// Subscription plan reported for the Ollama account.
        /// </summary>
        public string Plan { get; set; } = string.Empty;
    }


    // Ollama account command result

    /// <summary>
    /// Captures the result of one Ollama account command such as <c>signin</c> or <c>signout</c>.
    /// </summary>
    public sealed class OllamaAccountActionResult
    {
        /// <summary>
        /// Indicates whether the command completed successfully.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Human-readable message returned to the settings UI.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// Indicates that the browser-based sign-in flow started and should be verified asynchronously.
        /// </summary>
        public bool IsPendingVerification { get; init; }
    }
}
