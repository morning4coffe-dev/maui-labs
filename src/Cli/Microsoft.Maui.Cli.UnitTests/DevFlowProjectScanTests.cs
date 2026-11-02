using Microsoft.Maui.Cli.DevFlow;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

public class DevFlowProjectScanTests
{
    [Fact]
    public void DiscoveryHints_FindAppButSkipBuildOutputsAndLinkCycles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(root, "App"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "Microsoft.Maui.DevFlow.Agent");
        File.WriteAllText(Path.Combine(root, "obj", "Ignored.csproj"), "Microsoft.Maui.DevFlow.Agent");
        try
        {
            if (!OperatingSystem.IsWindows()) Directory.CreateSymbolicLink(Path.Combine(root, "loop"), root);
            Assert.Equal([Path.Combine("App", "App.csproj")], AgentManagementOperations.ScanForProjects(root));
            Assert.Empty(AgentManagementOperations.ScanForProjects(root, maxDirectories: 1));
            Assert.Empty(AgentManagementOperations.ScanForProjects(root, cancellationToken: new CancellationToken(true)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
