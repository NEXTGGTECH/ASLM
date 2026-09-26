// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;

namespace ASLM.Controls.Modules;

/// <summary>Confirms module removal and displays its failure or retained-file information.</summary>
public partial class ModuleUninstallConfirmationView : ContentView, ILocalizable
{
    private TaskCompletionSource<bool>? _completion;
    private string? _titleKey;
    private string? _messageKey;
    private object[] _arguments = [];
    private bool _isConfirmation;

    internal bool IsOpen => _completion != null;

    public ModuleUninstallConfirmationView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ResizeDialog();
        Unloaded += (_, _) => Dismiss();
    }

    internal void Initialize(AppLocalizationService localization) => LocalizableAttach.Hook(this, localization, this);

    internal Task<bool> ConfirmAsync(string moduleName) => ShowAsync(
        LocalizationKeys.Modules_RemoveConfirmTitle, LocalizationKeys.Modules_RemoveConfirmFormat,
        [moduleName], isConfirmation: true);

    internal Task<bool> ShowFailureAsync(string message) => ShowAsync(
        LocalizationKeys.Modules_RemoveFailed, null, [message], isConfirmation: false);

    internal Task<bool> ShowCleanupAsync(string directory) => ShowAsync(
        LocalizationKeys.Modules_RemoveCleanupTitle, LocalizationKeys.Modules_RemoveCleanupFormat,
        [directory], isConfirmation: false);

    internal void Dismiss() => _completion?.TrySetResult(false);

    private async Task<bool> ShowAsync(string titleKey, string? messageKey, object[] arguments, bool isConfirmation)
    {
        if (IsOpen) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _titleKey = titleKey;
        _messageKey = messageKey;
        _arguments = arguments;
        _isConfirmation = isConfirmation;
        ApplyLocalization();
        ResizeDialog();
        IsVisible = true;
        try
        {
            return await completion.Task;
        }
        finally
        {
            IsVisible = false;
            _completion = null;
            _titleKey = null;
            _messageKey = null;
            _arguments = [];
        }
    }

    public void ApplyLocalization()
    {
        if (_titleKey == null) return;
        TitleLabel.Text = L.Get(_titleKey, _arguments);
        MessageLabel.Text = _messageKey != null ? L.Get(_messageKey, _arguments) : _arguments.FirstOrDefault()?.ToString();
        UninstallButton.Text = L.Get(LocalizationKeys.Modules_Remove);
        UninstallButton.IsVisible = _isConfirmation;
        OkButton.Text = L.Get(LocalizationKeys.Common_OK);
        OkButton.IsVisible = !_isConfirmation;
    }

    private void ResizeDialog() => DialogBorder.WidthRequest = Math.Max(0, Math.Min(540, Width - 48));
    private void OnCancelClicked(object? sender, EventArgs e) => Dismiss();
    private void OnConfirmClicked(object? sender, EventArgs e) => _completion?.TrySetResult(true);
}
