using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Maui.Cli.DevFlow;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Execution;
using Microsoft.Maui.Cli.Models;
using Microsoft.Maui.Cli.Providers.Android;
using Microsoft.Maui.Cli.UnitTests.Fakes;
using Microsoft.Maui.Cli.UnitTests.Fixtures;
using Microsoft.Maui.Cli.Utils;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

[Collection("CLI")]
public sealed class FlowExecutionCoreTests
{
    [Fact]
    public void ArtifactResolver_MultipleExactArtifacts_RejectsAmbiguity()
    {
        var candidates = new[]
        {
            Artifact("app-one.apk"),
            Artifact("app-two.apk"),
        };

        var exception = Assert.Throws<FlowExecutionException>(() =>
            MsBuildAppArtifactResolver.SelectSingleArtifact(
                candidates,
                candidates[0].ProjectPath,
                candidates[0].TargetFramework,
                candidates[0].Configuration));

        Assert.Equal(FlowExecutionExitCategories.InvalidConfiguration, exception.ExitCategory);
        Assert.Equal("artifact-ambiguous", exception.Code);
    }

    [Fact]
    public void ArtifactResolver_CandidateTypeAllowlist_DoesNotFallBackToForeignArtifact()
    {
        var candidate = Artifact("app.dll") with { ArtifactType = "dll" };

        var exception = Assert.Throws<FlowExecutionException>(() =>
            MsBuildAppArtifactResolver.SelectSingleArtifact(
                [candidate],
                candidate.ProjectPath,
                candidate.TargetFramework,
                candidate.Configuration,
                candidateArtifactTypes: ["apk"]));

        Assert.Equal("artifact-not-found", exception.Code);
    }

    [Fact]
    public void ArtifactResolver_RequestedRuntimeIdentifier_MustMatchExactly()
    {
        var candidate = Artifact("app.apk") with { RuntimeIdentifier = "android-arm64" };

        var exception = Assert.Throws<FlowExecutionException>(() =>
            MsBuildAppArtifactResolver.SelectSingleArtifact(
                [candidate],
                candidate.ProjectPath,
                candidate.TargetFramework,
                candidate.Configuration,
                runtimeIdentifier: "android-x64",
                candidateArtifactTypes: ["apk"]));

        Assert.Equal("artifact-not-found", exception.Code);
    }

    [Fact]
    public async Task ArtifactResolver_UsesAppProjectReferenceDescriptiveMetadata()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "PlainApp.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "Program.cs"),
            "public static class PlainAppMarker { public static int Value => 1; }");
        var resolver = new MsBuildAppArtifactResolver(new ExecutionProcessRunner());

        var artifact = await resolver.ResolveAsync(new AppArtifactResolutionRequest
        {
            ProjectPath = project,
            AgentSessionId = "flowsession",
            TargetFramework = "net10.0",
            Configuration = "Debug",
            WorkDirectory = workspace.Output,
        });

        Assert.Equal("dll", artifact.ArtifactType);
        Assert.Equal("supporting", artifact.ArtifactRole);
        Assert.Equal("unknown", artifact.TargetRuntimeKind);
        Assert.False(artifact.Installable);
        Assert.False(artifact.Launchable);
        Assert.StartsWith("sha256:", artifact.PackageDigest);
    }

    [Fact]
    public async Task ArtifactResolver_AppleBundleDirectory_PreservesSimulatorRuntimeAndHashesBundle()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "SimulatorApp.csproj");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var runner = new CallbackProcessRunner(async call =>
        {
            var hostProject = call.Arguments[1];
            var host = XDocument.Load(hostProject);
            Assert.Equal(
                "iossimulator-arm64",
                host.Descendants("RuntimeIdentifier").Single().Value);
            Assert.Contains(
                "MauiDevFlowSessionId=flowsession",
                host.Descendants("Properties").Single().Value,
                StringComparison.Ordinal);
            var outputRoot = host.Descendants("OutputRoot").Single().Value;
            var metadataPath = host.Descendants("WriteLinesToFile")
                .Single()
                .Attribute("File")!
                .Value;
            var bundlePath = Path.Combine(outputRoot, "app", "SimulatorApp.app");
            Directory.CreateDirectory(bundlePath);
            await File.WriteAllTextAsync(Path.Combine(bundlePath, "Info.plist"), "plist");
            await File.WriteAllBytesAsync(Path.Combine(bundlePath, "SimulatorApp"), [1, 2, 3, 4]);
            await File.WriteAllTextAsync(
                metadataPath,
                string.Join(
                    "|||DEVFLOW_ARTIFACT|||",
                    bundlePath,
                    "SimulatorApp",
                    project,
                    "net10.0-ios",
                    "ios",
                    "iossimulator-arm64",
                    "Debug",
                    "com.example.app",
                    "app",
                    "1",
                    "deployable",
                    "ios-simulator",
                    "simulator-bundle",
                    "apple-bundle-id",
                    "com.example.app",
                    "true",
                    "true"));
            return Success();
        });
        var resolver = new MsBuildAppArtifactResolver(runner);

        var artifact = await resolver.ResolveAsync(new AppArtifactResolutionRequest
        {
            ProjectPath = project,
            AgentSessionId = "flowsession",
            TargetFramework = "net10.0-ios",
            Configuration = "Debug",
            WorkDirectory = workspace.Output,
            Platform = "ios",
            TargetFrameworkPlatformIdentifiers = ["ios"],
            CandidateArtifactTypes = ["app", "ipa"],
            RuntimeIdentifier = "iossimulator-arm64",
        });

        Assert.Equal("ios-simulator", artifact.TargetRuntimeKind);
        Assert.Equal("iossimulator-arm64", artifact.RuntimeIdentifier);
        Assert.StartsWith("sha256:", artifact.PackageDigest);
        Assert.True(Directory.Exists(artifact.Path));
    }

    [Fact]
    public async Task ArtifactResolver_WindowsUnpackagedExecutable_ResolvesExactLauncher()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "WindowsApp.csproj");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
                <AssemblyName>ImportedExactWindowsApp</AssemblyName>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "Directory.Build.targets"),
            "<Project />");
        await File.WriteAllTextAsync(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ApplicationId>com.example.windowsapp</ApplicationId>
                <WindowsPackageType>None</WindowsPackageType>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "Program.cs"),
            "System.Console.WriteLine(\"Exact Windows app\");");
        var resolver = new MsBuildAppArtifactResolver(new ExecutionProcessRunner());

        var artifact = await resolver.ResolveAsync(new AppArtifactResolutionRequest
        {
            ProjectPath = project,
            AgentSessionId = "flowsession",
            TargetFramework = "net10.0-windows10.0.19041.0",
            Configuration = "Debug",
            WorkDirectory = workspace.Output,
            Platform = "windows",
            TargetFrameworkPlatformIdentifiers = ["windows"],
            CandidateArtifactTypes = ["exe", "msix", "appinstaller"],
        });

        Assert.Equal("exe", artifact.ArtifactType);
        Assert.Equal("windows", artifact.TargetRuntimeKind);
        Assert.Equal("file-path", artifact.LaunchIdentityKind);
        Assert.Equal("ImportedExactWindowsApp.exe", Path.GetFileName(artifact.Path));
        Assert.Equal(Path.GetFullPath(artifact.Path), Path.GetFullPath(artifact.LaunchIdentity!));
        Assert.True(File.Exists(artifact.Path));
    }

    [Fact]
    public async Task ArtifactResolver_ArtifactOutsideOwnedBuildRoot_IsRejected()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-android</TargetFramework></PropertyGroup></Project>");
        var runner = new CallbackProcessRunner(async call =>
        {
            var host = XDocument.Load(call.Arguments[1]);
            var metadataPath = host.Descendants("WriteLinesToFile").Single().Attribute("File")!.Value;
            var outside = Path.Combine(workspace.Root, "outside.apk");
            await File.WriteAllBytesAsync(outside, [1, 2, 3]);
            await File.WriteAllTextAsync(
                metadataPath,
                ArtifactMetadata(outside, project, runtimeIdentifier: ""));
            return Success();
        });
        var resolver = new MsBuildAppArtifactResolver(runner);

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            resolver.ResolveAsync(new AppArtifactResolutionRequest
            {
                ProjectPath = project,
                AgentSessionId = "flowsession",
                TargetFramework = "net10.0-android",
                Configuration = "Debug",
                WorkDirectory = workspace.Output,
                Platform = "android",
                TargetFrameworkPlatformIdentifiers = ["android"],
                CandidateArtifactTypes = ["apk"],
            }));

        Assert.Equal("artifact-path-outside-build-root", failure.Code);
    }

    [Fact]
    public async Task ArtifactResolver_OversizedArtifact_IsRejectedBeforeUse()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-android</TargetFramework></PropertyGroup></Project>");
        var runner = new CallbackProcessRunner(async call =>
        {
            var host = XDocument.Load(call.Arguments[1]);
            var outputRoot = host.Descendants("OutputRoot").Single().Value;
            var metadataPath = host.Descendants("WriteLinesToFile").Single().Attribute("File")!.Value;
            var artifactPath = Path.Combine(outputRoot, "package", "App.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            await File.WriteAllBytesAsync(artifactPath, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(
                metadataPath,
                ArtifactMetadata(artifactPath, project, runtimeIdentifier: ""));
            return Success();
        });
        var resolver = new MsBuildAppArtifactResolver(
            runner,
            new AppArtifactInspectionLimits
            {
                MaximumFileBytes = 3,
                MaximumTotalBytes = 8,
                MaximumFiles = 8,
                MaximumDepth = 8,
                Timeout = TimeSpan.FromSeconds(5),
            });

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            resolver.ResolveAsync(new AppArtifactResolutionRequest
            {
                ProjectPath = project,
                AgentSessionId = "flowsession",
                TargetFramework = "net10.0-android",
                Configuration = "Debug",
                WorkDirectory = workspace.Output,
                Platform = "android",
                TargetFrameworkPlatformIdentifiers = ["android"],
                CandidateArtifactTypes = ["apk"],
            }));

        Assert.Equal("artifact-file-size-exceeded", failure.Code);
    }

    [Fact]
    public async Task ArtifactResolver_BuildFailureDiagnostics_AreBounded()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-android</TargetFramework></PropertyGroup></Project>");
        var resolver = new MsBuildAppArtifactResolver(
            new QueueProcessRunner(new ProcessResult
            {
                ExitCode = 1,
                StandardError = new string('x', 16_000) + "diagnostic-tail",
            }));

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            resolver.ResolveAsync(new AppArtifactResolutionRequest
            {
                ProjectPath = project,
                AgentSessionId = "flowsession",
                TargetFramework = "net10.0-android",
                Configuration = "Debug",
                WorkDirectory = workspace.Output,
                Platform = "android",
                TargetFrameworkPlatformIdentifiers = ["android"],
                CandidateArtifactTypes = ["apk"],
            }));

        Assert.Equal("app-build-failed", failure.Code);
        Assert.Contains("diagnostic-tail", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length < 2_200, failure.Message.Length.ToString());
    }

    [Fact]
    public void ArtifactPath_ReparsePointInsideOwnedRoot_IsRejected()
    {
        using var workspace = new ExecutionTestWorkspace();
        var ownedRoot = Path.Combine(workspace.Root, "owned-build");
        var outside = Path.Combine(workspace.Root, "outside.apk");
        var link = Path.Combine(ownedRoot, "App.apk");
        Directory.CreateDirectory(ownedRoot);
        File.WriteAllBytes(outside, [1, 2, 3]);
        if (!TryCreateFileSymbolicLink(link, outside))
            return;

        var failure = Assert.Throws<FlowExecutionException>(() =>
            ExecutionPathSafety.ValidateConfinedArtifactPath(ownedRoot, link));

        Assert.Equal("artifact-reparse-point", failure.Code);
    }

    [Fact]
    public async Task Coordinator_AabWithLegacyInstallableTrue_IsUnsupportedBeforePlatformMutation()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var adapter = new FakePlatformAdapter(validateWithAndroidRules: true);
        var coordinator = CreateCoordinator(
            new FakeArtifactResolver(Artifact(Path.Combine(workspace.Root, "app.aab")) with
            {
                ArtifactType = "aab",
                ArtifactRole = "distribution",
                DeploymentModel = "store-bundle",
                Installable = true,
                Launchable = false,
            }),
            adapter);

        var result = await coordinator.RunAsync(Request(bundle, workspace.Output));

        Assert.Equal(FlowExecutionExitCategories.Unsupported, result.ExitCategory);
        Assert.Equal(0, adapter.MutationCalls);
        Assert.Equal("android-aab-unsupported", result.Report?.Failure?.Code);
    }

    [Fact]
    public async Task Coordinator_ResettablePlanWithoutProvider_RefusesBeforeMutation()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.TestTenantResettable);
        var adapter = new FakePlatformAdapter();
        var coordinator = CreateCoordinator(
            new FakeArtifactResolver(Artifact(Path.Combine(workspace.Root, "app.apk"))),
            adapter);

        var result = await coordinator.RunAsync(Request(bundle, workspace.Output));

        Assert.Equal(FlowExecutionExitCategories.Unsupported, result.ExitCategory);
        Assert.Equal(0, adapter.MutationCalls);
        Assert.Equal("state-evidence-provider-missing", result.Report?.Failure?.Code);
    }

    [Fact]
    public async Task Coordinator_InvalidConfiguration_StillWritesCategorizedOutputs()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var coordinator = CreateCoordinator(
            new FakeArtifactResolver(Artifact(Path.Combine(workspace.Root, "app.apk"))),
            new FakePlatformAdapter());
        var request = Request(bundle, workspace.Output) with { ProjectPath = "" };

        var result = await coordinator.RunAsync(request);

        Assert.Equal(FlowExecutionExitCategories.InvalidConfiguration, result.ExitCategory);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.True(File.Exists(result.ReportPath));
        Assert.True(File.Exists(result.JUnitPath));
        Assert.Equal("project-path-missing", result.Report?.Failure?.Code);
        var manifestJson = await File.ReadAllTextAsync(result.ManifestPath!);
        ExecutionManifestSchemaValidator.AssertValid(manifestJson);
        using var manifest = JsonDocument.Parse(manifestJson);
        Assert.False(manifest.RootElement.TryGetProperty("build", out _));
        Assert.False(manifest.RootElement.TryGetProperty("device", out _));
    }

    [Fact]
    public async Task Coordinator_NonDebugConfiguration_IsRejectedBeforeArtifactBuild()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var resolver = new FakeArtifactResolver(
            Artifact(Path.Combine(workspace.Root, "app.apk")));
        var coordinator = CreateCoordinator(resolver, new FakePlatformAdapter());

        var result = await coordinator.RunAsync(
            Request(bundle, workspace.Output) with { Configuration = "Release" });

        Assert.Equal(FlowExecutionExitCategories.Unsupported, result.ExitCategory);
        Assert.Equal("devflow-agent-debug-configuration-required", result.Report?.Failure?.Code);
        Assert.Equal("validate-request", result.Report?.Failure?.Phase);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task Coordinator_ArtifactBuildFailure_ReportsActualLifecyclePhase()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var coordinator = CreateCoordinator(
            new ThrowingArtifactResolver(
                FlowExecutionException.Infrastructure(
                    "app-build-failed",
                    "bounded build failure")),
            new FakePlatformAdapter());

        var result = await coordinator.RunAsync(Request(bundle, workspace.Output));

        Assert.Equal(FlowExecutionExitCategories.InfrastructureFailure, result.ExitCategory);
        Assert.Equal("app-build-failed", result.Report?.Failure?.Code);
        Assert.Equal("resolve-artifact", result.Report?.Failure?.Phase);
    }

    [Fact]
    public void ExecutionOutput_RejectsReparsePointAncestor()
    {
        using var workspace = new ExecutionTestWorkspace();
        var target = Path.Combine(workspace.Root, "output-target");
        var link = Path.Combine(workspace.Root, "output-link");
        Directory.CreateDirectory(target);
        if (!TryCreateDirectorySymbolicLink(link, target))
            return;

        var failure = Assert.Throws<FlowExecutionException>(() =>
            ExecutionPathSafety.PrepareNewOrEmptyDirectory(Path.Combine(link, "run")));

        Assert.Equal("execution-output-reparse-point", failure.Code);
    }

    [Fact]
    public async Task ImmutableOutputWriter_RechecksPreparedRootBeforeWriting()
    {
        using var workspace = new ExecutionTestWorkspace();
        var output = Path.Combine(workspace.Root, "prepared-output");
        var moved = Path.Combine(workspace.Root, "moved-output");
        Directory.CreateDirectory(output);
        Directory.Move(output, moved);
        if (!TryCreateDirectorySymbolicLink(output, moved))
        {
            Directory.Move(moved, output);
            return;
        }

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            new ImmutableExecutionOutputWriter().WriteAsync(
                output,
                [new ExecutionOutputFile("result.json", [1, 2, 3])]));

        Assert.Equal("execution-output-reparse-point", failure.Code);
        Assert.False(File.Exists(Path.Combine(moved, "result.json")));
    }

    [Fact]
    public async Task Coordinator_EndToEndFakeAndroidRun_UsesCanonicalRunnerAndIsUnverifiedWithoutOracle()
    {
        const string status = """
            {
              "agent": { "name": "Microsoft.Maui.DevFlow.Agent", "version": "test" },
              "device": { "platform": "Android", "deviceType": "Virtual", "idiom": "Phone" },
              "app": { "name": "App", "packageId": "com.example.app", "processId": 321, "build": "42" },
              "capabilities": { "ui": true, "mutations": true, "workflowCommandLedger": true },
              "route": "//checkout",
              "running": true
            }
            """;
        await using var server = new MockAgentServer(status);
        await server.StartAsync();
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var adapter = new FakePlatformAdapter(allowMutation: true);
        var listCalls = 0;
        var binding = new ExactAgentBindingResolver(_ =>
        {
            listCalls++;
            return Task.FromResult<AgentRegistration[]?>(
                listCalls == 1
                    ? []
                    :
                    [
                        Agent("new-instance") with
                        {
                            Port = server.Port,
                            ProcessId = 321,
                        },
                    ]);
        }, pollInterval: TimeSpan.Zero);
        var coordinator = new FlowExecutionCoordinator(
            new CommittedFlowBundleLoader(),
            new FakeArtifactResolver(Artifact(Path.Combine(workspace.Root, "app.apk"))),
            [adapter],
            new FlowStateEvidenceProviderRegistry([]),
            binding,
            new FlowRunReportWriter(),
            new JUnitFlowExecutionWriter(),
            new ExecutionManifestWriter(),
            new ImmutableExecutionOutputWriter(),
            () => Task.FromResult<int?>(19223),
            appSourceIdentityProvider: new FakeAppSourceIdentityProvider(),
            agentSessionIdFactory: static () => "flowsession");

        var result = await coordinator.RunAsync(Request(bundle, workspace.Output));

        Assert.Equal(FlowExecutionExitCategories.Unverified, result.ExitCategory);
        Assert.False(result.Ok);
        Assert.Equal(MauiFlowRunOutcomes.Passed, result.Report?.Outcome?.Status);
        Assert.False(result.Report?.Outcome?.Verified);
        Assert.False(result.Manifest?.Outcome?.Verified);
        Assert.Equal(2, adapter.MutationCalls);
        Assert.Equal(1, adapter.CleanupCalls);
        Assert.True(result.Manifest?.Lifecycle?.InstalledByInvocation);
        Assert.True(result.Manifest?.Lifecycle?.LaunchedByInvocation);
        Assert.True(result.Manifest?.Lifecycle?.CleanupCompleted);
        Assert.Equal("source-current", result.Report?.Target?.AppSourceFingerprint);
        Assert.StartsWith("sha256:", result.Manifest?.Build?.AppSourceFingerprint);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", result.Manifest?.Build?.SourceRevision);
        Assert.Contains(server.RecordedRequests, request => request.Path == "/api/v1/agent/status");
        var junit = XDocument.Load(result.JUnitPath!);
        Assert.Equal("1", junit.Root?.Attribute("failures")?.Value);
        Assert.Equal("0", junit.Root?.Attribute("errors")?.Value);
        Assert.Equal("0", junit.Root?.Attribute("skipped")?.Value);
        Assert.Equal(
            FlowExecutionExitCategories.Unverified,
            junit.Descendants("failure").Single().Attribute("type")?.Value);
    }

    [Fact]
    public async Task Coordinator_TestFailurePlusCleanupFailure_IsCompositeInfrastructureOutcome()
    {
        const string status = """
            {
              "agent": { "name": "Microsoft.Maui.DevFlow.Agent", "version": "test" },
              "device": { "platform": "Android", "deviceType": "Virtual", "idiom": "Phone" },
              "app": { "name": "App", "packageId": "com.example.app", "processId": 321, "build": "42" },
              "capabilities": { "ui": true, "mutations": true, "workflowCommandLedger": true },
              "route": "//checkout",
              "running": true
            }
            """;
        await using var server = new MockAgentServer(status);
        await server.StartAsync();
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(
            MauiFlowSideEffectPolicies.None,
            expectedRoute: "//different");
        var adapter = new FakePlatformAdapter(
            allowMutation: true,
            cleanupSucceeds: false);
        var listCalls = 0;
        var binding = new ExactAgentBindingResolver(_ =>
        {
            listCalls++;
            return Task.FromResult<AgentRegistration[]?>(
                listCalls == 1
                    ? []
                    :
                    [
                        Agent("new-instance") with
                        {
                            Port = server.Port,
                            ProcessId = 321,
                        },
                    ]);
        }, pollInterval: TimeSpan.Zero);
        var coordinator = new FlowExecutionCoordinator(
            new CommittedFlowBundleLoader(),
            new FakeArtifactResolver(Artifact(Path.Combine(workspace.Root, "app.apk"))),
            [adapter],
            new FlowStateEvidenceProviderRegistry([]),
            binding,
            new FlowRunReportWriter(),
            new JUnitFlowExecutionWriter(),
            new ExecutionManifestWriter(),
            new ImmutableExecutionOutputWriter(),
            () => Task.FromResult<int?>(19223),
            agentSessionIdFactory: static () => "flowsession");

        var result = await coordinator.RunAsync(Request(bundle, workspace.Output));

        Assert.Equal(FlowExecutionExitCategories.InfrastructureFailure, result.ExitCategory);
        Assert.Equal(MauiFlowRunOutcomes.InfrastructureError, result.Report?.Outcome?.Status);
        Assert.Equal("fake-cleanup-failed", result.Report?.Failure?.Code);
        Assert.Equal("cleanup", result.Report?.Failure?.Phase);
        Assert.False(result.Manifest?.Lifecycle?.CleanupCompleted);
        var primary = result.Report!.ExtensionData!["primaryExecutionOutcome"];
        Assert.Equal(FlowExecutionExitCategories.TestFailure, primary.GetProperty("exitCategory").GetString());
        Assert.Equal(MauiFlowRunOutcomes.Failed, primary.GetProperty("status").GetString());
        var junit = XDocument.Load(result.JUnitPath!);
        Assert.Equal("0", junit.Root?.Attribute("failures")?.Value);
        Assert.Equal("1", junit.Root?.Attribute("errors")?.Value);
        Assert.Equal("0", junit.Root?.Attribute("skipped")?.Value);
    }

    [Fact]
    public void ExactBinding_RejectsStaleInstanceAndAcceptsMatchingNewInstance()
    {
        var old = Agent("old-instance");
        var expectation = Expectation();

        var stale = ExactAgentBindingResolver.SelectNewMatch([old], [old], expectation);
        var fresh = ExactAgentBindingResolver.SelectNewMatch(
            [old],
            [old, Agent("new-instance")],
            expectation);

        Assert.Equal(ExactAgentBindingSelectionKind.Pending, stale.Kind);
        Assert.True(stale.MatchingStaleAgentObserved);
        Assert.Equal(ExactAgentBindingSelectionKind.Matched, fresh.Kind);
        Assert.Equal("new-instance", fresh.Agent?.InstanceId);
    }

    [Fact]
    public void ExactBinding_MultipleNewMatchingInstances_IsAmbiguous()
    {
        var selection = ExactAgentBindingResolver.SelectNewMatch(
            [],
            [Agent("new-one"), Agent("new-two")],
            Expectation());

        Assert.Equal(ExactAgentBindingSelectionKind.Ambiguous, selection.Kind);
        Assert.Null(selection.Agent);
    }

    [Fact]
    public void ExactBinding_UsesOpaqueSessionInsteadOfProjectPathRepresentation()
    {
        var relativeProject = Agent("relative-project") with
        {
            Project = "App.csproj",
        };
        var wrongSession = relativeProject with
        {
            InstanceId = "wrong-session",
            SessionId = "different-session",
            Port = 9333,
        };

        var selection = ExactAgentBindingResolver.SelectNewMatch(
            [],
            [relativeProject, wrongSession],
            Expectation());

        Assert.Equal(ExactAgentBindingSelectionKind.Matched, selection.Kind);
        Assert.Equal("relative-project", selection.Agent?.InstanceId);
    }

    [Fact]
    public async Task CommittedBundleLoader_StaleDigest_IsRejected()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var parsed = FlowMarkdown.Parse(await File.ReadAllTextAsync(bundle.Flow)).Flow!;
        parsed.Name = "changed-after-plan-commit";
        await File.WriteAllTextAsync(bundle.Flow, FlowMarkdown.Serialize(parsed));

        var exception = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            new CommittedFlowBundleLoader().LoadAsync(bundle.Flow, bundle.Plan));

        Assert.Equal("plan-flow-digest-stale", exception.Code);
    }

    [Fact]
    public async Task CommittedBundleLoader_DraftSidecar_IsRejected()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.None);
        var plan = JsonNode.Parse(await File.ReadAllTextAsync(bundle.Plan))!.AsObject();
        plan["draft"] = true;
        await File.WriteAllTextAsync(bundle.Plan, plan.ToJsonString());

        var exception = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            new CommittedFlowBundleLoader().LoadAsync(bundle.Flow, bundle.Plan));

        Assert.Equal("draft-not-executable", exception.Code);
    }

    [Fact]
    public async Task AppSourceIdentity_CleanProjectTree_UsesCommitAndRelativeProject()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var runner = new QueueProcessRunner(
            Success("0123456789abcdef0123456789abcdef01234567\n"),
            Success());
        var provider = new GitAppSourceIdentityProvider(runner);

        var identity = await provider.ResolveAsync(project);

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", identity.SourceRevision);
        Assert.StartsWith("sha256:", identity.AppSourceFingerprint);
        var status = Assert.Single(runner.Calls, call => call.Arguments.Contains("status"));
        Assert.Equal("--", status.Arguments[^2]);
        Assert.Equal(
            Path.GetRelativePath(FindRepositoryRoot(), workspace.Root)
                .Replace(Path.DirectorySeparatorChar, '/'),
            status.Arguments[^1]);
    }

    [Fact]
    public async Task AppSourceIdentity_DirtyProjectTree_OmitsSourceFingerprint()
    {
        using var workspace = new ExecutionTestWorkspace();
        var project = Path.Combine(workspace.Root, "App.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var runner = new QueueProcessRunner(
            Success("0123456789abcdef0123456789abcdef01234567\n"),
            Success(" M App.csproj\n"));
        var provider = new GitAppSourceIdentityProvider(runner);

        var identity = await provider.ResolveAsync(project);

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", identity.SourceRevision);
        Assert.Null(identity.AppSourceFingerprint);
    }

    [Fact]
    public async Task AndroidDeployment_PreexistingPackageIsRejectedBeforeOverwrite()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(
            SingleUser(),
            Success("package:/data/app/~~token/com.example.app/base.apk\n"));
        var deployment = new AndroidAppDeployment(provider, runner);

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
            {
                DeviceSerial = "emulator-5554",
                ApkPath = workspace.WriteApk(),
                PackageId = "com.example.app",
            }));

        Assert.Equal(FlowExecutionExitCategories.Unsupported, failure.ExitCategory);
        Assert.Equal("android-preexisting-app-unsafe", failure.Code);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("install"));
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("uninstall"));
        Assert.All(runner.Calls, call => Assert.Equal(["-s", "emulator-5554"], call.Arguments.Take(2)));
    }

    [Fact]
    public async Task AndroidCleanup_UninstallRequested_RemovesOnlyNewlyInstalledPackage()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(
            SingleUser(),
            Success(),
            Success("Success\n"),
            Success("com.example.app/.MainActivity\n"),
            Success(),
            Success("Status: ok\n"),
            Success("321\n"),
            Success(),
            Success("Success\n"));
        var deployment = new AndroidAppDeployment(provider, runner);

        var session = await deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
        {
            DeviceSerial = "emulator-5554",
            ApkPath = workspace.WriteApk(),
            PackageId = "com.example.app",
        });
        var cleanup = await deployment.CleanupAsync(session, FlowExecutionCleanupPolicies.Uninstall);

        Assert.True(session.InstalledByInvocation);
        Assert.True(cleanup.PackageUninstalled);
        var uninstall = Assert.Single(runner.Calls, call => call.Arguments.Contains("uninstall"));
        Assert.Equal(["-s", "emulator-5554", "uninstall", "com.example.app"], uninstall.Arguments);
    }

    [Fact]
    public async Task AndroidDeployment_PackageNotFoundExit_IsNormalFreshInstallState()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(
            SingleUser(),
            new ProcessResult
            {
                ExitCode = 1,
                StandardError = "Error: package com.example.app was not found\r\n",
            },
            Success("Performing Streamed Install\r\nSuccess\r\n"),
            Success("priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\r\ncom.example.app/.MainActivity\r\n"),
            Success(),
            Success("Status: ok\r\nLaunchState: COLD\r\n"),
            Success("321\r\n"));
        var deployment = new AndroidAppDeployment(provider, runner);

        var session = await deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
        {
            DeviceSerial = "emulator-5554",
            ApkPath = workspace.WriteApk(),
            PackageId = "com.example.app",
        });

        Assert.False(session.PackageWasInstalledBefore);
        Assert.True(session.InstalledByInvocation);
        Assert.True(session.LaunchedByInvocation);
        Assert.Equal(321, session.ProcessId);
    }

    [Fact]
    public async Task AndroidDeployment_ModernBlankExitOnePackageQuery_IsNormalAbsence()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(
            SingleUser(),
            new ProcessResult { ExitCode = 1 },
            Success("Success\n"),
            Success("com.example.app/.MainActivity\n"),
            Success(),
            Success("Status: ok\n"),
            Success("321\n"));
        var deployment = new AndroidAppDeployment(provider, runner);

        var session = await deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
        {
            DeviceSerial = "emulator-5554",
            ApkPath = workspace.WriteApk(),
            PackageId = "com.example.app",
        });

        Assert.True(session.InstalledByInvocation);
        Assert.True(session.LaunchedByInvocation);
    }

    [Fact]
    public async Task AndroidDeployment_MultiUserDevice_IsRejectedBeforePackageMutation()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(Success(
            "Users:\n\tUserInfo{0:Owner:13} running\n\tUserInfo{10:Work:30}\n"));
        var deployment = new AndroidAppDeployment(provider, runner);

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
            {
                DeviceSerial = "emulator-5554",
                ApkPath = workspace.WriteApk(),
                PackageId = "com.example.app",
            }));

        Assert.Equal(FlowExecutionExitCategories.Unsupported, failure.ExitCategory);
        Assert.Equal("android-multi-user-unsupported", failure.Code);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("install"));
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("path"));
    }

    [Fact]
    public async Task AndroidDeployment_DeviceFailureDuringPackageQuery_IsInfrastructure()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(
            SingleUser(),
            new ProcessResult
            {
                ExitCode = 1,
                StandardError = "adb: device 'emulator-5554' not found\r\n",
            });
        var deployment = new AndroidAppDeployment(provider, runner);

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
            {
                DeviceSerial = "emulator-5554",
                ApkPath = workspace.WriteApk(),
                PackageId = "com.example.app",
            }));

        Assert.Equal(FlowExecutionExitCategories.InfrastructureFailure, failure.ExitCategory);
        Assert.Equal("android-package-query-failed", failure.Code);
        Assert.DoesNotContain(runner.Calls, call => call.Arguments.Contains("install"));
    }

    [Fact]
    public async Task AndroidDeployment_CancelledInstall_ReconcilesNewPackageForOwnedCleanup()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var packageQueries = 0;
        var runner = new CallbackProcessRunner(call =>
        {
            if (call.Arguments.Contains("users"))
                return Task.FromResult(SingleUser());
            if (call.Arguments.Contains("pm") && call.Arguments.Contains("path"))
            {
                packageQueries++;
                return Task.FromResult(packageQueries == 1
                    ? new ProcessResult
                    {
                        ExitCode = 1,
                        StandardError = "Error: package com.example.app was not found\n",
                    }
                    : Success("package:/data/app/~~token/com.example.app/base.apk\n"));
            }
            if (call.Arguments.Contains("install"))
                throw new OperationCanceledException("adb install was cancelled after device commit");
            return Task.FromResult(Success("Success\n"));
        });
        var deployment = new AndroidAppDeployment(provider, runner);

        var failure = await Assert.ThrowsAsync<AndroidAppDeploymentException>(() =>
            deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
            {
                DeviceSerial = "emulator-5554",
                ApkPath = workspace.WriteApk(),
                PackageId = "com.example.app",
            }));
        var cleanup = await deployment.CleanupAsync(
            failure.Session,
            FlowExecutionCleanupPolicies.Uninstall);

        Assert.Equal(FlowExecutionExitCategories.UnknownCompletion, failure.Failure.ExitCategory);
        Assert.Equal("android-install-cancelled-unknown", failure.Failure.Code);
        Assert.True(failure.Session.InstallAttempted);
        Assert.True(failure.Session.InstalledByInvocation);
        Assert.False(failure.Session.InstallationCompletionUnknown);
        Assert.True(cleanup.PackageUninstalled);
        Assert.Equal(2, packageQueries);
        Assert.Single(runner.Calls, call => call.Arguments.Contains("uninstall"));
    }

    [Fact]
    public async Task AndroidCleanup_FailedLaunch_PreservesOwnershipForRequestedUninstall()
    {
        using var workspace = new ExecutionTestWorkspace();
        var provider = workspace.CreateAndroidProvider();
        var runner = new QueueProcessRunner(
            SingleUser(),
            Success(),
            Success("Success\n"),
            Success("com.other.app/.MainActivity\n"),
            Success("Success\n"));
        var deployment = new AndroidAppDeployment(provider, runner);

        var failure = await Assert.ThrowsAsync<AndroidAppDeploymentException>(() =>
            deployment.DeployAndLaunchAsync(new AndroidAppDeploymentRequest
            {
                DeviceSerial = "emulator-5554",
                ApkPath = workspace.WriteApk(),
                PackageId = "com.example.app",
            }));
        var cleanup = await deployment.CleanupAsync(
            failure.Session,
            FlowExecutionCleanupPolicies.Uninstall);

        Assert.True(failure.Session.InstalledByInvocation);
        Assert.False(failure.Session.LaunchedByInvocation);
        Assert.True(cleanup.PackageUninstalled);
        Assert.Single(runner.Calls, call => call.Arguments.Contains("uninstall"));
    }

    [Fact]
    public async Task Outputs_ManifestJUnitAndReportAgreeOnUnsupportedCategory()
    {
        using var workspace = new ExecutionTestWorkspace();
        var bundle = workspace.WriteBundle(MauiFlowSideEffectPolicies.TestTenantResettable);
        var coordinator = CreateCoordinator(
            new FakeArtifactResolver(Artifact(Path.Combine(workspace.Root, "app.apk"))),
            new FakePlatformAdapter());

        var result = await coordinator.RunAsync(Request(bundle, workspace.Output));

        var manifestJson = await File.ReadAllTextAsync(result.ManifestPath!);
        ExecutionManifestSchemaValidator.AssertValid(manifestJson);
        using var manifest = JsonDocument.Parse(manifestJson);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath!));
        var junit = XDocument.Load(result.JUnitPath!);
        var junitCategory = junit
            .Descendants("property")
            .Single(property => (string?)property.Attribute("name") == "devflow.exitCategory")
            .Attribute("value")?.Value;

        Assert.Equal(result.ExitCategory, manifest.RootElement.GetProperty("outcome").GetProperty("exitCategory").GetString());
        Assert.Equal(result.ExitCategory, junitCategory);
        Assert.Equal(result.ExitCategory, report.RootElement.GetProperty("exitCategory").GetString());
        Assert.Equal("state-evidence-provider-missing", report.RootElement.GetProperty("failure").GetProperty("code").GetString());
        Assert.Equal(
            manifest.RootElement.GetProperty("runId").GetString(),
            report.RootElement.GetProperty("runId").GetString());
        Assert.DoesNotContain("emulator-5554", manifestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\src\App.csproj", manifestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MauiFlowRunOutcomes.Passed, true, null, FlowExecutionExitCategories.Pass)]
    [InlineData(MauiFlowRunOutcomes.Passed, false, null, FlowExecutionExitCategories.Unverified)]
    [InlineData(MauiFlowRunOutcomes.Failed, false, MauiFlowFailureClasses.AssertionFailed, FlowExecutionExitCategories.TestFailure)]
    [InlineData(MauiFlowRunOutcomes.InfrastructureError, false, MauiFlowFailureClasses.Infrastructure, FlowExecutionExitCategories.InfrastructureFailure)]
    [InlineData(MauiFlowRunOutcomes.UnknownCompletion, false, MauiFlowFailureClasses.UnknownCompletion, FlowExecutionExitCategories.UnknownCompletion)]
    [InlineData(MauiFlowRunOutcomes.Failed, false, MauiFlowFailureClasses.FlowInvalid, FlowExecutionExitCategories.InvalidConfiguration)]
    public void OutputCategory_MapsCanonicalReport(
        string status,
        bool verified,
        string? failureCode,
        string expected)
    {
        var report = new MauiFlowRunReport
        {
            Outcome = new MauiFlowRunOutcome
            {
                Status = status,
                Terminal = true,
                Verified = verified,
            },
            Failure = failureCode is null ? null : new MauiFlowFailure { Code = failureCode },
        };

        Assert.Equal(expected, FlowExecutionCoordinator.ClassifyReport(report));
    }

    [Fact]
    public void OutputCategory_UsesCanonicalFailureClassInsteadOfDetailCode()
    {
        var report = new MauiFlowRunReport
        {
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Failed,
                Terminal = true,
                Verified = false,
            },
            Failure = new MauiFlowFailure
            {
                Class = MauiFlowFailureClasses.Infrastructure,
                Code = "selector-zero-matches",
            },
        };

        Assert.Equal(
            FlowExecutionExitCategories.InfrastructureFailure,
            FlowExecutionCoordinator.ClassifyReport(report));
    }

    [Fact]
    public async Task StateEvidence_PostRunOracleIsBoundAndCanVerifyPassedExecution()
    {
        var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
        var plan = OraclePlan();
        var flow = OracleFlow();
        var provider = new FakeStateEvidenceProvider
        {
            PrepareResult = new FlowStateEvidenceResult
            {
                RunContext = AdmissionContext(now),
            },
            Evaluate = request => new FlowPostRunOracleEvidenceResult
            {
                RunId = request.RunId,
                FlowDigest = request.FlowDigest,
                DeviceIdentityFingerprint = request.DeviceIdentityFingerprint,
                AppBuildFingerprint = request.AppBuildFingerprint,
                PackageDigest = request.PackageDigest,
                StartedAt = request.StartedAt,
                EndedAt = request.EndedAt,
                ObservedAt = request.EndedAt.AddSeconds(1),
                BusinessOracles =
                [
                    new MauiIndependentBusinessOracleResult
                    {
                        OracleId = "order-created",
                        Independent = true,
                        Succeeded = true,
                        ObservedAt = request.EndedAt.AddSeconds(1),
                    },
                ],
            },
        };
        var registry = new FlowStateEvidenceProviderRegistry([provider]);
        var evidenceRequest = new FlowStateEvidenceRequest
        {
            Plan = plan,
            Flow = flow,
            Artifact = Artifact("app.apk"),
        };

        var admission = await registry.PrepareAsync(evidenceRequest);
        Assert.Empty(admission.RunContext.BusinessOracles);
        var evaluationRequest = new FlowPostRunOracleEvaluationRequest
        {
            Plan = plan,
            Flow = flow,
            Artifact = evidenceRequest.Artifact,
            RunId = "run-1",
            FlowDigest = "flow-digest",
            DeviceIdentityFingerprint = "device-fingerprint",
            AppBuildFingerprint = "build-fingerprint",
            PackageDigest = evidenceRequest.Artifact.PackageDigest,
            StartedAt = now,
            EndedAt = now.AddSeconds(5),
            EvaluationDeadline = now.AddMinutes(1),
            Report = new MauiFlowRunReport(),
        };
        var oracles = await registry.EvaluatePostRunAsync(admission, evaluationRequest);
        admission.RunContext.BusinessOracles = oracles.ToList();
        var report = new MauiFlowRunReport
        {
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Passed,
                Terminal = true,
                Verified = false,
            },
        };

        FlowExecutionCoordinator.ApplyPostRunVerification(
            report,
            plan,
            flow,
            admission.RunContext,
            now.AddSeconds(7));

        Assert.Equal(1, provider.PrepareCalls);
        Assert.Equal(1, provider.EvaluateCalls);
        Assert.True(report.Outcome?.Verified);
        Assert.True(report.Verification?.Verified);
        Assert.Equal(FlowExecutionExitCategories.Pass, FlowExecutionCoordinator.ClassifyReport(report));
    }

    [Fact]
    public async Task StateEvidence_PreRunOracleEvidenceIsRejected()
    {
        var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
        var context = AdmissionContext(now);
        context.BusinessOracles.Add(new MauiIndependentBusinessOracleResult
        {
            OracleId = "stale",
            Independent = true,
            Succeeded = true,
            ObservedAt = now,
        });
        var registry = new FlowStateEvidenceProviderRegistry(
        [
            new FakeStateEvidenceProvider
            {
                PrepareResult = new FlowStateEvidenceResult { RunContext = context },
            },
        ]);

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            registry.PrepareAsync(new FlowStateEvidenceRequest
            {
                Plan = OraclePlan(),
                Flow = OracleFlow(),
                Artifact = Artifact("app.apk"),
            }));

        Assert.Equal(FlowExecutionExitCategories.InvalidConfiguration, failure.ExitCategory);
        Assert.Equal("pre-run-oracle-evidence-not-allowed", failure.Code);
    }

    [Fact]
    public async Task StateEvidence_PostRunOracleBindingMismatchFailsClosed()
    {
        var now = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);
        var provider = new FakeStateEvidenceProvider
        {
            PrepareResult = new FlowStateEvidenceResult
            {
                RunContext = AdmissionContext(now),
            },
            Evaluate = request => new FlowPostRunOracleEvidenceResult
            {
                RunId = "different-run",
                FlowDigest = request.FlowDigest,
                DeviceIdentityFingerprint = request.DeviceIdentityFingerprint,
                AppBuildFingerprint = request.AppBuildFingerprint,
                PackageDigest = request.PackageDigest,
                StartedAt = request.StartedAt,
                EndedAt = request.EndedAt,
                ObservedAt = request.EndedAt,
            },
        };
        var registry = new FlowStateEvidenceProviderRegistry([provider]);
        var plan = OraclePlan();
        var flow = OracleFlow();
        var artifact = Artifact("app.apk");
        var admission = await registry.PrepareAsync(new FlowStateEvidenceRequest
        {
            Plan = plan,
            Flow = flow,
            Artifact = artifact,
        });

        var failure = await Assert.ThrowsAsync<FlowExecutionException>(() =>
            registry.EvaluatePostRunAsync(admission, new FlowPostRunOracleEvaluationRequest
            {
                Plan = plan,
                Flow = flow,
                Artifact = artifact,
                RunId = "run-1",
                FlowDigest = "flow-digest",
                DeviceIdentityFingerprint = "device-fingerprint",
                AppBuildFingerprint = "build-fingerprint",
                PackageDigest = artifact.PackageDigest,
                StartedAt = now,
                EndedAt = now.AddSeconds(5),
                EvaluationDeadline = now.AddMinutes(1),
                Report = new MauiFlowRunReport(),
            }));

        Assert.Equal(FlowExecutionExitCategories.InfrastructureFailure, failure.ExitCategory);
        Assert.Equal("post-run-oracle-binding-mismatch", failure.Code);
    }

    [Fact]
    public void PlanRequirements_UsesSharedValidatorForAliasesVersionsAndSemantics()
    {
        var plan = new MauiTestPlan
        {
            Requirements = new MauiFlowRequirements
            {
                RequiredCapabilities =
                [
                    new MauiCapabilityRequirement
                    {
                        Name = "agent.ui",
                        MinimumVersion = 1,
                    },
                ],
                RequiredSemantics =
                [
                    new MauiRequiredSemantic
                    {
                        Name = "stable-step-identity",
                        MinimumVersion = 1,
                    },
                ],
            },
        };
        var status = new AgentStatus
        {
            Capabilities = new AgentCapabilities
            {
                Ui = true,
            },
        };

        FlowExecutionCoordinator.ValidatePlanRequirements(plan, status);

        var unsupported = new MauiTestPlan
        {
            Requirements = new MauiFlowRequirements
            {
                RequiredSemantics =
                [
                    new MauiRequiredSemantic
                    {
                        Name = "stable-step-identity",
                        MinimumVersion = 2,
                    },
                ],
            },
        };
        var failure = Assert.Throws<FlowExecutionException>(() =>
            FlowExecutionCoordinator.ValidatePlanRequirements(unsupported, status));
        Assert.Equal("required-semantics-unsupported", failure.Code);
    }

    [Fact]
    public async Task FlowRunHelp_DescribesProductionPlatformOptions()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);

        var result = await cli.InvokeRawAsync("devflow", "flow", "run", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--project", result.StdOut);
        Assert.Contains("--cleanup", result.StdOut);
        Assert.Contains("supported local platform adapters", result.StdOut);
    }

    [Fact]
    public async Task FlowRunCommand_ParsesAndInvokesCoordinator()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);
        var fake = new FakeCoordinator();
        DevFlowCommands.CreateFlowExecutionCoordinator = () => fake;
        try
        {
            var result = await cli.InvokeRawAsync(
                "devflow", "flow", "run", "maui-tests\\checkout.md",
                "--project", "src\\App\\App.csproj",
                "--device", "emulator-5554",
                "--cleanup", "uninstall",
                "--json");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("android", fake.Request?.Platform);
            Assert.Equal("emulator-5554", fake.Request?.DeviceSerial);
            Assert.Equal(FlowExecutionCleanupPolicies.Uninstall, fake.Request?.CleanupPolicy);
            Assert.Equal(FlowExecutionExitCategories.Pass, result.ParseJsonOutput().GetProperty("exitCategory").GetString());
        }
        finally
        {
            DevFlowCommands.ResetBrokerClientForTests();
        }
    }

    [Fact]
    public async Task FlowHandoffHelp_DescribesImportAndDiagnosticOnlyBoundaries()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);

        var reproduce = await cli.InvokeRawAsync("devflow", "flow", "reproduce", "--help");
        var triage = await cli.InvokeRawAsync("devflow", "flow", "triage", "--help");

        Assert.Equal(0, reproduce.ExitCode);
        Assert.Contains("--import", reproduce.StdOut, StringComparison.Ordinal);
        Assert.Contains("--output", reproduce.StdOut, StringComparison.Ordinal);
        Assert.Contains("--json", reproduce.StdOut, StringComparison.Ordinal);
        Assert.Contains("--no-json", reproduce.StdOut, StringComparison.Ordinal);
        Assert.Contains("--platform", reproduce.StdOut, StringComparison.Ordinal);
        Assert.Contains("--device", reproduce.StdOut, StringComparison.Ordinal);
        Assert.Contains("stop after trust evaluation", reproduce.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, triage.ExitCode);
        Assert.Contains("--manifest", triage.StdOut, StringComparison.Ordinal);
        Assert.Contains("--report", triage.StdOut, StringComparison.Ordinal);
        Assert.Contains("--json", triage.StdOut, StringComparison.Ordinal);
        Assert.Contains("--no-json", triage.StdOut, StringComparison.Ordinal);
        Assert.Contains("diagnostic-only", triage.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FlowReproduceCommand_ParsesRunOptionsAndInvokesHandoffCoordinator()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);
        var fake = new FakeReproductionCoordinator();
        DevFlowCommands.CreateFlowReproductionCoordinator = () => fake;
        try
        {
            var result = await cli.InvokeRawAsync(
                "devflow", "flow", "reproduce", "maui-tests\\checkout.md",
                "--import", "downloaded\\flow-run.json",
                "--project", "src\\App\\App.csproj",
                "--device", "emulator-5554",
                "--cleanup", "uninstall",
                "--output", "artifacts\\reproduction",
                "--json");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("downloaded\\flow-run.json", fake.Request?.ImportedArtifactPath);
            Assert.Equal("android", fake.Request?.Execution.Platform);
            Assert.Equal("emulator-5554", fake.Request?.Execution.DeviceSerial);
            Assert.Equal(FlowExecutionCleanupPolicies.Uninstall, fake.Request?.Execution.CleanupPolicy);
            Assert.True(result.ParseJsonOutput().GetProperty("matched").GetBoolean());
            Assert.False(result.ParseJsonOutput().GetProperty("approvalGranted").GetBoolean());
        }
        finally
        {
            DevFlowCommands.ResetBrokerClientForTests();
        }
    }

    [Fact]
    public async Task FlowTriageCommand_InvokesSharedCoordinator()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);
        var fake = new FakeTriageCoordinator();
        DevFlowCommands.CreateFlowTriageCoordinator = () => fake;
        var previousOutput = Environment.GetEnvironmentVariable("MAUIDEVFLOW_OUTPUT");
        Environment.SetEnvironmentVariable("MAUIDEVFLOW_OUTPUT", "json");
        try
        {
            var result = await cli.InvokeRawAsync(
                "devflow", "flow", "triage",
                "--manifest", "execution-manifest.json",
                "--report", "flow-run.json",
                "--format", "markdown",
                "--no-json");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("execution-manifest.json", fake.Request?.ManifestPath);
            Assert.Equal("flow-run.json", fake.Request?.ReportPath);
            Assert.Equal(FlowTriageOutputFormats.Markdown, fake.Request?.Format);
            Assert.Contains("# MAUI DevFlow triage", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAUIDEVFLOW_OUTPUT", previousOutput);
            DevFlowCommands.ResetBrokerClientForTests();
        }
    }

    [Fact]
    public async Task FlowTriageCommand_JsonAndMarkdownFormat_AreRejected()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);
        var fake = new FakeTriageCoordinator();
        DevFlowCommands.CreateFlowTriageCoordinator = () => fake;
        try
        {
            var result = await cli.InvokeRawAsync(
                "devflow", "flow", "triage",
                "--manifest", "execution-manifest.json",
                "--report", "flow-run.json",
                "--format", "markdown",
                "--json");

            Assert.Equal(1, result.ExitCode);
            Assert.Null(fake.Request);
            using var error = JsonDocument.Parse(result.StdErr);
            Assert.Equal("InvalidArgument", error.RootElement.GetProperty("type").GetString());
            Assert.Contains(
                "effective JSON output",
                error.RootElement.GetProperty("error").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DevFlowCommands.ResetBrokerClientForTests();
        }
    }

    [Fact]
    public async Task FlowTriageCommand_RedirectedEffectiveJson_RequiresExplicitNoJsonForMarkdown()
    {
        var cli = new CliTestHarness(mockAgentPort: 1);
        var fake = new FakeTriageCoordinator();
        DevFlowCommands.CreateFlowTriageCoordinator = () => fake;
        var previousOutput = Environment.GetEnvironmentVariable("MAUIDEVFLOW_OUTPUT");
        Environment.SetEnvironmentVariable("MAUIDEVFLOW_OUTPUT", "json");
        try
        {
            var result = await cli.InvokeRawAsync(
                "devflow", "flow", "triage",
                "--manifest", "execution-manifest.json",
                "--report", "flow-run.json",
                "--format", "markdown");

            Assert.Equal(1, result.ExitCode);
            Assert.Null(fake.Request);
            using var error = JsonDocument.Parse(result.StdErr);
            Assert.Contains(
                "effective JSON output",
                error.RootElement.GetProperty("error").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAUIDEVFLOW_OUTPUT", previousOutput);
            DevFlowCommands.ResetBrokerClientForTests();
        }
    }

    private static FlowExecutionCoordinator CreateCoordinator(
        IAppArtifactResolver resolver,
        IFlowExecutionPlatformAdapter adapter,
        IFlowStateEvidenceProviderRegistry? stateEvidenceProviders = null)
        => new(
            new CommittedFlowBundleLoader(),
            resolver,
            [adapter],
            stateEvidenceProviders ?? new FlowStateEvidenceProviderRegistry([]),
            new ExactAgentBindingResolver(_ => throw new InvalidOperationException("Broker must not be reached.")),
            new FlowRunReportWriter(),
            new JUnitFlowExecutionWriter(),
            new ExecutionManifestWriter(),
            new ImmutableExecutionOutputWriter(),
            () => throw new InvalidOperationException("Broker must not be started."));

    private static FlowExecutionRequest Request((string Flow, string Plan) bundle, string output)
        => new()
        {
            FlowPath = bundle.Flow,
            PlanPath = bundle.Plan,
            ProjectPath = @"C:\src\App.csproj",
            Platform = "android",
            TargetFramework = "net10.0-android",
            Configuration = "Debug",
            DeviceSerial = "emulator-5554",
            OutputDirectory = output,
        };

    private static ResolvedAppArtifact Artifact(string path) => new()
    {
        Path = path,
        ProjectPath = @"C:\src\App.csproj",
        AgentSessionId = "flowsession",
        TargetFramework = "net10.0-android",
        TargetPlatformIdentifier = "android",
        Configuration = "Debug",
        ApplicationId = "com.example.app",
        ArtifactType = "apk",
        ArtifactContractVersion = "1",
        ArtifactRole = "deployable",
        TargetRuntimeKind = "android",
        DeploymentModel = "package",
        LaunchIdentityKind = "android-package-name",
        LaunchIdentity = "com.example.app",
        Installable = true,
        Launchable = true,
        PackageDigest = "sha256:" + new string('a', 64),
    };

    private static AgentRegistration Agent(string instanceId) => new()
    {
        Id = "agent",
        InstanceId = instanceId,
        Project = @"C:\src\App.csproj",
        Tfm = "net10.0-android",
        Platform = "Android",
        AppName = "App",
        PackageId = "com.example.app",
        SessionId = "flowsession",
        DeviceId = "platform=android;serial=emulator-5554",
        ProcessId = 321,
        Port = instanceId == "old-instance" ? 9223 : 9224,
    };

    private static ExactAgentBindingExpectation Expectation() => new()
    {
        SessionId = "flowsession",
        TargetFramework = "net10.0-android",
        Platform = "android",
        PlatformAliases = ["android"],
        PackageId = "com.example.app",
        DeviceSerial = "emulator-5554",
        ProcessId = 321,
    };

    private static MauiTestPlan OraclePlan() => new()
    {
        PlanId = "oracle-plan",
        Revision = 1,
        Flow = new MauiFlowReference
        {
            Path = "oracle-flow.md",
            Digest = "sha256:" + new string('1', 64),
        },
        Goal = "Verify the independent order-created business oracle.",
        Reset = new MauiTestResetRequirement(),
        RequiredPlatforms = ["android"],
        SideEffectPolicy = MauiFlowSideEffectPolicies.None,
        Provenance = new MauiActorProvenance
        {
            ActorKind = "human",
            Channel = "unit-test",
        },
        IndependentBusinessOracles =
        [
            new MauiIndependentBusinessOracleDeclaration
            {
                OracleId = "order-created",
                Required = true,
                Independent = true,
            },
        ],
    };

    private static MauiFlow OracleFlow() => new()
    {
        Name = "oracle-flow",
        Platform = "android",
        Steps = [],
    };

    private static MauiFlowRunContext AdmissionContext(DateTimeOffset checkedAt)
        => new()
        {
            Intent = MauiFlowReplayIntents.OrdinaryReplay,
            Preconditions = new MauiFlowReplayPreconditions
            {
                Expected = new MauiFlowCheckpoint(),
                Observed = new MauiFlowCheckpoint(),
                CheckedAt = checkedAt,
            },
            PriorMutationCompletionCertain = true,
            BusinessOracles = [],
        };

    private static ProcessResult Success(string stdout = "") => new()
    {
        ExitCode = 0,
        StandardOutput = stdout,
    };

    private static ProcessResult SingleUser()
        => Success("Users:\n\tUserInfo{0:Owner:13} running\n");

    private static string ArtifactMetadata(
        string artifactPath,
        string projectPath,
        string runtimeIdentifier)
        => string.Join(
            "|||DEVFLOW_ARTIFACT|||",
            artifactPath,
            "App",
            projectPath,
            "net10.0-android",
            "android",
            runtimeIdentifier,
            "Debug",
            "com.example.app",
            "apk",
            "1",
            "deployable",
            "android",
            "package",
            "android-package-name",
            "com.example.app",
            "true",
            "true");

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(Environment.CurrentDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "MauiLabs.slnx")))
                return current.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static bool TryCreateDirectorySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class FakeArtifactResolver(ResolvedAppArtifact artifact) : IAppArtifactResolver
    {
        public int Calls { get; private set; }

        public Task<ResolvedAppArtifact> ResolveAsync(
            AppArtifactResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(artifact with { AgentSessionId = request.AgentSessionId });
        }
    }

    private sealed class ThrowingArtifactResolver(FlowExecutionException failure) : IAppArtifactResolver
    {
        public Task<ResolvedAppArtifact> ResolveAsync(
            AppArtifactResolutionRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<ResolvedAppArtifact>(failure);
    }

    private sealed class FakeStateEvidenceProvider : IFlowStateEvidenceProvider
    {
        public string ProviderId => "fake-state-evidence";
        public FlowStateEvidenceResult PrepareResult { get; init; } = new()
        {
            RunContext = AdmissionContext(DateTimeOffset.UtcNow),
        };
        public Func<FlowPostRunOracleEvaluationRequest, FlowPostRunOracleEvidenceResult>? Evaluate { get; init; }
        public int PrepareCalls { get; private set; }
        public int EvaluateCalls { get; private set; }

        public bool Supports(FlowStateEvidenceRequest request) => true;

        public Task<FlowStateEvidenceResult> PrepareAsync(
            FlowStateEvidenceRequest request,
            CancellationToken cancellationToken = default)
        {
            PrepareCalls++;
            return Task.FromResult(PrepareResult);
        }

        public Task<FlowPostRunOracleEvidenceResult> EvaluatePostRunAsync(
            FlowPostRunOracleEvaluationRequest request,
            CancellationToken cancellationToken = default)
        {
            EvaluateCalls++;
            return Task.FromResult(
                Evaluate?.Invoke(request) ??
                new FlowPostRunOracleEvidenceResult
                {
                    RunId = request.RunId,
                    FlowDigest = request.FlowDigest,
                    DeviceIdentityFingerprint = request.DeviceIdentityFingerprint,
                    AppBuildFingerprint = request.AppBuildFingerprint,
                    PackageDigest = request.PackageDigest,
                    StartedAt = request.StartedAt,
                    EndedAt = request.EndedAt,
                    ObservedAt = request.EndedAt,
                });
        }
    }

    private sealed class FakePlatformAdapter(
        bool validateWithAndroidRules = false,
        bool allowMutation = false,
        bool cleanupSucceeds = true) : IFlowExecutionPlatformAdapter
    {
        public int MutationCalls { get; private set; }
        public int CleanupCalls { get; private set; }
        public FlowExecutionPlatformDescriptor Descriptor { get; } = new()
        {
            Platform = "android",
            DisplayName = "Android",
            CommandAliases = ["android"],
            FlowPlatformAliases = ["android"],
            AgentPlatformAliases = ["android"],
            TargetFrameworkPlatformIdentifiers = ["android"],
            CandidateArtifactTypes = ["apk", "aab"],
        };

        public void ValidateHost()
        {
        }

        public string? GetDefaultRuntimeIdentifier() => null;

        public Task<FlowExecutionPlatformPreflight> PreflightAsync(
            FlowExecutionPlatformPreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            if (validateWithAndroidRules)
                AndroidFlowExecutionAdapter.ValidateAndroidArtifact(request.Artifact);
            return Task.FromResult(new FlowExecutionPlatformPreflight
            {
                Device = CreateDevice(),
                DeviceSerial = "emulator-5554",
                PackageId = "com.example.app",
            });
        }

        public Task<FlowExecutionPlatformSession> PrepareAndLaunchAsync(
            FlowExecutionPlatformRequest request,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            if (!allowMutation)
                throw new InvalidOperationException("Mutation should not be reached.");
            return Task.FromResult(new FlowExecutionPlatformSession
            {
                Device = request.Preflight.Device,
                DeviceSerial = "emulator-5554",
                PackageId = "com.example.app",
                Platform = "android",
                RuntimeKind = "emulator",
                DeviceProfile = "emulator",
                ProcessId = 321,
                InstalledByInvocation = true,
                LaunchedByInvocation = true,
                State = new object(),
            });
        }

        public Task EstablishAgentForwardingAsync(
            FlowExecutionPlatformSession session,
            int agentPort,
            int brokerPort,
            CancellationToken cancellationToken = default)
        {
            MutationCalls++;
            if (!allowMutation)
                throw new InvalidOperationException("Mutation should not be reached.");
            return Task.CompletedTask;
        }

        public Task<FlowExecutionCleanupResult> CleanupAsync(
            FlowExecutionPlatformSession session,
            string cleanupPolicy,
            CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.FromResult(new FlowExecutionCleanupResult
            {
                Succeeded = cleanupSucceeds,
                PackageStopped = cleanupSucceeds,
                DetailCode = cleanupSucceeds ? "cleanup-complete" : "fake-cleanup-failed",
            });
        }

        private static Device CreateDevice() => new()
        {
            Id = "emulator-5554",
            Name = "Pixel",
            Platforms = ["android"],
            IsEmulator = true,
            IsRunning = true,
            State = DeviceState.Booted,
            Type = DeviceType.Emulator,
            Architecture = "x64",
            Version = "35",
            Idiom = DeviceIdiom.Phone,
        };
    }

    private sealed class QueueProcessRunner(params ProcessResult[] results) : IExecutionProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results);
        public List<(string FileName, string[] Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            IEnumerable<string>? environmentVariablesToRemove = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Success());
        }
    }

    private sealed class CallbackProcessRunner(
            Func<(string FileName, string[] Arguments), Task<ProcessResult>> callback)
        : IExecutionProcessRunner
    {
        public List<(string FileName, string[] Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            IEnumerable<string>? environmentVariablesToRemove = null,
            CancellationToken cancellationToken = default)
        {
            var call = (fileName, arguments.ToArray());
            Calls.Add(call);
            return callback(call);
        }
    }

    private sealed class FakeCoordinator : IFlowExecutionCoordinator
    {
        public FlowExecutionRequest? Request { get; private set; }

        public Task<FlowExecutionResult> RunAsync(
            FlowExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new FlowExecutionResult
            {
                ExitCategory = FlowExecutionExitCategories.Pass,
                Message = "passed",
                OutputDirectory = @"C:\artifacts\run",
                ManifestPath = @"C:\artifacts\run\execution-manifest.json",
                ReportPath = @"C:\artifacts\run\flow-run.json",
                JUnitPath = @"C:\artifacts\run\report.junit.xml",
            });
        }
    }

    private sealed class FakeReproductionCoordinator : IFlowReproductionCoordinator
    {
        public FlowReproductionRequest? Request { get; private set; }

        public Task<FlowReproductionResult> ReproduceAsync(
            FlowReproductionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var report = MauiLocalReproductionReportSerializer.CreateSafeProjection(
                new MauiLocalReproductionReport
                {
                    ImportedArtifact = MauiImportedArtifactIdentity.Create(),
                    ImportedArtifactKind = "flow-run",
                    ImportedArtifactDigest = new string('a', 64),
                    LocalRunId = "run-local",
                    LocalExitCategory = FlowExecutionExitCategories.TestFailure,
                    LocalManifestDigest = new string('b', 64),
                    LocalReportDigest = new string('c', 64),
                    Matched = true,
                    TrustState = MauiArtifactTrustStates.LocallyReproduced,
                    ReasonCodes = ["locally-reproduced"],
                });
            return Task.FromResult(new FlowReproductionResult
            {
                LocalExecution = new FlowExecutionResult
                {
                    ExitCategory = FlowExecutionExitCategories.TestFailure,
                },
                Report = report,
                ReportPath = "local-reproduction.json",
            });
        }
    }

    private sealed class FakeTriageCoordinator : IFlowTriageCoordinator
    {
        public FlowTriageRequest? Request { get; private set; }

        public Task<FlowTriageResult> AnalyzeAsync(
            FlowTriageRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new FlowTriageResult
            {
                Triage = new MauiFlowTriage
                {
                    ImportedEvidence = true,
                    RepairEligible = false,
                },
                Content = Encoding.UTF8.GetBytes("# MAUI DevFlow triage\n"),
            });
        }
    }

    private sealed class FakeAppSourceIdentityProvider : IAppSourceIdentityProvider
    {
        public Task<AppSourceIdentity> ResolveAsync(
            string projectPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new AppSourceIdentity
            {
                SourceRevision = "0123456789abcdef0123456789abcdef01234567",
                AppSourceFingerprint = "source-current",
            });
    }

    private sealed class ExecutionTestWorkspace : IDisposable
    {
        public ExecutionTestWorkspace()
        {
            Root = Path.Combine(
                FindRepositoryRoot(),
                "artifacts",
                "TestResults",
                "flow-execution-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Output);
        }

        public string Root { get; }
        public string Output => Path.Combine(Root, "output");

        public (string Flow, string Plan) WriteBundle(
            string policy,
            string expectedRoute = "//checkout")
        {
            const string flowName = "checkout.md";
            var flow = new MauiFlow
            {
                Name = "checkout",
                App = "App",
                Platform = "android",
                Steps =
                [
                    new FlowStep
                    {
                        Seq = 1,
                        StepId = "open-checkout",
                        Action = FlowActions.Assert,
                        Asserts =
                        [
                            new FlowAssert
                            {
                                Kind = "routeIs",
                                Expected = expectedRoute,
                                Verify = true,
                            },
                        ],
                    },
                ],
            };
            var digest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow);
            var plan = new MauiTestPlan
            {
                PlanId = "plan-checkout",
                Revision = 1,
                Flow = new MauiFlowReference
                {
                    Path = flowName,
                    Digest = digest,
                },
                Goal = "Verify checkout.",
                Reset = new MauiTestResetRequirement
                {
                    Required = policy != MauiFlowSideEffectPolicies.None,
                    SeedFingerprint = policy == MauiFlowSideEffectPolicies.TestTenantResettable ? "seed" : null,
                    BackendStateFingerprint = policy == MauiFlowSideEffectPolicies.TestTenantResettable ? "backend" : null,
                },
                RequiredPlatforms = ["android"],
                SideEffectPolicy = policy,
                Provenance = new MauiActorProvenance
                {
                    ActorKind = "human",
                    Channel = "unit-test",
                },
            };
            var flowPath = Path.Combine(Root, flowName);
            var planPath = Path.Combine(Root, "checkout.maui-plan.json");
            File.WriteAllText(flowPath, FlowMarkdown.Serialize(flow));
            File.WriteAllText(
                planPath,
                JsonSerializer.Serialize(plan, MauiTestingJsonContext.Default.MauiTestPlan));
            return (flowPath, planPath);
        }

        public FakeAndroidProvider CreateAndroidProvider()
        {
            var sdk = Path.Combine(Root, "android-sdk");
            var platformTools = Path.Combine(sdk, "platform-tools");
            Directory.CreateDirectory(platformTools);
            File.WriteAllText(Path.Combine(platformTools, OperatingSystem.IsWindows() ? "adb.exe" : "adb"), "");
            return new FakeAndroidProvider { SdkPath = sdk };
        }

        public string WriteApk()
        {
            var path = Path.Combine(Root, "app.apk");
            File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "MauiLabs.slnx")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new InvalidOperationException("Repository root not found.");
        }
    }
}
