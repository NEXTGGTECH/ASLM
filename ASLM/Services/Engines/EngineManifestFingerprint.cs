// Copyright NEXTGGTECH. Apache License 2.0.

using System.Security.Cryptography;
using System.Text.Json;
using ASLM.Models;

namespace ASLM.Services.Engines
{
    /// <summary>
    /// Computes a stable semantic hash for declarative engine metadata.
    /// Mutable installation status and runtime-only model properties are excluded.
    /// </summary>
    public static class EngineManifestFingerprint
    {
        /// <summary>
        /// Computes a stable hash of install-relevant engine metadata.
        /// </summary>
        public static string Compute(EngineConfig config)
        {
            config.Normalize();
            var json = JsonSerializer.Serialize(config);
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(document.RootElement, writer, isRoot: true);
            }

            return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        }

        /// <summary>
        /// Compares engine definitions while ignoring mutable installation status.
        /// </summary>
        public static bool AreEquivalent(EngineConfig left, EngineConfig right) =>
            string.Equals(Compute(left), Compute(right), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Writes deterministic JSON so property order cannot change the fingerprint.
        /// </summary>
        private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer, bool isRoot = false)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    // Status is mutable local state and must not trigger engine reinstallation.
                    foreach (var property in element.EnumerateObject()
                                 .Where(property => !(isRoot &&
                                     string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase)))
                                 .OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteCanonical(item, writer);
                    }
                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
