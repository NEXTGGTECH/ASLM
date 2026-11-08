// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Builds minimal valid <see cref="ModuleConfig"/> instances for tests that need a module but not a real manifest.
    /// </summary>
    public static class ModuleConfigBuilder
    {
        /// <summary>
        /// Creates a normalized web module with one run command, one <c>port</c> setting named <c>http</c> and a
        /// GitHub source. The manifest path points to <c>Modules/{id}/ASLM_Module.json</c> beside the test output
        /// directory, but nothing is written to disk.
        /// </summary>
        /// <param name="id">Module id, also used as the module folder name in the manifest path.</param>
        /// <param name="name">Display name of the module.</param>
        /// <param name="configure">Optional callback that adjusts the module after normalization.</param>
        public static ModuleConfig Create(
            string id = "test-module",
            string name = "Test Module",
            Action<ModuleConfig>? configure = null)
        {
            // Defaults describe a plain web module with a single run command and a port setting.
            var module = new ModuleConfig
            {
                Id = id,
                Name = name,
                Description = "Test module",
                Version = "1.0.0",
                Author = "ASLM Tests",
                Type = "web",
                SourcePath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..",
                    "Modules",
                    id,
                    "ASLM_Module.json"),
                Source = new ModuleSource
                {
                    Type = "github",
                    Repo = "Example/Module"
                },
                Commands = new ModuleCommands
                {
                    Run = [new ModuleCommand { Name = "Run", Exec = "echo test" }]
                },
                Settings =
                [
                    new ModuleSetting
                    {
                        Key = "http",
                        Type = "port",
                        Name = "HTTP",
                        Default = "0"
                    }
                ]
            };

            // Normalize first so the callback can override normalized values.
            module.Normalize();
            configure?.Invoke(module);
            return module;
        }
    }
}
