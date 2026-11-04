// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Implements the shared compact settings toggle with its visual structure defined in XAML.
    /// </summary>
    public partial class SettingsToggle : ContentView
    {
        // Fields

        /// <summary>
        /// True while the state is set programmatically, so <see cref="Toggled"/> is not raised for it.
        /// </summary>
        private bool _suppressToggleEvent;


        // Bindable properties

        /// <summary>
        /// Identifies the two-way bindable property that holds the toggle state.
        /// </summary>
        public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
            nameof(IsToggled),
            typeof(bool),
            typeof(SettingsToggle),
            false,
            BindingMode.TwoWay,
            propertyChanged: OnIsToggledChanged);


        // Initialization

        /// <summary>
        /// Creates the compact toggle backed by the native platform switch.
        /// </summary>
        public SettingsToggle()
        {
            InitializeComponent();
        }


        // Properties and events

        /// <summary>
        /// Occurs when the user or a binding changes the toggle value, except for changes made through
        /// <see cref="SetStateWithoutToggleEvent"/>.
        /// </summary>
        public event EventHandler<ToggledEventArgs>? Toggled;

        /// <summary>
        /// Gets or sets the current toggle state.
        /// </summary>
        public bool IsToggled
        {
            get => (bool)GetValue(IsToggledProperty);
            set => SetValue(IsToggledProperty, value);
        }


        // State changes

        /// <summary>
        /// Updates the state without notifying draft synchronization handlers.
        /// </summary>
        public void SetStateWithoutToggleEvent(bool isToggled)
        {
            // The flag is always reset, even when a property-changed handler throws.
            _suppressToggleEvent = true;
            try
            {
                IsToggled = isToggled;
            }
            finally
            {
                _suppressToggleEvent = false;
            }
        }

        /// <summary>
        /// Applies state changes raised by binding or direct property assignment.
        /// </summary>
        private static void OnIsToggledChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var toggle = (SettingsToggle)bindable;

            if (!toggle._suppressToggleEvent)
            {
                toggle.Toggled?.Invoke(toggle, new ToggledEventArgs((bool)newValue));
            }
        }
    }
}
