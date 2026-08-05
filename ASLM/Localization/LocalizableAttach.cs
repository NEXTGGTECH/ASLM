// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Localization
{
    /// <summary>
    /// Hooks <see cref="ILocalizable"/> views into <see cref="AppLocalizationService"/> lifetime events.
    /// </summary>
    public static class LocalizableAttach
    {
        /// <summary>
        /// Registers localization refresh handlers on <paramref name="element"/> loaded/unloaded events.
        /// </summary>
        public static void Hook(VisualElement element, AppLocalizationService localization, ILocalizable target)
        {
            element.Loaded += (_, _) =>
            {
                localization.Register(target);
                target.ApplyLocalization();
            };

            element.Unloaded += (_, _) => localization.Unregister(target);
        }
    }
}
