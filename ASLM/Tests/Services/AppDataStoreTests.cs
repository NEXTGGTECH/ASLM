// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class AppDataStoreTests
{
    [Fact]
    public async Task LoadAsync_creates_defaults_when_file_missing()
    {
        var layout = new AslmFileSystemLayout();
        layout.ResetDataAppDirectory();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());

        await store.LoadAsync();

        store.IsFirstRun.Should().BeTrue();
        File.Exists(layout.AppDataFilePath).Should().BeTrue("LoadAsync persists defaults when the file is missing");
    }

    [Fact]
    public async Task SaveAsync_and_LoadAsync_round_trip()
    {
        var layout = new AslmFileSystemLayout();
        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await store.LoadAsync();

        store.Data.FirstRunCompleted = true;
        store.Data.User.Name = "RoundTrip";
        await store.SaveAsync();

        var reloaded = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await reloaded.LoadAsync();

        reloaded.IsFirstRun.Should().BeFalse();
        reloaded.Data.User.Name.Should().Be("RoundTrip");
        File.Exists(layout.AppDataFilePath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_recreates_defaults_on_invalid_json()
    {
        var layout = new AslmFileSystemLayout();
        layout.WriteAppDataJson("{ not valid json");

        var store = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        await store.LoadAsync();

        store.Data.User.Name.Should().BeEmpty();
        store.IsFirstRun.Should().BeTrue();
    }
}
