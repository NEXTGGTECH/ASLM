// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// Describes one module instance that currently has tracked live processes in
    /// <see cref="Services.Modules.ModuleRunner"/>.
    /// </summary>
    /// <param name="Id">Module identifier from its manifest.</param>
    /// <param name="Name">Human-readable module name.</param>
    /// <param name="SourcePath">Path of the module manifest that identifies the instance.</param>
    public sealed record RunningModuleSnapshot(string Id, string Name, string SourcePath);
}
