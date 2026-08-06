using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Evidence;
using Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using Xunit.Abstractions;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests;

[CollectionDefinition("WindowsFlowQa", DisableParallelization = true)]
public sealed class WindowsFlowQaCollection;

/// <summary>
/// Real second-platform QA. This class is intentionally environment-gated because it builds and
/// launches a WinUI MAUI app; the checked-in PowerShell entry point enables it explicitly.
/// </summary>
[Collection("WindowsFlowQa")]
[Trait("Category", "WindowsFlowQa")]
public sealed class WindowsFlowQaTests
{
    const int DefaultCleanRepetitions = 3;
    readonly ITestOutputHelper _output;

    public WindowsFlowQaTests(ITestOutputHelper output)
        => _output = output;

    [Fact]
    public async Task TierOneFlows_RunThreeCleanWindowsAttempts_AndPreserveFirstAttemptArtifacts()
    {
        if (!IsEnabled())
        {
            _output.WriteLine(
                "Windows flow QA not requested. Set DEVFLOW_RUN_WINDOWS_FLOW_QA=1 on a Windows MAUI host.");
            return;
        }

        var repositoryRoot = AppFixtureBase.FindRepoRoot();
        var configuration = WindowsFlowQaConfiguration.FromEnvironment(repositoryRoot);
        var manifest = FlowPilotArtifactManifest.Create(configuration.ManifestOptions);
        var failures = new List<string>();
        var flows = new List<FlowPilotFlowSource>();
        WindowsFixture? fixture = null;
        WindowsFlowTestHost? host = null;
        var cleanRepetitions = ResolveCleanRepetitions(
            Environment.GetEnvironmentVariable("DEVFLOW_FLOW_QA_REPEAT"));

        try
        {
            flows = LoadTierOneFlows(repositoryRoot);
            fixture = new WindowsFixture();
            await fixture.InitializeAsync();
            host = fixture.CreateFlowTestHost();

            foreach (var flow in flows)
            {
                for (var repetition = 1; repetition <= cleanRepetitions; repetition++)
                {
                    var runId = $"{flow.Flow.Name}-attempt-{repetition}";
                    var runDirectory = Path.Combine(configuration.ArtifactRoot, runId);
                    Directory.CreateDirectory(runDirectory);
                    var evidence = new FlowReplayEvidenceCapture(
                        fixture.Client,
                        Path.Combine(runDirectory, "failure.mauitrace"),
                        repositoryRoot,
                        "windows-flow-qa");

                    var result = await host.RunAsync(
                        flow.Flow,
                        CreatePlan(flow.Flow.Name),
                        new WindowsFlowRunRequest
                        {
                            RunId = runId,
                            ArtifactRoot = configuration.ArtifactRoot,
                            FailureEvidenceCapture = evidence,
                        });

                    manifest.RecordCleanAttempt(flow.Flow, flow.SourcePath, repetition, result.Report);
                    if (!string.Equals(result.Report.Outcome?.Status, MauiFlowRunOutcomes.Passed, StringComparison.Ordinal))
                    {
                        failures.Add(
                            $"{flow.Flow.Name} clean attempt {repetition}: " +
                            $"{result.Report.Outcome?.Status ?? "unknown"} " +
                            $"({result.Report.Failure?.Code ?? "no-failure-code"}).");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var flow in flows)
            {
                if (!manifest.Flows.Any(entry =>
                        string.Equals(entry.Name, flow.Flow.Name, StringComparison.Ordinal) &&
                        entry.FirstAttempt is not null))
                {
                    manifest.RecordInfrastructureFailure(
                        flow.Flow,
                        flow.SourcePath,
                        repetition: 1,
                        phase: "windows-fixture-initialization");
                }
            }

            manifest.AddOmission(
                "windows-infrastructure",
                $"The Windows flow QA stopped before all clean attempts completed ({ex.GetType().Name}).");
            failures.Add($"Windows flow QA infrastructure failed ({ex.GetType().Name}).");
        }
        finally
        {
            if (host is not null)
            {
                try { await host.DisposeAsync(); }
                catch (Exception ex) { manifest.AddOmission("host-cleanup", $"Windows host cleanup failed ({ex.GetType().Name})."); }
            }

            if (fixture is not null)
            {
                try { await fixture.DisposeAsync(); }
                catch (Exception ex) { manifest.AddOmission("fixture-cleanup", $"Windows fixture cleanup failed ({ex.GetType().Name})."); }
            }

            var write = manifest.WriteAtomic();
            if (!write.Ok)
            {
                failures.Add(write.Error ?? "The Windows Tier-1 manifest could not be written.");
            }
            else
            {
                _output.WriteLine($"Windows Tier-1 manifest: {write.Path}");
                if (write.Validation is { IsValid: false })
                    failures.AddRange(write.Validation.Errors);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task PlatformContracts_ValidateIdentityNavigationDialogsAndWebView_WhenEnabled()
    {
        if (!IsEnabled())
            return;

        var artifactRoot = ResolveWindowsArtifactRoot();
        var failures = new List<string>();
        var capabilityGaps = new List<string>();
        var outcomes = new List<object>();
        var fixture = new WindowsFixture();
        try
        {
            await fixture.InitializeAsync();
            var add = Assert.Single(await fixture.Client.QueryAsync(automationId: "AddButton"));
            Assert.Equal("AddButton", add.AutomationId);
            if (!string.Equals(add.NativeAutomationIdentity, "AddButton", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(add.NativeAutomationIdentityKind))
            {
                failures.Add("Windows managed AutomationId did not map to an authoritative native UIA identity.");
                outcomes.Add(new { name = "managedNativeIdentity", status = "failed" });
            }
            else
            {
                outcomes.Add(new { name = "managedNativeIdentity", status = "passed" });
            }

            await AssertDuplicateSelectorRejectedAsync();
            outcomes.Add(new { name = "duplicateSelectorRejection", status = "passed" });
            await fixture.Client.NavigateAsync("//interactions");
            await WaitForRouteAsync(fixture.Client, "interactions");
            var checkpointRoute = (await fixture.Client.GetStatusAsync())?.Route;
            await fixture.Client.NavigateAsync("//native");
            await WaitForRouteAsync(fixture.Client, "native");
            if (!string.IsNullOrWhiteSpace(checkpointRoute))
            {
                await fixture.Client.NavigateAsync(checkpointRoute);
                await WaitForRouteAsync(fixture.Client, "interactions");
            }
            outcomes.Add(new { name = "shellModalRouteRestore", status = "passed" });

            await fixture.Client.NavigateAsync("//native");
            var modalButton = Assert.Single(await fixture.Client.QueryAsync(automationId: "ShowModalButton"));
            Assert.True(await fixture.Client.TapAsync(modalButton.Id));
            await WaitForElementAsync(fixture.Client, "ModalTitle");
            var modalStatus = await fixture.Client.GetStatusAsync();
            if (string.IsNullOrWhiteSpace(modalStatus?.Modal))
            {
                capabilityGaps.Add("agent.modal-checkpoint");
                outcomes.Add(new { name = "modalCheckpoint", status = "capability-missing" });
            }
            var close = Assert.Single(await fixture.Client.QueryAsync(automationId: "CloseModalButton"));
            Assert.True(await fixture.Client.TapAsync(close.Id));
            await WaitForElementAsync(fixture.Client, "AddButton");

            await fixture.Client.NavigateAsync("//dialogs");
            var alert = Assert.Single(await fixture.Client.QueryAsync(automationId: "AlertOkOnlyBtn"));
            Assert.True(await fixture.Client.TapAsync(alert.Id));
            var nativeOk = await TryWaitForNativeButtonAsync(fixture.Client, "OK");
            if (nativeOk is null)
            {
                capabilityGaps.Add("windows-native-dialog-uia");
                outcomes.Add(new { name = "nativeDialog", status = "capability-missing" });
            }
            else
            {
                Assert.True(await fixture.Client.TapAsync(nativeOk.Id));
                outcomes.Add(new { name = "nativeDialog", status = "passed" });
            }

            var status = await fixture.Client.GetStatusAsync();
            if (status?.Capabilities?.WebView != true)
            {
                capabilityGaps.Add("agent.webview");
                outcomes.Add(new { name = "webViewContextReset", status = "capability-missing" });
            }
            else
            {
                await fixture.Client.NavigateAsync("//blazor");
                var firstContexts = await WaitForWebViewContextsAsync(fixture.Client);
                Assert.True(HasWebViewContexts(firstContexts));

                var reset = await fixture.HardResetAsync(new PlatformFlowResetRequest());
                var launch = await fixture.LaunchAsync();
                await fixture.WaitForExpectedAgentAsync(new PlatformAgentExpectation
                {
                    PackageId = launch.PackageId,
                    ExpectedProcessId = launch.ProcessId,
                    PreviousAgent = launch.PreviousAgent,
                });
                var seed = await fixture.SeedAsync(new PlatformFlowSeedRequest());
                await fixture.VerifyCheckpointAsync(new PlatformCheckpointRequest
                {
                    Expected = new MauiFlowCheckpoint { Route = "//native" },
                    Reset = reset,
                    Seed = seed,
                });
                await fixture.Client.NavigateAsync("//blazor");
                var resetContexts = await WaitForWebViewContextsAsync(fixture.Client);
                Assert.True(HasWebViewContexts(resetContexts));
                outcomes.Add(new { name = "webViewContextReset", status = "passed" });
            }
        }
        catch (PlatformFlowLifecycleException ex)
        {
            failures.Add(ex.Message);
            outcomes.Add(new
            {
                name = "runtimeLifecycle",
                status = ex.Kind == PlatformFlowLifecycleFailureKind.Capability
                    ? "capability-missing"
                    : "infrastructure-failure",
            });
        }
        catch (Exception ex) when (ex is Xunit.Sdk.XunitException or HttpRequestException or IOException or TimeoutException)
        {
            failures.Add(ex.Message);
            outcomes.Add(new { name = "runtimeLifecycle", status = "infrastructure-failure" });
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        if (capabilityGaps.Count > 0)
            failures.Add($"capability-missing: {string.Join(", ", capabilityGaps)}");

        await WriteWindowsContractArtifactAsync(artifactRoot, outcomes, failures, capabilityGaps);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    static bool IsEnabled()
        => OperatingSystem.IsWindows() &&
           string.Equals(
               Environment.GetEnvironmentVariable("DEVFLOW_RUN_WINDOWS_FLOW_QA"),
               "1",
               StringComparison.Ordinal);

    internal static int ResolveCleanRepetitions(string? configured)
        => int.TryParse(configured, out var value) && value is >= 1 and <= 20
            ? value
            : DefaultCleanRepetitions;

    static List<FlowPilotFlowSource> LoadTierOneFlows(string repositoryRoot)
    {
        var directory = Path.Combine(repositoryRoot, "samples", "DevFlow.Sample", "maui-tests");
        var sources = new List<FlowPilotFlowSource>();
        foreach (var path in Directory.GetFiles(directory, "*.md")
                     .Where(static path => !string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var parsed = FlowMarkdown.Parse(File.ReadAllText(path), path);
            if (!parsed.Ok)
                throw new InvalidOperationException($"{Path.GetFileName(path)} could not be parsed: {parsed.Error}");

            var validation = FlowValidator.Validate(parsed.Flow!);
            if (!validation.Ok)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(path)} is invalid: {string.Join("; ", validation.Errors)}");
            if (!parsed.Flow!.Platform!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains("windows", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(path)} does not declare the required windows platform tag.");
            }

            sources.Add(new FlowPilotFlowSource(path, parsed.Flow!));
        }

        if (sources.Count is < 6 or > 12)
            throw new InvalidOperationException($"Expected 6-12 Tier-1 flows, found {sources.Count}.");

        return sources;
    }

    static MauiTestPlan CreatePlan(string? flowName)
        => new()
        {
            PlanId = $"windows-flow-qa-{flowName ?? "flow"}",
            SideEffectPolicy = MauiFlowSideEffectPolicies.None,
            RequiredPlatforms = ["android", "windows"],
            Requirements = new MauiFlowRequirements
            {
                RequiredCapabilities =
                [
                    new MauiCapabilityRequirement
                    {
                        Name = "agent.ui",
                        Required = true,
                        Reason = "Tier-1 selectors must be resolved by the in-app agent.",
                    },
                    new MauiCapabilityRequirement
                    {
                        Name = "agent.mutations",
                        Required = true,
                        Reason = "The canonical runner must dispatch typed test actions.",
                    },
                    new MauiCapabilityRequirement
                    {
                        Name = "agent.workflowCommandLedger",
                        Required = true,
                        Reason = "The platform contract requires command receipt capability.",
                    },
                ],
            },
            Checkpoint = new MauiFlowCheckpointRequirements { Route = "//native" },
            IndependentBusinessOracles =
            [
                new MauiIndependentBusinessOracleDeclaration
                {
                    OracleId = "sample-integration-state",
                    Required = true,
                    Independent = true,
                    EvidenceKind = "sample-test-state",
                },
            ],
        };

    static async Task AssertDuplicateSelectorRejectedAsync()
    {
        var driver = new DuplicateIdentityDriver();
        var report = await new MauiFlowRunner(driver, new MauiFlowRunnerOptions
        {
            RunId = "windows-duplicate-selector",
            PollTries = 1,
            PollGapMs = 0,
        }).RunAsync(new MauiFlow
        {
            Name = "windows-duplicate-selector",
            Platform = "windows",
            Steps =
            [
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Tap,
                    Args = new FlowStepArgs { Selector = new FlowSelector { AutomationId = "AddButton" } },
                },
            ],
        });

        Assert.Equal(MauiFlowRunOutcomes.Failed, report.Outcome?.Status);
        Assert.Equal(0, driver.MutationCalls);
        Assert.Equal(MauiFlowFailureClasses.LocatorAmbiguous, report.Failure?.Code);
    }

    static async Task WaitForRouteAsync(AgentClient client, string routePart, int timeoutMs = 10000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var route = (await client.GetStatusAsync())?.Route;
            if (route?.Contains(routePart, StringComparison.OrdinalIgnoreCase) == true)
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException($"The Windows agent did not report route containing '{routePart}'.");
    }

    static async Task WaitForElementAsync(AgentClient client, string automationId, int timeoutMs = 10000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await client.QueryAsync(automationId: automationId)).Count == 1)
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException($"The Windows agent did not expose '{automationId}'.");
    }

    static async Task<ElementInfo?> TryWaitForNativeButtonAsync(AgentClient client, string text, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var native = (await client.QueryAsync(type: "Button", text: text))
                .FirstOrDefault(element =>
                    element.Id.StartsWith("native:", StringComparison.Ordinal) &&
                    string.Equals(element.Text, text, StringComparison.OrdinalIgnoreCase));
            if (native is not null)
                return native;
            await Task.Delay(200);
        }

        return null;
    }

    static async Task<JsonElement> WaitForWebViewContextsAsync(AgentClient client, int timeoutMs = 30000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        JsonElement latest = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await client.GetCdpWebViewsAsync();
            if (HasWebViewContexts(latest))
                return latest;
            await Task.Delay(250);
        }

        return latest;
    }

    static bool HasWebViewContexts(JsonElement contexts)
        => contexts.ValueKind == JsonValueKind.Array
            ? contexts.GetArrayLength() > 0
            : contexts.ValueKind == JsonValueKind.Object &&
              contexts.TryGetProperty("webviews", out var values) &&
              values.ValueKind == JsonValueKind.Array &&
              values.GetArrayLength() > 0;

    static string ResolveWindowsArtifactRoot()
    {
        var root = Environment.GetEnvironmentVariable("DEVFLOW_FLOW_QA_ARTIFACT_ROOT")
            ?? Path.Combine(
                AppFixtureBase.FindRepoRoot(),
                "artifacts",
                "TestResults",
                "devflow-flow",
                "windows-contract");
        var fullRoot = Path.GetFullPath(root);
        var repositoryRoot = Path.GetFullPath(AppFixtureBase.FindRepoRoot());
        var relative = Path.GetRelativePath(repositoryRoot, fullRoot);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Windows QA artifacts must remain under the repository.");
        return fullRoot;
    }

    static async Task WriteWindowsContractArtifactAsync(
        string artifactRoot,
        IReadOnlyList<object> outcomes,
        IReadOnlyList<string> failures,
        IReadOnlyList<string> capabilityGaps)
    {
        try
        {
            Directory.CreateDirectory(artifactRoot);
            var path = Path.Combine(artifactRoot, "windows-contract.json");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var status = failures.Count == 0
                    ? "passed"
                    : capabilityGaps.Count > 0
                        ? "capability-missing"
                        : "infrastructure-failure";
                var value = new
                {
                    schema = 1,
                    kind = "devflow-windows-platform-contract",
                    platform = "windows",
                    generatedAt = DateTimeOffset.UtcNow,
                    status,
                    outcomes,
                    capabilityGaps,
                    failureCount = failures.Count,
                };
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        catch
        {
            // The primary assertion still reports the lifecycle/capability result.
        }
    }

    internal sealed class DuplicateIdentityDriver : IMauiFlowDriver
    {
        readonly List<ElementInfo> _elements =
        [
            Element("one"),
            Element("two"),
        ];

        public int MutationCalls { get; private set; }
        public WorkflowCommandReceipt? LastWorkflowCommandReceipt => null;

        public Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
            => Task.FromResult(
                string.Equals(automationId, "AddButton", StringComparison.Ordinal)
                    ? _elements
                    : []);
        public Task<ElementInfo?> GetElementAsync(string id)
            => Task.FromResult(_elements.FirstOrDefault(element => element.Id == id));
        public Task<bool> TapAsync(string elementId) { MutationCalls++; return Task.FromResult(true); }
        public Task<bool> FillAsync(string elementId, string text) => Task.FromResult(false);
        public Task<bool> SetPropertyAsync(string elementId, string propertyName, string value) => Task.FromResult(false);
        public Task<bool> ScrollAsync(string? elementId = null, double deltaX = 0, double deltaY = 0, bool animated = true, int? itemIndex = null, string? scrollToPosition = null) => Task.FromResult(false);
        public Task<bool> NavigateAsync(string route) => Task.FromResult(false);
        public Task<bool> BackAsync() => Task.FromResult(false);
        public Task<ThemeResult> SetThemeAsync(DevFlowTheme theme) => Task.FromResult(new ThemeResult { Theme = theme, Success = false });
        public Task<string?> GetPropertyAsync(string elementId, string propertyName) => Task.FromResult<string?>(null);
        public Task<AgentStatus?> GetStatusAsync() => Task.FromResult<AgentStatus?>(new AgentStatus { Running = true });
        public Task<List<ElementInfo>> GetTreeAsync(int maxDepth = 0) => Task.FromResult(_elements);

        static ElementInfo Element(string id) => new()
        {
            Id = id,
            AutomationId = "AddButton",
            NativeAutomationIdentity = "AddButton",
            NativeAutomationIdentityKind = "uia-automation-id",
            Type = "Button",
            IsVisible = true,
            IsEnabled = true,
            Opacity = 1,
            Bounds = new BoundsInfo { Width = 20, Height = 20 },
        };
    }
}

public sealed class WindowsFlowTestHostContractTests
{
    [Fact]
    public async Task Lifecycle_UsesCanonicalOrderingAndDisablesMutationRetries()
    {
        var lifecycle = new FakeLifecycle();
        var driver = new FakeFlowDriver();
        using var client = new AgentClient("127.0.0.1", 1) { RetryMutatingRequests = true };
        bool? retryDuringMutation = null;
        driver = new FakeFlowDriver { OnMutation = () => retryDuringMutation = client.RetryMutatingRequests };
        await using var host = new WindowsFlowTestHost(lifecycle, driver, client);

        var result = await host.RunAsync(WindowsTapFlow(), WindowsPlan());

        Assert.Equal(MauiFlowRunOutcomes.Passed, result.Report.Outcome?.Status);
        Assert.Equal(
            ["build", "install", "reset", "launch", "wait", "seed", "verify"],
            lifecycle.Calls.Take(7).ToArray());
        Assert.False(retryDuringMutation);
        Assert.True(client.RetryMutatingRequests);
    }

    [Fact]
    public async Task SeedMismatch_BlocksBeforeCanonicalRunnerMutation()
    {
        var lifecycle = new FakeLifecycle
        {
            VerifyException = PlatformFlowLifecycleException.Precondition("seed fingerprint mismatch"),
        };
        var driver = new FakeFlowDriver();
        await using var host = new WindowsFlowTestHost(lifecycle, driver);

        var result = await host.RunAsync(WindowsTapFlow(), WindowsPlan());

        Assert.Equal(MauiFlowFailureClasses.PreconditionUnsatisfied, result.Report.Failure?.Code);
        Assert.Equal("windows-lifecycle", result.Report.Failure?.Phase);
        Assert.Equal(0, driver.MutationCalls);
    }

    [Fact]
    public async Task DesktopAdmissionInfrastructureFailure_PreservesFirstAttemptManifestAndSkipsMutation()
    {
        var root = Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "artifacts",
            "TestResults",
            "windows-desktop-admission-manifest",
            Guid.NewGuid().ToString("N"));
        try
        {
            var lifecycle = new FakeLifecycle
            {
                LaunchException = PlatformFlowLifecycleException.Infrastructure(
                    "Windows desktop session admission failed before WinUI launch."),
            };
            var driver = new FakeFlowDriver();
            await using var host = new WindowsFlowTestHost(lifecycle, driver);

            var result = await host.RunAsync(
                WindowsTapFlow(),
                WindowsPlan(),
                new WindowsFlowRunRequest
                {
                    ArtifactRoot = root,
                    RunId = "desktop-admission",
                });

            Assert.Equal(MauiFlowRunOutcomes.InfrastructureError, result.Report.Outcome?.Status);
            Assert.Equal(MauiFlowFailureClasses.Infrastructure, result.Report.Failure?.Code);
            Assert.Equal(0, driver.MutationCalls);
            Assert.DoesNotContain(lifecycle.Calls, call => call is "seed" or "verify");

            var manifest = FlowPilotArtifactManifest.Create(new FlowPilotManifestOptions
            {
                ArtifactRoot = root,
                RepositoryRoot = AppFixtureBase.FindRepoRoot(),
                ManifestFileName = "windows-tier1-manifest.json",
                Platform = "windows",
                PackageId = "com.companyname.mauitodo",
                DeviceEvidenceKind = "desktop-host",
            });
            manifest.RecordCleanAttempt(
                WindowsTapFlow(),
                Path.Combine(
                    AppFixtureBase.FindRepoRoot(),
                    "samples",
                    "DevFlow.Sample",
                    "maui-tests",
                    "native-baseline.md"),
                repetition: 1,
                result.Report);

            var write = manifest.WriteAtomic();

            Assert.True(write.Ok, write.Error);
            var firstAttempt = Assert.Single(manifest.Flows).FirstAttempt;
            Assert.NotNull(firstAttempt);
            Assert.Equal("clean", firstAttempt.RunKind);
            Assert.Equal(MauiFlowRunOutcomes.InfrastructureError, firstAttempt.Outcome);
            Assert.Equal(MauiFlowFailureClasses.Infrastructure, firstAttempt.FailureCode);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingPlatformTag_IsCapabilityMissingWithoutSelectorFallback()
    {
        var lifecycle = new FakeLifecycle();
        var driver = new FakeFlowDriver();
        await using var host = new WindowsFlowTestHost(lifecycle, driver);
        var flow = WindowsTapFlow();
        flow.Platform = "android";

        var result = await host.RunAsync(flow, WindowsPlan());

        Assert.Equal(MauiFlowFailureClasses.CapabilityMissing, result.Report.Failure?.Code);
        Assert.Equal(0, driver.MutationCalls);
        Assert.DoesNotContain(lifecycle.Calls, call => call is "build" or "launch" or "seed");
    }

    [Fact]
    public async Task StaleAgentIdentity_IsRejectedBeforeWindowsReplay()
    {
        var statuses = new Queue<AgentStatus?>([
            Status(processId: 42, instanceId: "old"),
            Status(processId: 42, instanceId: "new"),
        ]);

        var ready = await PlatformAgentReadiness.WaitForExpectedAsync(
            _ => Task.FromResult(statuses.Dequeue()),
            new PlatformAgentExpectation
            {
                PackageId = "com.companyname.mauitodo",
                ExpectedProcessId = 42,
                PreviousAgent = new PlatformAgentIdentity("old", 42),
                Timeout = TimeSpan.FromSeconds(1),
            },
            pollInterval: TimeSpan.Zero);

        Assert.Equal("new", ready.Agent.InstanceId);
    }

    [Fact]
    public async Task ReportAndArtifacts_ArePersistedWithNormalizedParity()
    {
        var root = Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "artifacts",
            "TestResults",
            "windows-flow-host-contract",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = new WindowsFlowTestHost(new FakeLifecycle(), new FakeFlowDriver());
            var result = await host.RunAsync(
                WindowsTapFlow(),
                WindowsPlan(),
                new WindowsFlowRunRequest { ArtifactRoot = root, RunId = "windows-contract" });

            Assert.Equal(MauiFlowRunOutcomes.Passed, result.Report.Outcome?.Status);
            Assert.NotNull(result.Report.ReportPath);
            Assert.True(File.Exists(result.Report.ReportPath));
            Assert.Contains(result.Artifacts, artifact => artifact.Kind == "host-diagnostics");

            var android = NormalizeForParity(CreateParityReport("android"));
            var windows = NormalizeForParity(result.Report);
            Assert.Equal(android, windows);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CanonicalRunner_RecordsWindowsCommandReceiptAndNormalizedFailureParity()
    {
        var receiptDriver = new ReceiptDriver();
        var passing = await new MauiFlowRunner(receiptDriver, new MauiFlowRunnerOptions
        {
            RunId = "windows-receipt",
            PollTries = 1,
            PollGapMs = 0,
        }).RunAsync(WindowsTapFlow());

        var receipt = Assert.Single(passing.Steps);
        Assert.Equal("cmd-windows-tap", receipt.CommandId);
        Assert.Equal("prepared", receipt.AcknowledgementState);
        Assert.Equal(1, receipt.CommandSequence);

        var ambiguousFlow = WindowsTapFlow();
        ambiguousFlow.Steps[0].Args!.Selector!.AutomationId = "AddButton";
        var ambiguous = await new MauiFlowRunner(new WindowsFlowQaTests.DuplicateIdentityDriver(), new MauiFlowRunnerOptions
        {
            RunId = "android-golden-ambiguous",
            PollTries = 1,
            PollGapMs = 0,
        }).RunAsync(ambiguousFlow);
        Assert.Equal(MauiFlowFailureClasses.LocatorAmbiguous, ambiguous.Failure?.Code);

        var androidGolden = new MauiFlowRunReport
        {
            Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed, Terminal = true },
            Failure = new MauiFlowFailure { Code = MauiFlowFailureClasses.LocatorAmbiguous },
            DivergenceStepId = "1",
            Steps =
            [
                new MauiFlowStepAttempt
                {
                    Sequence = 1,
                    Action = FlowActions.Tap,
                    FailureClass = MauiFlowFailureClasses.LocatorAmbiguous,
                },
            ],
        };
        Assert.Equal(NormalizeForParity(androidGolden), NormalizeForParity(ambiguous));
    }

    [Fact]
    public void SourceProposalAndTrustContracts_DeclareWindowsCompilationAndRollbackSafety()
    {
        var root = AppFixtureBase.FindRepoRoot();
        var xamlTests = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DevFlow",
            "Microsoft.Maui.DevFlow.Tests",
            "XamlSourceProposalTests.cs"));
        var csharpTests = File.ReadAllText(Path.Combine(
            root,
            "src",
            "DevFlow",
            "Microsoft.Maui.DevFlow.Tests",
            "CSharpSourceProposalTests.cs"));
        Assert.Contains("net10.0-windows10.0.19041.0", xamlTests, StringComparison.Ordinal);
        Assert.Contains("net10.0-windows10.0.19041.0", csharpTests, StringComparison.Ordinal);
        Assert.Contains("Rollback", xamlTests, StringComparison.Ordinal);
        Assert.Contains("Rollback", csharpTests, StringComparison.Ordinal);

        var imported = new MauiArtifactTrustRecord
        {
            Identity = MauiImportedArtifactIdentity.Create(),
            Verification = new MauiArtifactTrustVerificationResult
            {
                State = MauiArtifactTrustStates.Untrusted,
            },
        };
        Assert.False(MauiFlowRepairProposalTrustPolicy.CanCreateProposal(imported).Allowed);
        Assert.False(MauiSourceProposalTrustPolicy.CanCreateProposal(imported).Allowed);
    }

    [Fact]
    public void WindowsSelectorRepair_AbstainsForUnknownCompletionAndPlatformDivergence()
    {
        var checkpoint = new MauiFlowCheckpoint
        {
            AppBuildFingerprint = "sha256:windows-build",
            AgentInstanceId = "windows-agent",
            SeedFingerprint = "sha256:seed",
            BackendStateFingerprint = "sha256:backend",
            Route = "//native",
            Locale = "en-US",
            Theme = "light",
            Orientation = "Landscape",
            DisplayProfile = "1280x720@1",
        };
        var decision = MauiFlowRepairEligibilityEvaluator.Evaluate(new MauiFlowRepairEligibilityInput
        {
            IsCurrentLocalRun = true,
            ExpectedCheckpoint = checkpoint,
            CurrentCheckpoint = checkpoint,
            BeforeDispatch = false,
            Run = new MauiFlowRunReport
            {
                RunId = "windows-unknown-completion",
                Failure = new MauiFlowFailure
                {
                    Code = MauiFlowFailureClasses.UnknownCompletion,
                    Class = MauiFlowFailureClasses.UnknownCompletion,
                    Phase = "dispatch",
                },
            },
        });

        Assert.False(decision.Eligible);
        Assert.Contains(decision.Reasons, reason => reason.Blocking);

        var divergence = File.ReadAllText(Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "tests",
            "DevFlow",
            "InspectorCorpus",
            "cases",
            "mutation-platform-divergence.json"));
        Assert.Contains("\"windowsCandidateKinds\": [\"native-automation-id\"]", divergence, StringComparison.Ordinal);
        Assert.Contains("\"diagnostic-only\"", divergence, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowAndScripts_InvokeTheDedicatedWindowsFlowQaLane()
    {
        var root = AppFixtureBase.FindRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "devflow-integration.yml"));
        var script = File.ReadAllText(Path.Combine(
            root,
            "eng",
            "devflow",
            "Run-DevFlowFlowQa.ps1"));

        Assert.Contains("windows-flow-qa:", workflow, StringComparison.Ordinal);
        Assert.Contains("Run-DevFlowFlowQa.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("--platform windows", workflow, StringComparison.Ordinal);
        Assert.Contains("--repeat 3", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-tier1-manifest.json", File.ReadAllText(Path.Combine(
            root,
            "src",
            "DevFlow",
            "Microsoft.Maui.DevFlow.Agent.IntegrationTests",
            "WindowsFlowQaTests.cs")), StringComparison.Ordinal);
        Assert.Contains("Category=WindowsFlowQa", script, StringComparison.Ordinal);
        Assert.Contains("DEVFLOW_RUN_WINDOWS_FLOW_QA", script, StringComparison.Ordinal);
        Assert.Contains("capability-missing", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureOnlyWindowsManifest_PreservesFirstAttemptAndExplicitOmission()
    {
        var root = Path.Combine(
            AppFixtureBase.FindRepoRoot(),
            "artifacts",
            "TestResults",
            "windows-infrastructure-manifest",
            Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = FlowPilotArtifactManifest.Create(new FlowPilotManifestOptions
            {
                ArtifactRoot = root,
                RepositoryRoot = AppFixtureBase.FindRepoRoot(),
                ManifestFileName = "windows-tier1-manifest.json",
                Platform = "windows",
                PackageId = "com.companyname.mauitodo",
                DeviceEvidenceKind = "desktop-host",
            });
            manifest.RecordInfrastructureFailure(
                WindowsTapFlow(),
                Path.Combine(
                    AppFixtureBase.FindRepoRoot(),
                    "samples",
                    "DevFlow.Sample",
                    "maui-tests",
                    "native-baseline.md"),
                1,
                "windows-agent-readiness");

            var write = manifest.WriteAtomic();

            Assert.True(write.Ok, write.Error);
            Assert.True(write.Validation?.IsValid, string.Join("; ", write.Validation?.Errors ?? []));
            Assert.Equal("infrastructure-error", Assert.Single(manifest.Flows).FirstAttempt?.Outcome);
            Assert.Contains(manifest.Omissions, omission =>
                omission.Reason?.Contains("windows infrastructure", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    static MauiFlow WindowsTapFlow() => new()
    {
        Name = "windows-host-tap",
        Platform = "windows",
        Steps =
        [
            new FlowStep
            {
                Seq = 1,
                Action = FlowActions.Tap,
                Args = new FlowStepArgs { Selector = new FlowSelector { AutomationId = "submit" } },
            },
        ],
    };

    static MauiTestPlan WindowsPlan() => new()
    {
        PlanId = "windows-test-plan",
        SideEffectPolicy = MauiFlowSideEffectPolicies.None,
        RequiredPlatforms = ["android", "windows"],
        Checkpoint = new MauiFlowCheckpointRequirements { Route = "//native" },
    };

    static AgentStatus Status(int processId, string instanceId) => new()
    {
        Agent = new AgentDescriptor { InstanceId = instanceId },
        App = new AppDescriptor { PackageId = "com.companyname.mauitodo", ProcessId = processId },
        Running = true,
    };

    static MauiFlowRunReport CreateParityReport(string platform) => new()
    {
        RunId = $"{platform}-transport-specific",
        Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Passed, Terminal = true },
        Steps =
        [
            new MauiFlowStepAttempt
            {
                Sequence = 1,
                Action = FlowActions.Tap,
            },
        ],
    };

    static string NormalizeForParity(MauiFlowRunReport report)
        => JsonSerializer.Serialize(new
        {
            outcome = report.Outcome?.Status,
            terminal = report.Outcome?.Terminal,
            failure = report.Failure?.Code,
            divergence = report.DivergenceStepId,
            steps = report.Steps.Select(step => new
            {
                step.Sequence,
                step.Action,
                step.FailureClass,
                assertions = step.Assertions.Select(assertion => new
                {
                    assertion.Kind,
                    assertion.Passed,
                    assertion.Skipped,
                }),
            }),
        });

    sealed class ReceiptDriver : IMauiFlowDriver
    {
        readonly ElementInfo _element = new()
        {
            Id = "submit-id",
            AutomationId = "submit",
            Type = "Button",
            IsVisible = true,
            IsEnabled = true,
            Opacity = 1,
            Bounds = new BoundsInfo { Width = 20, Height = 20 },
        };

        public WorkflowCommandReceipt? LastWorkflowCommandReceipt { get; private set; }

        public Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
            => Task.FromResult(
                string.Equals(automationId, "submit", StringComparison.Ordinal)
                    ? new List<ElementInfo> { _element }
                    : []);
        public Task<ElementInfo?> GetElementAsync(string id)
            => Task.FromResult<ElementInfo?>(id == _element.Id ? _element : null);
        public Task<bool> TapAsync(string elementId)
        {
            LastWorkflowCommandReceipt = new WorkflowCommandReceipt
            {
                RunId = "windows-receipt",
                Sequence = 1,
                CommandId = "cmd-windows-tap",
                ActionDigest = "sha256:windows-tap",
                AuthorityEpoch = 1,
            };
            return Task.FromResult(true);
        }
        public Task<bool> FillAsync(string elementId, string text) => Task.FromResult(false);
        public Task<bool> SetPropertyAsync(string elementId, string propertyName, string value) => Task.FromResult(false);
        public Task<bool> ScrollAsync(string? elementId = null, double deltaX = 0, double deltaY = 0, bool animated = true, int? itemIndex = null, string? scrollToPosition = null) => Task.FromResult(false);
        public Task<bool> NavigateAsync(string route) => Task.FromResult(false);
        public Task<bool> BackAsync() => Task.FromResult(false);
        public Task<ThemeResult> SetThemeAsync(DevFlowTheme theme) => Task.FromResult(new ThemeResult { Theme = theme, Success = false });
        public Task<string?> GetPropertyAsync(string elementId, string propertyName) => Task.FromResult<string?>(null);
        public Task<AgentStatus?> GetStatusAsync() => Task.FromResult<AgentStatus?>(new AgentStatus { Running = true });
        public Task<List<ElementInfo>> GetTreeAsync(int maxDepth = 0) => Task.FromResult(new List<ElementInfo> { _element });
    }
}

internal sealed class WindowsFlowQaConfiguration
{
    WindowsFlowQaConfiguration(string repositoryRoot)
    {
        RepositoryRoot = repositoryRoot;
        WorkflowRunId = Environment.GetEnvironmentVariable("DEVFLOW_FLOW_QA_RUN_ID")
            ?? $"local-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        ArtifactRoot = Environment.GetEnvironmentVariable("DEVFLOW_FLOW_QA_ARTIFACT_ROOT")
            ?? Path.Combine(repositoryRoot, "artifacts", "devflow", WorkflowRunId, "windows");
    }

    public string RepositoryRoot { get; }
    public string WorkflowRunId { get; }
    public string ArtifactRoot { get; }

    public FlowPilotManifestOptions ManifestOptions => new()
    {
        ArtifactRoot = ArtifactRoot,
        RepositoryRoot = RepositoryRoot,
        ManifestFileName = "windows-tier1-manifest.json",
        Platform = "windows",
        PackageId = "com.companyname.mauitodo",
        RepositoryCommit = Environment.GetEnvironmentVariable("GITHUB_SHA"),
        WorkflowRunId = WorkflowRunId,
        WorkflowName = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW"),
        WorkflowAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
        DotNetWorkloadVersion = Environment.GetEnvironmentVariable("DOTNET_WORKLOAD_VERSION"),
        DeviceEvidenceKind = "desktop-host",
        RealDevice = false,
    };

    public static WindowsFlowQaConfiguration FromEnvironment(string repositoryRoot)
        => new(repositoryRoot);
}
