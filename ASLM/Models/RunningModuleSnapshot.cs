// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// Describes one module instance that currently has tracked live processes in <see cref="Services.ModuleRunner"/>.
    /// </summary>
    public sealed record RunningModuleSnapshot(string Id, string Name, string SourcePath);
}
