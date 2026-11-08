// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM
{
    /// <summary>
    /// Applies a self-update staged on a previous run before the UI starts.
    /// </summary>
    public static class MacPendingUpdateGate
    {
        /// <summary>
        /// When a pending update exists, starts the patcher helper and returns true so Main can exit.
        /// On any failure the update stays pending and normal startup continues.
        /// </summary>
        public static bool TryHandOffToPatcher()
        {
            try
            {
                // An update is staged when pending.json exists in the data root's update folder.
                var pendingPath = Path.Combine(AppRoot.Directory, ".aslm-update", "pending.json");
                if (!File.Exists(pendingPath))
                {
                    return false;
                }

                MacAppRelauncher.StartDetachedRelaunch();
                return true;
            }
            catch
            {
                // Any failure leaves the update pending and lets normal startup continue.
                return false;
            }
        }
    }
}
