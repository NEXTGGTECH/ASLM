// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Implements the shared compact settings toggle with its visual structure defined in XAML.
    /// </summary>
    public partial class SettingsToggle : ContentView
    {
        private bool _suppressToggleEvent;

        /// <summary>
        /// Identifies the two-way toggle state bindable property.
        /// </summary>
        public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
            nameof(IsToggled),
            typeof(bool),
            typeof(SettingsToggle),
            false,
            BindingMode.TwoWay,
            propertyChanged: OnIsToggledChanged);

        /// <summary>
        /// Creates the compact toggle backed by the native platform switch.
        /// </summary>
        public SettingsToggle()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Occurs when user or bound state changes the toggle value.
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

        /// <summary>
        /// Updates the state without notifying draft synchronization handlers.
        /// </summary>
        public void SetStateWithoutToggleEvent(bool isToggled)
        {
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
