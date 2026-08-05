// Copyright NEXTGGTECH. Apache License 2.0.

using System.Reflection;

namespace ASLM.Tests.Services;

public sealed class EngineInstallerStepContextTests
{
    [Fact]
    public void StepContext_resolve_variables_and_paths_within_allowed_roots()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ASLM.Tests", "base");
        var tempDir = Path.Combine(Path.GetTempPath(), "ASLM.Tests", "temp");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var context = CreateStepContext(baseDir, tempDir);

            Invoke<string>(context, "ResolveVariables", "copy {temp}/file.txt")
                .Should()
                .Be($"copy {tempDir.TrimEnd(Path.DirectorySeparatorChar)}/file.txt");

            var resolved = Invoke<string>(context, "ResolvePath", "subdir/file.bin");
            resolved.Should().StartWith(baseDir);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StepContext_resolve_path_rejects_traversal_outside_roots()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ASLM.Tests", "secure-base");
        var tempDir = Path.Combine(Path.GetTempPath(), "ASLM.Tests", "secure-temp");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(tempDir);

        try
        {
            var context = CreateStepContext(baseDir, tempDir);

            var act = () => Invoke<string>(context, "ResolvePath", "..\\outside.bin");

            act.Should().Throw<TargetInvocationException>()
                .WithInnerException<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static object CreateStepContext(string baseDir, string tempDir)
    {
        var stepContextType = typeof(EngineInstaller).GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name == "StepContext");

        var constructor = stepContextType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.GetParameters().Length == 2);

        return constructor.Invoke([baseDir, tempDir])!;
    }

    private static T Invoke<T>(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull();
        return (T)method!.Invoke(instance, args)!;
    }
}
