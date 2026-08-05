// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Localization
{
    /// <summary>
    /// Views that can refresh user-visible strings after the active UI culture changes.
    /// </summary>
    public interface ILocalizable
    {
        /// <summary>
        /// Reapplies localized text to static and dynamically built UI elements.
        /// </summary>
        void ApplyLocalization();
    }
}
