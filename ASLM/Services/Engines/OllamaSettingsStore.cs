// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Engines
{
    /// <summary>
    /// Reads the managed Ollama account state and executes account commands against the ASLM runtime.
    /// </summary>
    public sealed class OllamaSettingsStore
    {
        private const int ManagedOllamaPort = 11434;

        private readonly EngineInstaller _engineInstaller;
        private readonly HttpClient _httpClient;
        private readonly ILogger<OllamaSettingsStore> _logger;

        private readonly object _runtimeSync = new();
        private Process? _managedRuntimeProcess;
        private bool _hasVerifiedSignInState;
        private bool _isSignedIn;
        private OllamaPersistentSettings _accountDetails = new();


        // Construction

        /// <summary>
        /// Initializes a new Ollama settings service instance.
        /// </summary>
        public OllamaSettingsStore(
            EngineInstaller engineInstaller,
            ILogger<OllamaSettingsStore> logger)
        {
            _engineInstaller = engineInstaller ?? throw new ArgumentNullException(nameof(engineInstaller));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(2)
            };
        }


        // Settings access

        /// <summary>
        /// Loads the cached managed Ollama settings without querying the runtime.
        /// </summary>
        public OllamaPersistentSettings LoadSettings()
        {
            var executable = ResolveExecutable();
            if (string.IsNullOrWhiteSpace(executable))
            {
                _hasVerifiedSignInState = false;
                _isSignedIn = false;
                ClearAccountDetails();
            }

            return BuildSettingsSnapshot(!string.IsNullOrWhiteSpace(executable));
        }

        /// <summary>
        /// Refreshes the current managed Ollama sign-in state from the local ASLM runtime.
        /// </summary>
        public async Task<OllamaPersistentSettings> RefreshSettingsAsync(CancellationToken ct = default)
        {
            var runtime = await EnsureManagedRuntimeAsync(ct);
            if (!runtime.IsAvailable)
            {
                _hasVerifiedSignInState = false;
                _isSignedIn = false;
                ClearAccountDetails();
                return BuildSettingsSnapshot(isCliAvailable: false);
            }

            await TryRefreshSignInStateAsync(runtime.Port, ct);
            return BuildSettingsSnapshot(isCliAvailable: true);
        }


        // Account actions

        /// <summary>
        /// Runs <c>ollama signin</c> using the ASLM-managed Ollama runtime.
        /// </summary>
        public async Task<OllamaAccountActionResult> SignInAsync(CancellationToken ct = default)
        {
            var runtime = await EnsureManagedRuntimeAsync(ct);
            if (!runtime.IsAvailable)
            {
                return CreateCliUnavailableResult();
            }

            var result = await RunAccountCommandAsync("signin", "Sign-in flow completed.", runtime, ct);
            if (!result.Success)
            {
                return result;
            }

            var isSignedIn = await TryRefreshSignInStateAsync(runtime.Port, ct);
            return new OllamaAccountActionResult
            {
                Success = true,
                Message = result.Message,
                IsPendingVerification = isSignedIn != true
            };
        }

        /// <summary>
        /// Runs <c>ollama signout</c> using the ASLM-managed Ollama runtime.
        /// </summary>
        public async Task<OllamaAccountActionResult> SignOutAsync(CancellationToken ct = default)
        {
            var runtime = await EnsureManagedRuntimeAsync(ct);
            if (!runtime.IsAvailable)
            {
                return CreateCliUnavailableResult();
            }

            var result = await RunAccountCommandAsync("signout", "Signed out from ollama.com.", runtime, ct);
            if (result.Success)
            {
                await TryRefreshSignInStateAsync(runtime.Port, ct);
            }

            return result;
        }


        // Runtime lifecycle

        /// <summary>
        /// Stops the managed Ollama runtime started for the settings UI, if this service owns it.
        /// </summary>
        public void StopManagedRuntime()
        {
            Process? ownedProcess;
            lock (_runtimeSync)
            {
                ownedProcess = _managedRuntimeProcess;
                _managedRuntimeProcess = null;
            }

            if (ownedProcess == null)
            {
                return;
            }

            try
            {
                if (!ownedProcess.HasExited)
                {
                    ownedProcess.Kill(entireProcessTree: true);
                    ownedProcess.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to stop the managed Ollama settings runtime cleanly.");
            }
            finally
            {
                ownedProcess.Dispose();
            }
        }


        // Settings snapshot

        /// <summary>
        /// Builds the current settings snapshot from the cached managed sign-in state.
        /// </summary>
        private OllamaPersistentSettings BuildSettingsSnapshot(bool isCliAvailable)
        {
            var exposesAccount = isCliAvailable && _hasVerifiedSignInState && _isSignedIn;
            return new OllamaPersistentSettings
            {
                IsCliAvailable = isCliAvailable,
                IsSignedIn = exposesAccount,
                UserName = exposesAccount ? _accountDetails.UserName : string.Empty,
                UserId = exposesAccount ? _accountDetails.UserId : string.Empty,
                Email = exposesAccount ? _accountDetails.Email : string.Empty,
                Bio = exposesAccount ? _accountDetails.Bio : string.Empty,
                AvatarUrl = exposesAccount ? _accountDetails.AvatarUrl : string.Empty,
                FirstName = exposesAccount ? _accountDetails.FirstName : string.Empty,
                LastName = exposesAccount ? _accountDetails.LastName : string.Empty,
                Plan = exposesAccount ? _accountDetails.Plan : string.Empty
            };
        }

        /// <summary>
        /// Clears account metadata when the managed runtime or authenticated session is unavailable.
        /// </summary>
        private void ClearAccountDetails()
        {
            _accountDetails = new OllamaPersistentSettings();
        }


        // Managed runtime

        /// <summary>
        /// Ensures that the ASLM-managed Ollama runtime is reachable on the standard local Ollama port.
        /// </summary>
        private async Task<ManagedRuntimeState> EnsureManagedRuntimeAsync(CancellationToken ct)
        {
            var executablePath = ResolveExecutable();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return ManagedRuntimeState.Unavailable;
            }

            var port = ResolveManagedPort();
            if (await IsRuntimeAvailableAsync(port, ct))
            {
                return new ManagedRuntimeState(true, executablePath, port);
            }

            lock (_runtimeSync)
            {
                if (_managedRuntimeProcess is { HasExited: true })
                {
                    _managedRuntimeProcess.Dispose();
                    _managedRuntimeProcess = null;
                }
            }

            var workingDirectory = Path.GetDirectoryName(executablePath) ?? GetRootDirectory();
            Directory.CreateDirectory(GetManagedModelsDirectory());

            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "serve",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
            psi.Environment["OLLAMA_MODELS"] = GetManagedModelsDirectory();

            lock (_runtimeSync)
            {
                if (_managedRuntimeProcess is null)
                {
                    var runtimeProcess = new Process { StartInfo = psi };
                    runtimeProcess.OutputDataReceived += (_, args) =>
                    {
                        if (!string.IsNullOrWhiteSpace(args.Data))
                        {
                            _logger.LogTrace("Managed Ollama stdout: {Line}", args.Data);
                        }
                    };
                    runtimeProcess.ErrorDataReceived += (_, args) =>
                    {
                        if (!string.IsNullOrWhiteSpace(args.Data))
                        {
                            _logger.LogTrace("Managed Ollama stderr: {Line}", args.Data);
                        }
                    };

                    _managedRuntimeProcess = runtimeProcess;
                    if (!_managedRuntimeProcess.Start())
                    {
                        _managedRuntimeProcess.Dispose();
                        _managedRuntimeProcess = null;
                        return ManagedRuntimeState.Unavailable;
                    }

                    _managedRuntimeProcess.BeginOutputReadLine();
                    _managedRuntimeProcess.BeginErrorReadLine();
                }
            }

            if (!await WaitForRuntimeAsync(port, ct))
            {
                StopManagedRuntime();
                return ManagedRuntimeState.Unavailable;
            }

            return new ManagedRuntimeState(true, executablePath, port);
        }

        /// <summary>
        /// Queries the managed Ollama API for the current authenticated account state.
        /// </summary>
        private async Task<bool?> TryRefreshSignInStateAsync(int port, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUri(port, "api/me"))
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadAsStringAsync(ct);
                    _hasVerifiedSignInState = true;
                    _isSignedIn = true;
                    _accountDetails = ParseAccountDetails(payload);
                    return true;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _hasVerifiedSignInState = true;
                    _isSignedIn = false;
                    ClearAccountDetails();
                    return false;
                }

                _logger.LogDebug("Unexpected status code {StatusCode} returned by managed Ollama /api/me.", response.StatusCode);
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to query the managed Ollama sign-in state from /api/me.");
                return null;
            }
        }


        // Account commands

        /// <summary>
        /// Runs one Ollama account command and captures its output for the settings UI.
        /// </summary>
        private async Task<OllamaAccountActionResult> RunAccountCommandAsync(
            string command,
            string successFallbackMessage,
            ManagedRuntimeState runtime,
            CancellationToken ct)
        {
            try
            {
                var output = new StringBuilder();
                var error = new StringBuilder();

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = runtime.ExecutablePath,
                    Arguments = command,
                    WorkingDirectory = GetRootDirectory(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                processStartInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{runtime.Port}";
                processStartInfo.Environment["OLLAMA_MODELS"] = GetManagedModelsDirectory();

                using var process = new Process { StartInfo = processStartInfo };
                process.Start();

                var outputTask = ReadStreamAsync(process.StandardOutput, output, ct);
                var errorTask = ReadStreamAsync(process.StandardError, error, ct);

                await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(ct));

                var combinedMessage = BuildCommandMessage(output, error);
                if (process.ExitCode != 0)
                {
                    return new OllamaAccountActionResult
                    {
                        Success = false,
                        Message = string.IsNullOrWhiteSpace(combinedMessage)
                            ? $"`ollama {command}` failed with exit code {process.ExitCode}."
                            : combinedMessage
                    };
                }

                return new OllamaAccountActionResult
                {
                    Success = true,
                    Message = string.IsNullOrWhiteSpace(combinedMessage) ? successFallbackMessage : combinedMessage
                };
            }
            catch (OperationCanceledException)
            {
                return new OllamaAccountActionResult
                {
                    Success = false,
                    Message = $"`ollama {command}` was cancelled."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run `ollama {Command}` against the managed runtime.", command);
                return new OllamaAccountActionResult
                {
                    Success = false,
                    Message = $"Failed to run `ollama {command}`: {ex.Message}"
                };
            }
        }


        // Resolution and paths

        /// <summary>
        /// Resolves the local port used by the managed Ollama runtime.
        /// </summary>
        private static int ResolveManagedPort() => ManagedOllamaPort;

        /// <summary>
        /// Resolves the ASLM-managed Ollama executable path.
        /// </summary>
        private string? ResolveExecutable()
        {
            var executablePath = _engineInstaller.GetEngineExecutablePath("ollama-service");
            return !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
                ? executablePath
                : null;
        }

        /// <summary>
        /// Returns the managed models directory shared by ASLM Ollama integrations.
        /// </summary>
        private static string GetManagedModelsDirectory() =>
            Path.Combine(GetRootDirectory(), "Models", "ollama-service");


        // Runtime probes

        /// <summary>
        /// Returns whether the managed Ollama HTTP endpoint is currently reachable.
        /// </summary>
        private async Task<bool> IsRuntimeAvailableAsync(int port, CancellationToken ct)
        {
            try
            {
                using var response = await _httpClient.GetAsync(BuildApiUri(port, "api/version"), ct);
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Waits until the managed Ollama HTTP endpoint becomes available.
        /// </summary>
        private async Task<bool> WaitForRuntimeAsync(int port, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsRuntimeAvailableAsync(port, ct))
                {
                    return true;
                }

                await Task.Delay(500, ct);
            }

            return false;
        }


        // API response parsing

        /// <summary>
        /// Parses all account fields exposed by Ollama's current-user response.
        /// </summary>
        private static OllamaPersistentSettings ParseAccountDetails(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new OllamaPersistentSettings();
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var name = ReadStringProperty(root, "name", "username", "user");
                var email = ReadStringProperty(root, "email");
                return new OllamaPersistentSettings
                {
                    UserName = string.IsNullOrWhiteSpace(name) ? email : name,
                    UserId = ReadStringProperty(root, "id"),
                    Email = email,
                    Bio = ReadStringProperty(root, "bio"),
                    AvatarUrl = ReadStringProperty(root, "avatarurl", "avatar_url"),
                    FirstName = ReadStringProperty(root, "firstname", "first_name"),
                    LastName = ReadStringProperty(root, "lastname", "last_name"),
                    Plan = ReadStringProperty(root, "plan")
                };
            }
            catch
            {
                return new OllamaPersistentSettings();
            }
        }

        /// <summary>
        /// Reads the first non-empty string among compatible Ollama response field names.
        /// </summary>
        private static string ReadStringProperty(JsonElement element, params string[] propertyNames)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return string.Empty;
        }


        // Command results

        /// <summary>
        /// Returns the user-facing result used when the managed Ollama executable is unavailable.
        /// </summary>
        private static OllamaAccountActionResult CreateCliUnavailableResult()
        {
            return new OllamaAccountActionResult
            {
                Success = false,
                Message = "ASLM-managed Ollama is not installed. Install the internal Ollama engine first."
            };
        }


        // Command output

        /// <summary>
        /// Reads one redirected process stream into a shared string builder.
        /// </summary>
        private static async Task ReadStreamAsync(StreamReader reader, StringBuilder buffer, CancellationToken ct)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (buffer.Length > 0)
                {
                    buffer.AppendLine();
                }

                buffer.Append(line);
            }
        }

        /// <summary>
        /// Joins the captured stdout and stderr into a single user-facing message.
        /// </summary>
        private static string BuildCommandMessage(StringBuilder output, StringBuilder error)
        {
            var stdout = output.ToString().Trim();
            var stderr = error.ToString().Trim();

            return string.IsNullOrWhiteSpace(stdout)
                ? stderr
                : string.IsNullOrWhiteSpace(stderr)
                    ? stdout
                    : $"{stdout}{Environment.NewLine}{stderr}";
        }


        // URI and paths

        /// <summary>
        /// Builds one local managed Ollama API URI.
        /// </summary>
        private static Uri BuildApiUri(int port, string relativePath)
        {
            var builder = new UriBuilder("http", "127.0.0.1", port)
            {
                Path = relativePath.TrimStart('/'),
                Query = string.Empty
            };

            return builder.Uri;
        }

        /// <summary>
        /// Returns the application root directory above the deployed app folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Types

        /// <summary>
        /// Describes the currently resolved managed runtime endpoint.
        /// </summary>
        private readonly record struct ManagedRuntimeState(bool IsAvailable, string ExecutablePath, int Port)
        {
            public static ManagedRuntimeState Unavailable => new(false, string.Empty, 0);
        }
    }
}
