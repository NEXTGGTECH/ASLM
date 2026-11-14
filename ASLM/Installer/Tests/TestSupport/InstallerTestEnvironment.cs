// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Win32;

namespace ASLM.Installer.Tests.TestSupport
{
    // Temp directory redirection

    /// <summary>
    /// Creates an isolated temp directory and redirects TMP/TEMP so staging paths and bootstrapper
    /// extraction directories land inside it, where tests can assert cleanup.
    /// </summary>
    internal sealed class TempScope : IDisposable
    {
        // State

        /// <summary>
        /// Value of the TMP environment variable before the scope redirected it.
        /// </summary>
        private readonly string? _originalTmp;

        /// <summary>
        /// Value of the TEMP environment variable before the scope redirected it.
        /// </summary>
        private readonly string? _originalTemp;

        /// <summary>
        /// Absolute path of the scope's unique temp directory, which TMP and TEMP point to while the scope lives.
        /// </summary>
        public string Dir { get; }


        // Lifecycle

        /// <summary>
        /// Creates the unique temp directory and redirects TMP and TEMP to it.
        /// </summary>
        public TempScope()
        {
            Dir = Path.Combine(Path.GetTempPath(), "aslm-installer-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            _originalTmp = Environment.GetEnvironmentVariable("TMP");
            _originalTemp = Environment.GetEnvironmentVariable("TEMP");
            Environment.SetEnvironmentVariable("TMP", Dir);
            Environment.SetEnvironmentVariable("TEMP", Dir);
        }

        /// <summary>
        /// Restores TMP and TEMP and deletes the temp directory with everything the test left in it.
        /// </summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TMP", _originalTmp);
            Environment.SetEnvironmentVariable("TEMP", _originalTemp);
            TryDelete(Dir);
        }


        // Redirected locations

        /// <summary>
        /// Returns the redirected staging root that <c>InstallerService.CreateStagingPath</c> uses.
        /// </summary>
        public string StagingRoot => Path.Combine(Dir, "ASLM", "InstallStaging");

        /// <summary>
        /// Returns the redirected bootstrapper extraction root under the redirected temp path.
        /// </summary>
        public string BootstrapperRoot => Path.Combine(Dir, "ASLM", "Installer");


        // Cleanup

        /// <summary>
        /// Deletes a directory tree without failing the test when files are still locked.
        /// </summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }


    // Environment variable override

    /// <summary>
    /// Temporarily replaces one process environment variable and restores its original value.
    /// </summary>
    internal sealed class EnvironmentScope : IDisposable
    {
        /// <summary>
        /// Name of the environment variable that this scope overrides.
        /// </summary>
        private readonly string _name;

        /// <summary>
        /// Value the variable had before the scope; null when it was not set.
        /// </summary>
        private readonly string? _original;

        /// <summary>
        /// Remembers the current value of the variable and replaces it.
        /// </summary>
        /// <param name="name">Name of the environment variable to override.</param>
        /// <param name="value">Temporary value; null removes the variable while the scope lives.</param>
        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>
        /// Restores the original value of the variable.
        /// </summary>
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _original);
        }
    }


    // Registry key override

    /// <summary>
    /// Creates a unique registry key below HKEY_CURRENT_USER that stands in for a machine-wide key, so tests never
    /// need administrator rights or touch the real list of installed applications.
    /// </summary>
    internal sealed class RegistryScope : IDisposable
    {
        /// <summary>
        /// Key below HKEY_CURRENT_USER that holds the keys of all test scopes.
        /// </summary>
        private const string TestsKeyPath = @"Software\ASLM-Installer-Tests";

        /// <summary>
        /// Path of this scope's key below HKEY_CURRENT_USER.
        /// </summary>
        public string KeyPath { get; } = $@"{TestsKeyPath}\{Guid.NewGuid():N}";

        /// <summary>
        /// Creates the scope's key.
        /// </summary>
        public RegistryScope()
        {
            Registry.CurrentUser.CreateSubKey(KeyPath).Dispose();
        }

        /// <summary>
        /// Opens the scope's key for writing; the caller disposes it.
        /// </summary>
        public RegistryKey? OpenRoot()
        {
            return Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        }

        /// <summary>
        /// Deletes the scope's key and the shared tests key once no other scope uses it.
        /// </summary>
        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

            bool isUnused;
            using (var testsKey = Registry.CurrentUser.OpenSubKey(TestsKeyPath))
            {
                isUnused = testsKey is { SubKeyCount: 0, ValueCount: 0 };
            }

            if (isUnused)
            {
                Registry.CurrentUser.DeleteSubKey(TestsKeyPath, throwOnMissingSubKey: false);
            }
        }
    }
}
