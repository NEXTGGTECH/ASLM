// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Provides property notifications for settings presentation models without UI dependencies.
    /// </summary>
    public abstract class SettingsBindableObject : INotifyPropertyChanged
    {
        /// <summary>
        /// Raised when a property value changes so bound views refresh.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Updates one backing field and notifies bindings only when its value changes.
        /// Returns true when the value changed; the property name defaults to the calling member.
        /// </summary>
        protected bool SetProperty<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        /// <summary>
        /// Notifies bindings that a computed property changed after related state was updated.
        /// </summary>
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
