// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Creates and resolves engine-specific dependency environments for individual modules.
    /// </summary>
    public class ModuleEnvironmentResolver
    {
        private readonly EngineInstaller _engineInstaller;
        private readonly SemaphoreSlim _environmentLock = new(1, 1);


        // Initialization

        /// <summary>
        /// Creates the module environment service.
        /// </summary>
        public ModuleEnvironmentResolver(EngineInstaller engineInstaller)
        {
            _engineInstaller = engineInstaller;
        }


        // Environment lifecycle

        /// <summary>
        /// Returns whether the engine declares per-module environments.
        /// </summary>
        public static bool HasModuleEnvironment(EngineConfig engine) =>
            engine.ModuleEnvironment is { Enabled: true };

        /// <summary>
        /// Ensures the module-specific environment exists and returns its resolved paths.
        /// </summary>
        public async Task<ModuleEnvironmentResolution?> EnsureEnvironmentAsync(
            ModuleConfig module,
            EngineConfig engine,
            IProgress<string>? log,
            CancellationToken ct)
        {
            if (!HasModuleEnvironment(engine))
            {
                return null;
            }

            var resolution = ResolveEnvironment(module, engine);
            Directory.CreateDirectory(resolution.DirectoryPath);

            if (IsEnvironmentReady(engine, resolution))
            {
                return resolution;
            }

            await _environmentLock.WaitAsync(ct);
            try
            {
                // Re-check after acquiring the lock so only one creator runs.
                if (IsEnvironmentReady(engine, resolution))
                {
                    return resolution;
                }

                var config = engine.ModuleEnvironment!;
                if (string.IsNullOrWhiteSpace(config.CreateCommand))
                {
                    return resolution;
                }

                log?.Report($"[Env] Creating environment for {module.Name}: {resolution.DirectoryPath}");

                var engineExecutable = ResolveEngineExecutable(engine);
                var createArguments = ResolveTemplate(config.CreateCommand, module, engine, resolution);

                var created = await RunProcessAsync(
                    engineExecutable,
                    createArguments,
                    Path.GetDirectoryName(engine.SourcePath) ?? GetRootDirectory(),
                    log,
                    ct);

                if (!created && IsPythonVirtualEnvironment(engine))
                {
                    created = await TryCreatePythonVirtualenvFallbackAsync(module, engine, resolution, log, ct);
                }

                if (!created || !IsEnvironmentReady(engine, resolution))
                {
                    throw new InvalidOperationException(
                        $"Environment for module '{module.Name}' could not be created by engine '{engine.Id}'.");
                }

                return resolution;
            }
            finally
            {
                _environmentLock.Release();
            }
        }


        // Environment resolution

        /// <summary>
        /// Resolves the module environment path and executable/package-manager settings.
        /// </summary>
        public ModuleEnvironmentResolution ResolveEnvironment(ModuleConfig module, EngineConfig engine)
        {
            if (!HasModuleEnvironment(engine))
            {
                throw new InvalidOperationException($"Engine '{engine.Id}' does not declare a module environment.");
            }

            var engineDir = Path.GetDirectoryName(engine.SourcePath)
                ?? throw new InvalidOperationException($"Engine '{engine.Id}' has no source directory.");
            var config = engine.ModuleEnvironment!;
            var moduleSlug = NormalizeModuleSlug(module);
            var environmentDir = Path.Combine(engineDir, $"{config.DirectoryPrefix}{moduleSlug}");
            var resolution = new ModuleEnvironmentResolution(
                DirectoryPath: environmentDir,
                ExecutablePath: string.Empty,
                PackageManagerExecutablePath: string.Empty,
                PackageManagerCommand: string.Empty,
                EnvironmentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            var executableTemplate = string.IsNullOrWhiteSpace(config.ExecutablePath)
                ? "{engineExecutable}"
                : config.ExecutablePath;
            var executablePath = ResolvePathTemplate(executableTemplate, module, engine, resolution);

            var packageManagerExecutableTemplate = !string.IsNullOrWhiteSpace(config.PackageManagerExecutable)
                ? config.PackageManagerExecutable
                : !string.IsNullOrWhiteSpace(engine.PackageManager?.Executable)
                    ? engine.PackageManager!.Executable!
                    : executableTemplate;
            var packageManagerExecutable = ResolvePathTemplate(packageManagerExecutableTemplate, module, engine, resolution);

            var packageManagerCommand = !string.IsNullOrWhiteSpace(config.PackageManagerCommand)
                ? ResolveTemplate(config.PackageManagerCommand, module, engine, resolution)
                : engine.PackageManager?.Command ?? string.Empty;

            var environmentVariables = config.Environment
                .ToDictionary(
                    pair => pair.Key,
                    pair => ResolveTemplate(pair.Value, module, engine, resolution),
                    StringComparer.OrdinalIgnoreCase);

            return resolution with
            {
                ExecutablePath = executablePath,
                PackageManagerExecutablePath = packageManagerExecutable,
                PackageManagerCommand = packageManagerCommand,
                EnvironmentVariables = environmentVariables
            };
        }


        // Process environment

        /// <summary>
        /// Applies environment variables declared by the engine's module environment.
        /// </summary>
        public void ApplyEnvironmentVariables(ModuleConfig module, EngineConfig engine, ProcessStartInfo psi)
        {
            if (!HasModuleEnvironment(engine))
            {
                return;
            }

            var resolution = ResolveEnvironment(module, engine);
            foreach (var pair in resolution.EnvironmentVariables)
            {
                var value = pair.Value.Replace("{path}", GetProcessEnvironmentValue(psi, "PATH"), StringComparison.OrdinalIgnoreCase);
                psi.Environment[pair.Key] = value;
            }

            psi.Environment["ASLM_ENGINE_ENV_DIR"] = resolution.DirectoryPath;
        }


        // Package manager

        /// <summary>
        /// Returns a package-manager command line with package names appended.
        /// </summary>
        public string BuildPackageInstallArguments(
            ModuleEnvironmentResolution? resolution,
            EngineConfig engine,
            IEnumerable<string> packages)
        {
            var command = resolution?.PackageManagerCommand ?? engine.PackageManager?.Command ?? string.Empty;
            var packageList = JoinArguments(packages);
            return string.IsNullOrWhiteSpace(packageList)
                ? command
                : $"{command} {packageList}".Trim();
        }

        /// <summary>
        /// Returns the package-manager executable path for one engine/environment pair.
        /// </summary>
        public string ResolvePackageManagerExecutable(ModuleEnvironmentResolution? resolution, EngineConfig engine)
        {
            if (!string.IsNullOrWhiteSpace(resolution?.PackageManagerExecutablePath))
            {
                return resolution.PackageManagerExecutablePath;
            }

            var engineDir = Path.GetDirectoryName(engine.SourcePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(engine.PackageManager?.Executable))
            {
                return Path.GetFullPath(Path.Combine(engineDir, engine.PackageManager!.Executable!));
            }

            return ResolveEngineExecutable(engine);
        }

        /// <summary>
        /// Returns the executable used for module commands for one engine/environment pair.
        /// </summary>
        public string ResolveCommandExecutable(ModuleEnvironmentResolution? resolution, EngineConfig engine)
        {
            if (!string.IsNullOrWhiteSpace(resolution?.ExecutablePath))
            {
                return resolution.ExecutablePath;
            }

            return ResolveEngineExecutable(engine);
        }


        // Engine executable

        /// <summary>
        /// Resolves the installed engine executable from the manifest.
        /// </summary>
        private string ResolveEngineExecutable(EngineConfig engine) =>
            _engineInstaller.GetEngineExecutablePath(engine.Id)
            ?? throw new InvalidOperationException($"Engine executable not found for '{engine.Id}'.");


        // Environment readiness

        /// <summary>
        /// Returns whether a declared environment already has its expected executable.
        /// </summary>
        private static bool IsEnvironmentReady(EngineConfig engine, ModuleEnvironmentResolution resolution)
        {
            var config = engine.ModuleEnvironment;
            if (config == null || string.IsNullOrWhiteSpace(config.CreateCommand))
            {
                return Directory.Exists(resolution.DirectoryPath);
            }

            return File.Exists(resolution.ExecutablePath);
        }

        /// <summary>
        /// Returns whether the manifest describes a Python virtual environment.
        /// </summary>
        private static bool IsPythonVirtualEnvironment(EngineConfig engine)
        {
            var kind = engine.ModuleEnvironment?.Kind ?? string.Empty;
            if (kind.Contains("python", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var executableName = Path.GetFileName(engine.ExecutablePath);
            return executableName.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                   executableName.StartsWith("py", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Falls back to virtualenv when an embeddable Python runtime cannot run the stdlib venv module.
        /// </summary>
        private async Task<bool> TryCreatePythonVirtualenvFallbackAsync(
            ModuleConfig module,
            EngineConfig engine,
            ModuleEnvironmentResolution resolution,
            IProgress<string>? log,
            CancellationToken ct)
        {
            if (engine.PackageManager == null)
            {
                return false;
            }

            log?.Report("[Env] Python venv failed, trying virtualenv fallback...");

            var engineExecutable = ResolveEngineExecutable(engine);
            var packageManagerArguments = $"{engine.PackageManager.Command} virtualenv";
            var installed = await RunProcessAsync(
                engineExecutable,
                packageManagerArguments,
                Path.GetDirectoryName(engine.SourcePath) ?? GetRootDirectory(),
                log,
                ct);
            if (!installed)
            {
                return false;
            }

            var fallbackArguments = $"-m virtualenv {QuoteArgument(resolution.DirectoryPath)}";
            return await RunProcessAsync(
                engineExecutable,
                fallbackArguments,
                Path.GetDirectoryName(module.SourcePath) ?? GetRootDirectory(),
                log,
                ct);
        }


        // Process execution

        /// <summary>
        /// Runs one process and streams output into the provided log.
        /// </summary>
        private static async Task<bool> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return false;
            }

            var stdoutTask = DrainOutputAsync(process.StandardOutput, log, ct);
            var stderrTask = DrainOutputAsync(process.StandardError, log, ct);

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdoutTask, stderrTask);

            return process.ExitCode == 0;
        }

        /// <summary>
        /// Streams process output lines to the log.
        /// </summary>
        private static async Task DrainOutputAsync(StreamReader reader, IProgress<string>? log, CancellationToken ct)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    log?.Report($"  {line}");
                }
            }
        }


        // Template resolution

        /// <summary>
        /// Resolves a template that is expected to produce a filesystem path.
        /// </summary>
        private string ResolvePathTemplate(
            string template,
            ModuleConfig module,
            EngineConfig engine,
            ModuleEnvironmentResolution resolution)
        {
            var engineDir = Path.GetDirectoryName(engine.SourcePath)
                ?? throw new InvalidOperationException($"Engine '{engine.Id}' has no source directory.");
            var resolved = ResolveTemplate(template, module, engine, resolution);

            return Path.IsPathRooted(resolved)
                ? Path.GetFullPath(resolved)
                : Path.GetFullPath(Path.Combine(engineDir, resolved));
        }

        /// <summary>
        /// Resolves supported path and environment tokens inside manifest strings.
        /// </summary>
        private string ResolveTemplate(
            string template,
            ModuleConfig module,
            EngineConfig engine,
            ModuleEnvironmentResolution resolution)
        {
            var engineDir = Path.GetDirectoryName(engine.SourcePath) ?? string.Empty;
            var moduleDir = Path.GetDirectoryName(module.SourcePath) ?? string.Empty;
            var engineExecutable = ResolveEngineExecutable(engine);
            var runtimeDir = Path.Combine(engineDir, "runtime");

            return (template ?? string.Empty)
                .Replace("{engineDir}", engineDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{runtimeDir}", runtimeDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{engineExecutable}", engineExecutable, StringComparison.OrdinalIgnoreCase)
                .Replace("{environmentDir}", resolution.DirectoryPath, StringComparison.OrdinalIgnoreCase)
                .Replace("{moduleDir}", moduleDir, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds a stable ASCII-only module slug for an environment folder.
        /// </summary>
        private static string NormalizeModuleSlug(ModuleConfig module)
        {
            var raw = string.IsNullOrWhiteSpace(module.Id) ? module.Name : module.Id;
            var builder = new StringBuilder();
            var previousWasSeparator = false;

            foreach (var character in raw.ToLowerInvariant())
            {
                if ((character >= 'a' && character <= 'z') || char.IsDigit(character))
                {
                    builder.Append(character);
                    previousWasSeparator = false;
                }
                else if (character is '-' or '_' || char.IsWhiteSpace(character))
                {
                    if (builder.Length > 0 && !previousWasSeparator)
                    {
                        builder.Append('-');
                        previousWasSeparator = true;
                    }
                }
            }

            var slug = builder.ToString().Trim('-');
            if (!string.IsNullOrWhiteSpace(slug))
            {
                return slug;
            }

            var hash = Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw ?? string.Empty)))
                .ToLowerInvariant()[..8];
            return $"module-{hash}";
        }


        // Command helpers

        /// <summary>
        /// Returns the current process environment variable value from a ProcessStartInfo.
        /// </summary>
        private static string GetProcessEnvironmentValue(ProcessStartInfo psi, string key)
        {
            foreach (var pair in psi.Environment)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value ?? string.Empty;
                }
            }

            return Environment.GetEnvironmentVariable(key) ?? string.Empty;
        }

        /// <summary>
        /// Quotes one argument when it contains whitespace.
        /// </summary>
        private static string QuoteArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }

            return argument.Any(char.IsWhiteSpace)
                ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
                : argument;
        }

        /// <summary>
        /// Joins command-line arguments with basic quoting for whitespace.
        /// </summary>
        private static string JoinArguments(IEnumerable<string> arguments) =>
            string.Join(" ", arguments.Select(QuoteArgument));

        /// <summary>
        /// Returns the application root directory.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }

    /// <summary>
    /// Stores resolved per-module environment paths for one engine.
    /// </summary>
    public sealed record ModuleEnvironmentResolution(
        string DirectoryPath,
        string ExecutablePath,
        string PackageManagerExecutablePath,
        string PackageManagerCommand,
        IReadOnlyDictionary<string, string> EnvironmentVariables);
}
