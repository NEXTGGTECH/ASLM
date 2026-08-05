// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class NotificationCenterTests
{
    [Theory]
    [InlineData("Module", "ASLM-Chat", "module:aslm-chat")]
    [InlineData(" Engine ", " Ollama ", "engine:ollama")]
    public void BuildOperationKey_normalizes_source_parts(string kind, string id, string expected)
    {
        NotificationCenter.BuildOperationKey(kind, id).Should().Be(expected);
    }
}
