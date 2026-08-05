// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Resources.Strings;
using Microsoft.Maui.Controls;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Exposes the active UI language for ASLM, applies culture to RESX resources,
    /// and notifies <see cref="ILocalizable"/> views when the language changes.
    /// </summary>
    public sealed class AppLocalizationService
    {
        private static readonly HashSet<string> RtlLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "ar",
        };

        private readonly AppDataStore _appData;
        private readonly List<WeakReference<ILocalizable>> _localizableViews = [];
        private readonly object _localizableLock = new();
        private string _appliedLanguage = "en";

        private static readonly string[] SupportedLanguageCodes =
        [
            "en",
            "zh-Hans",
            "es",
            "ar",
            "hi",
            "pt-BR",
            "ru",
            "ja",
            "de",
            "fr",
            "ko",
            "it",
            "zh-Hant",
            "pt",
            "tr",
            "pl",
            "uk",
            "id",
            "vi",
            "nl",
        ];


        // Supported languages

        /// <summary>
        /// Languages available in personalization settings, sorted by English name.
        /// </summary>
        public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
            SupportedLanguageCodes
                .Select(id => new LanguageOption(id, GetCultureEnglishName(id)))
                .OrderBy(option => option.EnglishName, StringComparer.OrdinalIgnoreCase)
                .ToArray();


        // Events

        /// <summary>
        /// Raised after <see cref="ApplyCulture"/> updates thread culture and resources.
        /// </summary>
        public event EventHandler? CultureChanged;


        // Initialization

        /// <summary>
        /// Creates the localization service.
        /// </summary>
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
            var language = GetCurrentLanguage();
            if (string.Equals(_appliedLanguage, language, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(CultureInfo.CurrentUICulture.Name, language, StringComparison.OrdinalIgnoreCase))
            {
                AppResources.Culture = CultureInfo.CurrentUICulture;
                ApplyFlowDirection(CultureInfo.CurrentUICulture);
                return false;
            }

            var culture = CreateCulture(language);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            AppResources.Culture = culture;
            _appliedLanguage = language;

            ApplyFlowDirection(culture);
            NotifyLocalizableViews();
            CultureChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }


        // String lookup

        /// <summary>
        /// Returns a localized string for the given resource key.
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
        /// Returns a formatted localized string.
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

        private const string MachineTranslationPickerTag = "[AI]";

        /// <summary>
        /// Returns the bilingual label for the language picker: English name — native name,
        /// with a fixed English machine-translation tag for non-English locales.
        /// </summary>
        public static string GetPickerDisplayName(string languageCode)
        {
            var normalized = AppPersonalizationConfig.NormalizeLanguage(languageCode);
            var english = GetCultureEnglishName(normalized);
            var native = GetCultureNativeName(normalized);

            var label = string.Equals(english, native, StringComparison.OrdinalIgnoreCase)
                ? english
                : $"{english} - {native}";

            if (!string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase))
            {
                label = $"{label} {MachineTranslationPickerTag}";
            }

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
            var isRtl = RtlLanguageCodes.Contains(culture.Name) ||
                        RtlLanguageCodes.Contains(culture.TwoLetterISOLanguageName) ||
                        culture.TextInfo.IsRightToLeft;

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

    /// <summary>
    /// One selectable UI language in personalization settings.
    /// </summary>
    public sealed record LanguageOption(string Id, string EnglishName);
}
