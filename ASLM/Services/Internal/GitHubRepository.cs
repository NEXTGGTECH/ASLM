// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Parses exact GitHub repository coordinates without accepting other repository pages or hosts.
    /// </summary>
    internal static class GitHubRepository
    {
        /// <summary>
        /// Accepts owner/name and HTTPS repository links, including clone suffixes and trailing slashes.
        /// </summary>
        internal static bool TryParse(string? input, out string repository)
        {
            repository = string.Empty;
            var value = input?.Trim().TrimEnd('/') ?? string.Empty;
            if (value.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
                    uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    return false;
                var pathStart = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
                value = pathStart < 0 ? string.Empty : value[(pathStart + 1)..];
            }

            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
            var parts = value.Split('/');
            if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part is "." or ".." ||
                    part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))))
                return false;
            repository = value;
            return true;
        }

        /// <summary>
        /// Compares repository identities after normalizing supported input forms.
        /// </summary>
        internal static bool Equals(string left, string right) =>
            TryParse(left, out var first) && TryParse(right, out var second) &&
            string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }
}
