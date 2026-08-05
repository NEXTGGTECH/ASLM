// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// Carries download progress values for UI and notification streaming.
    /// </summary>
    /// <param name="Fraction">Download completion from 0.0 to 1.0.</param>
    /// <param name="DownloadedBytes">Total bytes downloaded so far.</param>
    /// <param name="TotalBytes">Expected total file size in bytes.</param>
    /// <param name="ActiveTransferName">Optional label for the active file or stream.</param>
    public record DownloadProgress(
        double Fraction,
        long DownloadedBytes,
        long TotalBytes,
        string? ActiveTransferName = null);
}
