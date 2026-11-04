// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Resources.Strings;
using Microsoft.Maui.Controls;

namespace ASLM.Services.Internal
{
    // Localization service

    /// <summary>
    /// Exposes the active UI language for ASLM, applies culture to RESX resources,
    /// and notifies <see cref="ILocalizable"/> views when the language changes.
    /// </summary>
    public sealed class AppLocalizationService
    {
        // Fields and constants

        /// <summary>
        /// Language codes that are laid out right to left even when the culture data does not say so.
        /// </summary>
        private static readonly HashSet<string> RtlLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "ar",
        };

        /// <summary>
        /// Persisted application data that holds the selected UI language.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Weakly referenced views to refresh on a culture change, so registration never keeps a view alive.
        /// </summary>
        private readonly List<WeakReference<ILocalizable>> _localizableViews = [];

        /// <summary>
        /// Guards <see cref="_localizableViews"/> against concurrent registration and notification.
        /// </summary>
        private readonly object _localizableLock = new();

        /// <summary>
        /// Language code applied by the last <see cref="ApplyCulture"/> call; used to detect real language changes.
        /// </summary>
        private string _appliedLanguage = "en";


        // Supported languages

        /// <summary>
        /// Languages available in personalization settings, sorted by English name.
        /// </summary>
        public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
            AppPersonalizationConfig.SupportedLanguageCodes
                .Select(id => new LanguageOption(id, GetCultureEnglishName(id)))
                .OrderBy(option => option.EnglishName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        /// <summary>
        /// Resolves the user's primary OS language independently of ASLM's current UI culture.
        /// Used for new profiles and when personalization settings are reset to defaults.
        /// </summary>
        public static string GetDefaultLanguage()
        {
            // Each platform exposes the OS language differently; failures fall back to English.
            try
            {
#if WINDOWS
                var language = global::Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault();
#elif MACCATALYST
                var language = Foundation.NSLocale.PreferredLanguages.FirstOrDefault();
#else
                var language = CultureInfo.CurrentUICulture.Name;
#endif
                return ResolveSystemLanguage(language);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Localization] Failed to read the system UI language: {ex.Message}");
                return "en";
            }
        }

        /// <summary>
        /// Matches an OS locale to a shipped translation, preserving script and regional variants.
        /// For example, ru-RU resolves to ru, pt-BR stays pt-BR, and zh-TW resolves to zh-Hant.
        /// </summary>
        internal static string ResolveSystemLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "en";
            }

            try
            {
                // Apple locale identifiers can use underscores instead of BCP-47 hyphens.
                var culture = CultureInfo.GetCultureInfo(language.Trim().Replace('_', '-'));

                // Walk from the specific culture up through its parents until a shipped translation matches.
                while (!string.IsNullOrEmpty(culture.Name))
                {
                    if (AppPersonalizationConfig.SupportedLanguageCodes.TryGetValue(culture.Name, out var supported))
                    {
                        return supported;
                    }

                    // A neutral Chinese locale has no region or script; use simplified Chinese.
                    if (string.Equals(culture.Name, "zh", StringComparison.OrdinalIgnoreCase))
                    {
                        return "zh-Hans";
                    }

                    culture = culture.Parent;
                }
            }
            catch (CultureNotFoundException)
            {
                // Unknown OS language identifiers use the English resource bundle.
            }

            return "en";
        }


        // Events

        /// <summary>
        /// Raised after <see cref="ApplyCulture"/> updates thread culture and resources.
        /// </summary>
        public event EventHandler? CultureChanged;


        // Initialization

        /// <summary>
        /// Creates the localization service.
        /// </summary>
        /// <param name="appData">Application data that provides the persisted UI language.</param>
        public AppLocalizationService(AppDataStore appData)
        {
            _appData = appData;
        }


        // Culture application

        /// <summary>
        /// Returns the normalized language code from persisted personalization.
        /// </summary>
        public string GetCurrentLanguage() =>
            AppPersonalizationConfig.NormalizeLanguage(_appData.Data.Personalization.Language);

        /// <summary>
        /// Reapplies RTL/LTR on the active window page.
        /// Call after replacing <see cref="Window.Page"/> so the new root page inherits flow direction.
        /// </summary>
        public void SyncFlowDirection() =>
            ApplyFlowDirection(CultureInfo.CurrentUICulture);

        /// <summary>
        /// Applies the persisted language to UI culture and RESX resources.
        /// </summary>
        /// <returns>True when the culture actually changed.</returns>
        public bool ApplyCulture()
        {
            // The language counts as changed when either the last applied one or the live UI culture differs.
            var language = GetCurrentLanguage();
            var changed = !string.Equals(_appliedLanguage, language, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(CultureInfo.CurrentUICulture.Name, language, StringComparison.OrdinalIgnoreCase);

            // Apply the culture to new and current threads and to the RESX resources.
            var culture = CreateCulture(language);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            AppResources.Culture = culture;
            _appliedLanguage = language;

            // Flow direction is refreshed on every call; views and listeners are notified only after a real change.
            ApplyFlowDirection(culture);
            if (!changed)
            {
                return false;
            }

            NotifyLocalizableViews();
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }


        // String lookup

        /// <summary>
        /// Returns a localized string for the given resource key.
        /// A blank key yields an empty string, and a key without a translation is returned unchanged.
        /// </summary>
        public string GetString(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return AppResources.ResourceManager.GetString(key, AppResources.Culture) ?? key;
        }

        /// <summary>
        /// Returns a localized string for the given resource key, formatted with <paramref name="args"/>.
        /// </summary>
        public string GetString(string key, params object[] args)
        {
            var format = GetString(key);
            return args.Length == 0 ? format : string.Format(format, args);
        }


        // View registration

        /// <summary>
        /// Registers a view that should refresh when the active culture changes.
        /// </summary>
        public void Register(ILocalizable view)
        {
            if (view == null)
            {
                return;
            }

            lock (_localizableLock)
            {
                PruneLocalizableViews();
                if (_localizableViews.All(reference => !reference.TryGetTarget(out var target) || !ReferenceEquals(target, view)))
                {
                    _localizableViews.Add(new WeakReference<ILocalizable>(view));
                }
            }
        }

        /// <summary>
        /// Removes a previously registered view.
        /// </summary>
        public void Unregister(ILocalizable view)
        {
            if (view == null)
            {
                return;
            }

            lock (_localizableLock)
            {
                _localizableViews.RemoveAll(reference =>
                    !reference.TryGetTarget(out var target) || ReferenceEquals(target, view));
            }
        }


        // Display names

        /// <summary>
        /// Returns the native display name for a language code, or the code itself when unknown.
        /// </summary>
        public static string GetDisplayName(string languageCode) =>
            GetCultureNativeName(AppPersonalizationConfig.NormalizeLanguage(languageCode));

        /// <summary>
        /// Returns the bilingual label for the language picker: "English name - native name",
        /// or just the English name when both are identical.
        /// </summary>
        public static string GetPickerDisplayName(string languageCode)
        {
            var normalized = AppPersonalizationConfig.NormalizeLanguage(languageCode);
            var english = GetCultureEnglishName(normalized);
            var native = GetCultureNativeName(normalized);

            var label = string.Equals(english, native, StringComparison.OrdinalIgnoreCase)
                ? english
                : $"{english} - {native}";

            return label;
        }


        // Culture helpers

        /// <summary>
        /// Returns the English culture name for one language code.
        /// </summary>
        private static string GetCultureEnglishName(string languageCode)
        {
            try
            {
                return CreateCulture(languageCode).EnglishName;
            }
            catch (CultureNotFoundException)
            {
                return languageCode;
            }
        }

        /// <summary>
        /// Returns the native culture name for one language code.
        /// </summary>
        private static string GetCultureNativeName(string languageCode)
        {
            try
            {
                return CreateCulture(languageCode).NativeName;
            }
            catch (CultureNotFoundException)
            {
                return languageCode;
            }
        }

        /// <summary>
        /// Creates a <see cref="CultureInfo"/> for one supported language code, falling back to English.
        /// </summary>
        private static CultureInfo CreateCulture(string language)
        {
            try
            {
                return language switch
                {
                    "zh-Hans" => CultureInfo.GetCultureInfo("zh-Hans"),
                    "zh-Hant" => CultureInfo.GetCultureInfo("zh-Hant"),
                    "pt-BR" => CultureInfo.GetCultureInfo("pt-BR"),
                    _ => CultureInfo.GetCultureInfo(language),
                };
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.GetCultureInfo("en");
            }
        }

        /// <summary>
        /// Applies RTL or LTR flow direction to the active application page.
        /// Embedded <see cref="WebView"/> controls stay LTR because module UIs manage direction themselves.
        /// </summary>
        private static void ApplyFlowDirection(CultureInfo culture)
        {
            // Direction comes from the explicit RTL list or from the culture's own text direction.
            var isRtl = RtlLanguageCodes.Contains(culture.Name) ||
                        RtlLanguageCodes.Contains(culture.TwoLetterISOLanguageName) ||
                        culture.TextInfo.IsRightToLeft;

            // Without a window page there is nothing to update yet.
            var flow = isRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page == null)
            {
                return;
            }

            page.FlowDirection = flow;
            ResetEmbeddedWebViewsOnPage(page);

            // Shell RTL is applied before the visual tree finishes updating; run again on the UI queue.
            if (flow == FlowDirection.RightToLeft)
            {
                MainThread.BeginInvokeOnMainThread(() => ResetEmbeddedWebViewsOnPage(page));
            }
        }

        /// <summary>
        /// Walks the active page and pins every embedded <see cref="WebView"/> to LTR.
        /// </summary>
        private static void ResetEmbeddedWebViewsOnPage(Page page)
        {
            if (page is ContentPage contentPage && contentPage.Content is Element pageRoot)
            {
                ResetEmbeddedWebViewsToLeftToRight(pageRoot);
            }
            else if (page is Element pageElement)
            {
                ResetEmbeddedWebViewsToLeftToRight(pageElement);
            }
        }

        /// <summary>
        /// Keeps host-embedded WebViews in LTR so module pages are not mirrored by shell RTL layout.
        /// </summary>
        private static void ResetEmbeddedWebViewsToLeftToRight(Element root)
        {
            if (root is WebView webView)
            {
                webView.FlowDirection = FlowDirection.LeftToRight;
            }

            // Descend through every container type that can host child content.
            switch (root)
            {
                case Layout layout:
                    foreach (var child in layout.Children)
                    {
                        if (child is Element element)
                        {
                            ResetEmbeddedWebViewsToLeftToRight(element);
                        }
                        else if (child is WebView childWebView)
                        {
                            childWebView.FlowDirection = FlowDirection.LeftToRight;
                        }
                    }

                    break;

                case ContentView contentView when contentView.Content is Element content:
                    ResetEmbeddedWebViewsToLeftToRight(content);
                    break;

                case ScrollView scrollView when scrollView.Content is Element scrollContent:
                    ResetEmbeddedWebViewsToLeftToRight(scrollContent);
                    break;

                case Border border when border.Content is Element borderContent:
                    ResetEmbeddedWebViewsToLeftToRight(borderContent);
                    break;
            }
        }


        // View refresh

        /// <summary>
        /// Refreshes every registered localizable view after the culture changes.
        /// </summary>
        private void NotifyLocalizableViews()
        {
            // Snapshot the live views under the lock, then notify outside it so handlers cannot block registration.
            List<ILocalizable> targets;
            lock (_localizableLock)
            {
                PruneLocalizableViews();
                targets = _localizableViews
                    .Select(reference =>
                    {
                        reference.TryGetTarget(out var target);
                        return target;
                    })
                    .Where(target => target != null)
                    .Cast<ILocalizable>()
                    .ToList();
            }

            foreach (var target in targets)
            {
                try
                {
                    target.ApplyLocalization();
                }
                catch
                {
                    // Best-effort refresh for each registered view.
                }
            }
        }

        /// <summary>
        /// Removes dead weak references from the registered view list.
        /// </summary>
        private void PruneLocalizableViews()
        {
            _localizableViews.RemoveAll(reference => !reference.TryGetTarget(out _));
        }
    }


    // Language options

    /// <summary>
    /// One selectable UI language in personalization settings.
    /// </summary>
    /// <param name="Id">Language code stored in personalization settings, such as <c>ru</c> or <c>pt-BR</c>.</param>
    /// <param name="EnglishName">English culture name used to sort and display the language.</param>
    public sealed record LanguageOption(string Id, string EnglishName);
}
