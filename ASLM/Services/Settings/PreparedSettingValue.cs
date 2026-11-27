// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Settings
{
    /// <summary>
    /// Retains a successful runtime value independently from invalidation of its cached getter request.
    /// </summary>
    /// <param name="Definition">Definition that produced the value and identifies compatible future rows.</param>
    /// <param name="Value">Normalized runtime value.</param>
    /// <param name="AutomaticValue">Host-resolved value computed outside the UI thread.</param>
    /// <param name="IsReadOnly">Whether the host owns this value.</param>
    internal sealed record PreparedSettingValue(
        ModuleSetting Definition, object? Value, object? AutomaticValue, bool IsReadOnly);

    /// <summary>
    /// Identifies one completed value without tying its producer to a visible settings page.
    /// </summary>
    /// <param name="SourcePath">Manifest path of the module that owns the value.</param>
    /// <param name="SettingKey">Manifest key of the completed setting.</param>
    /// <param name="Generation">Lifetime that rejects delayed notifications after maintenance.</param>
    internal sealed record PreparedSettingChangedEventArgs(string SourcePath, string SettingKey, CancellationToken Generation);
}
