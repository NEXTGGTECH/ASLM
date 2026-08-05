// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.TestSupport;

public static class ModuleConfigBuilder
{
    public static ModuleConfig Create(
        string id = "test-module",
        string name = "Test Module",
        Action<ModuleConfig>? configure = null)
    {
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

        module.Normalize();
        configure?.Invoke(module);
        return module;
    }
}
