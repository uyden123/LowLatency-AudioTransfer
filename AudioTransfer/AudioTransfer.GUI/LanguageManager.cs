using System;
using System.Linq;

namespace AudioTransfer.GUI
{
    /// <summary>
    /// Singleton manager for dynamic language switching using ResourceDictionary.
    /// Swaps Strings.en.xaml / Strings.vi.xaml at runtime without restart.
    /// </summary>
    public sealed class LanguageManager
    {
        private static readonly Lazy<LanguageManager> _instance = new(() => new LanguageManager());
        public static LanguageManager Instance => _instance.Value;

        /// <summary>
        /// Currently active language code: "English" or "Vietnamese"
        /// </summary>
        public string CurrentLanguage { get; private set; } = "English";

        /// <summary>
        /// Returns true if the current language is Vietnamese.
        /// </summary>
        public bool IsVietnamese => CurrentLanguage == "Vietnamese";

        /// <summary>
        /// Fired after the language ResourceDictionary has been swapped.
        /// </summary>
        public event EventHandler<string>? LanguageChanged;

        private LanguageManager() { }

        /// <summary>
        /// Swap the active string ResourceDictionary in Application.Current.Resources.
        /// Because all UI elements use {DynamicResource ...}, they will update instantly.
        /// </summary>
        public void ApplyLanguage(string language)
        {
            try
            {
                bool isVi = language == "Vietnamese";
                string langFile = isVi ? "Themes/Strings.vi.xaml" : "Themes/Strings.en.xaml";

                var newDict = new System.Windows.ResourceDictionary { Source = new Uri(langFile, UriKind.Relative) };

                var existingDict = System.Windows.Application.Current.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null &&
                        (d.Source.OriginalString.Contains("Strings.vi.xaml") ||
                         d.Source.OriginalString.Contains("Strings.en.xaml")));

                if (existingDict != null)
                {
                    System.Windows.Application.Current.Resources.MergedDictionaries.Remove(existingDict);
                }

                System.Windows.Application.Current.Resources.MergedDictionaries.Add(newDict);
                CurrentLanguage = language;
                LanguageChanged?.Invoke(this, language);
            }
            catch (Exception ex)
            {
                AudioTransfer.Core.Logging.CoreLogger.Instance.Log($"[LanguageManager] Failed to apply language '{language}': {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves a localized string from Application resources by key.
        /// Useful for code-behind where DynamicResource is not available.
        /// </summary>
        public string GetString(string key)
        {
            if (System.Windows.Application.Current.TryFindResource(key) is string value)
                return value;
            return key; // Fallback: return the key itself
        }
    }
}
