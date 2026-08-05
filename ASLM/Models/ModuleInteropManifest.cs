// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Module interop manifest

    /// <summary>
    /// Declares how a module participates in the host <c>moduleInterop</c> HTTP API and environment injection.
    /// </summary>
    public sealed class ModuleInteropManifest
    {
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        [JsonPropertyName("client")]
        public ModuleInteropClientConfig Client { get; set; } = new();

        /// <summary>
        /// Normalizes optional fields after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Client ??= new();
            Client.Normalize();
        }

        /// <summary>
        /// Returns whether the manifest opts in to host interop environment injection.
        /// </summary>
        public bool IsClientEnabled =>
            ProtocolVersion >= 1 && Client.Enabled;
    }

    /// <summary>
    /// Describes the module-side consumer of the host interop API.
    /// </summary>
    public sealed class ModuleInteropClientConfig
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        public void Normalize()
        {
            // Booleans deserialize with CLR defaults.
        }
    }
}
