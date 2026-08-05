// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Hosts the bindable state consumed by the native console output handler.
    /// </summary>
    public sealed class ConsoleOutputView : View
    {
        // Bindable properties

        /// <summary>
        /// Identifies the bindable console text property.
        /// </summary>
        public static readonly BindableProperty TextProperty =
            BindableProperty.Create(nameof(Text), typeof(string), typeof(ConsoleOutputView), string.Empty);

        /// <summary>
        /// Identifies the bindable session key property used to detect selection changes.
        /// </summary>
        public static readonly BindableProperty SessionKeyProperty =
            BindableProperty.Create(nameof(SessionKey), typeof(string), typeof(ConsoleOutputView), string.Empty);

        /// <summary>
        /// Gets or sets the console text rendered by the native host.
        /// </summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>
        /// Gets or sets the composite session key used to reset scroll position for a new session.
        /// </summary>
        public string SessionKey
        {
            get => (string)GetValue(SessionKeyProperty);
            set => SetValue(SessionKeyProperty, value);
        }
    }
}
