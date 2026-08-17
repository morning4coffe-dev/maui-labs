using Microsoft.Maui.Cli.DevFlow.Execution;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// The opaque agent session identity is compiled into the app binary by
/// <c>Microsoft.Maui.DevFlow.Agent.targets</c>. These tests pin that it is derived from the build
/// inputs rather than regenerated per invocation, so two <c>flow run</c>s of the same flow against
/// the same commit build the same app assembly.
/// </summary>
public sealed class FlowAgentSessionIdentityTests
{
    private static FlowExecutionRequest Request(
        string projectPath,
        string? targetFramework = "net10.0-android",
        string configuration = "Debug",
        string platform = "android")
        => new()
        {
            FlowPath = "flow.md",
            ProjectPath = projectPath,
            TargetFramework = targetFramework,
            Configuration = configuration,
            Platform = platform,
        };

    [Fact]
    public void CreateBuildScopedAgentSessionId_SameBuildInputs_IsStableAcrossInvocations()
    {
        var request = Request(Path.Combine("C:", "repo", "app", "App.csproj"));

        var first = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(request);
        var second = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(request);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateBuildScopedAgentSessionId_SatisfiesAgentSessionIdentityValidation()
    {
        var sessionId = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(
            Request(Path.Combine("C:", "repo", "app", "App.csproj")));

        Assert.StartsWith("flow", sessionId, StringComparison.Ordinal);
        Assert.Equal(36, sessionId.Length);
        Assert.All(sessionId, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
    }

    [Fact]
    public void CreateBuildScopedAgentSessionId_DifferentProjectPath_Differs()
    {
        var first = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(
            Request(Path.Combine("C:", "worktree-a", "App.csproj")));
        var second = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(
            Request(Path.Combine("C:", "worktree-b", "App.csproj")));

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("net10.0-ios", "Debug", "android")]
    [InlineData("net10.0-android", "Release", "android")]
    [InlineData("net10.0-android", "Debug", "ios")]
    public void CreateBuildScopedAgentSessionId_DifferentArtifactSelector_Differs(
        string targetFramework,
        string configuration,
        string platform)
    {
        var baseline = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(
            Request(Path.Combine("C:", "repo", "App.csproj")));
        var variant = FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(
            Request(Path.Combine("C:", "repo", "App.csproj"), targetFramework, configuration, platform));

        Assert.NotEqual(baseline, variant);
    }

    [Fact]
    public void CreateBuildScopedAgentSessionId_EquivalentProjectPathSpelling_Matches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "devflow-session-id");
        var direct = Path.Combine(directory, "App.csproj");
        var indirect = Path.Combine(directory, "nested", "..", "App.csproj");

        Assert.Equal(
            FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(Request(direct)),
            FlowExecutionCoordinator.CreateBuildScopedAgentSessionId(Request(indirect)));
    }
}
