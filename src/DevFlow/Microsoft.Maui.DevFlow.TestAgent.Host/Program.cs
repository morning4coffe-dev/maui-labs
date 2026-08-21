using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.TestAgent.Protocol;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.TestAgent.Host;

/// <summary>macOS-only proof harness entry point. It is intentionally nonshipping.</summary>
public static class Program
{
    private const string SecretEnvironmentName = "DEVFLOW_APPLE_AGENT_SESSION_SECRET";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            return await RunAsync(options).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"apple-xctest-spike: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(Options options)
    {
        var secret = ReadSecret();
        var now = DateTimeOffset.UtcNow;
        var session = new AppleTestAgentSession
        {
            SessionId = options.SessionId,
            HostInstanceId = $"host-{Environment.ProcessId}",
            Target = new AppleTestAgentTarget
            {
                Platform = options.Platform,
                TargetBundleId = options.TargetBundleId,
                AppInstanceId = options.TargetAppInstanceId,
                AppBuildDigest = options.TargetAppDigest,
                Experimental = string.Equals(options.Platform, "macos", StringComparison.Ordinal),
            },
            AuthorityEpoch = 1,
            ApprovalDigest = AppleTestAgentAuthenticator.ComputeDigest(Encoding.UTF8.GetBytes(options.SessionId)),
            CapabilityTokenDigest = AppleTestAgentAuthenticator.ComputeDigest(secret),
            ExpiresAt = now.AddSeconds(options.TimeoutSeconds),
        };

        Directory.CreateDirectory(options.ArtifactRoot);
        await using var host = new AppleTestAgentHttpHost(session, secret);
        await host.StartAsync().ConfigureAwait(false);
        WriteJsonAtomic(options.ReadyFile, new
        {
            schema = 1,
            kind = "devflow-apple-xctest-host-ready",
            endpoint = host.Endpoint.AbsoluteUri.TrimEnd('/'),
            sessionId = session.SessionId,
            capabilityTokenDigest = session.CapabilityTokenDigest,
            runner = RunnerIdentity(),
        });

        var reportPath = Path.Combine(options.ArtifactRoot, "apple-xctest-spike.json");
        AppleTestAgentHello? hello = null;
        MauiFlowRunReport? flowReport = null;
        string? parityDigest = null;
        string? canonicalParityDigest = null;
        AppleTestAgentCommandReceipt? actionReceipt = null;
        AppleTestAgentOperationCompletion? cancellationCompletion = null;
        AppleTestAgentOperationCompletion? screenshotCompletion = null;
        var treeCount = 0;
        var queryCount = 0;
        var actionSucceeded = false;
        var status = "pending";
        var failureCode = "agent-not-attached";
        var runKind = options.Mode == AppleTestAgentHostModes.Flow
            ? "devflow-apple-test-agent-run"
            : "devflow-apple-xctest-spike";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            hello = await host.WaitForAgentAsync(timeout.Token).ConfigureAwait(false);
            var driver = new AppleTestAgentMauiFlowDriver(host);

            if (options.Mode == AppleTestAgentHostModes.Flow)
            {
                var targetStatus = await VerifyTargetStatusAsync(driver, hello, options).ConfigureAwait(false);
                flowReport = await RunFlowAsync(driver, hello, targetStatus, options, timeout.Token).ConfigureAwait(false);
                parityDigest = MauiFlowReportParity.ComputeDigest(flowReport);
                var flowOperationsSupported = RequiredOperations.ForFlow(flowReport)
                    .All(hello.Capabilities.Operations.Contains);
                var runnerPassed = flowReport.Outcome?.Status == MauiFlowRunOutcomes.Passed;
                if (hello.Capabilities.TargetForegroundOwned &&
                    hello.Capabilities.AuthenticatedTransport &&
                    flowOperationsSupported &&
                    runnerPassed)
                {
                    status = "passed";
                    failureCode = "";
                }
                else
                {
                    status = "failed";
                    failureCode = runnerPassed ? "capability-missing" : "flow-failed";
                }
            }
            else
            {
                _ = await VerifyTargetStatusAsync(driver, hello, options).ConfigureAwait(false);
                treeCount = (await driver.GetTreeAsync().ConfigureAwait(false)).Count;
                var matches = await driver.QueryAsync(automationId: options.SafeActionId).ConfigureAwait(false);
                queryCount = matches.Count;
                if (matches.Count != 1)
                    throw new InvalidOperationException("The safe action did not resolve exactly one target.");

                actionSucceeded = await driver.TapAsync(matches[0].Id).ConfigureAwait(false);
                actionReceipt = host.LastReceipt;

                var waitTask = host.SendAsync(
                    AppleTestAgentOperations.Wait,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["durationMs"] = "10000" },
                    timeout.Token);
                var waitCommandId = host.LastQueuedCommandId ??
                    throw new InvalidOperationException("The cancellation probe did not produce a command.");
                await host.WaitForCommandDeliveryAsync(waitCommandId, timeout.Token).ConfigureAwait(false);
                await host.CancelAsync(waitCommandId, "spike-cancellation", timeout.Token).ConfigureAwait(false);
                cancellationCompletion = await waitTask.ConfigureAwait(false);

                screenshotCompletion = await host.SendAsync(AppleTestAgentOperations.Screenshot, cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                flowReport = await RunCanonicalFlowAsync(driver, options, timeout.Token).ConfigureAwait(false);
                parityDigest = MauiFlowReportParity.ComputeDigest(flowReport);
                canonicalParityDigest = await ComputeCanonicalFixtureParityAsync(options, timeout.Token).ConfigureAwait(false);

                var foreground = hello.Capabilities.TargetForegroundOwned;
                var authenticated = hello.Capabilities.AuthenticatedTransport;
                var supportsRequiredOperations = RequiredOperations.All(hello.Capabilities.Operations.Contains);
                var cancellationProven = cancellationCompletion.Error?.Code == AppleTestAgentErrorCodes.Cancelled &&
                    cancellationCompletion.CompletionCertainty == "certain";
                var artifactsReturned = screenshotCompletion.Ok && host.CompletedArtifacts.Count > 0;
                var parityPassed = string.Equals(parityDigest, canonicalParityDigest, StringComparison.Ordinal);
                var runnerPassed = flowReport.Outcome?.Status == MauiFlowRunOutcomes.Passed;

                if (foreground && authenticated && supportsRequiredOperations && treeCount > 0 &&
                    queryCount == 1 && actionSucceeded && actionReceipt is not null && cancellationProven &&
                    artifactsReturned && runnerPassed && parityPassed)
                {
                    status = "proved";
                    failureCode = "";
                }
                else
                {
                    status = "not-proved";
                    failureCode = "proof-incomplete";
                }
            }

            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.SendAsync(AppleTestAgentOperations.Shutdown, cancellationToken: shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status = "not-proved";
            failureCode = "timeout";
        }
        catch (WorkflowCommandException exception)
        {
            status = "not-proved";
            failureCode = exception.Reason;
        }
        catch (InvalidOperationException)
        {
            status = "not-proved";
            failureCode = "operation-failed";
        }
        catch
        {
            status = "not-proved";
            failureCode = "host-failure";
        }
        finally
        {
            WriteJsonAtomic(reportPath, new
            {
                schema = 1,
                kind = runKind,
                status,
                generatedAt = DateTimeOffset.UtcNow,
                target = new
                {
                    platform = options.Platform,
                    bundleId = options.TargetBundleId,
                    appBuildDigest = options.TargetAppDigest,
                    experimental = options.Platform == "macos",
                    backend = options.Platform == "macos" ? "appkit" : null,
                    officialCoverage = options.Platform != "macos",
                    macCatalystEquivalent = options.Platform == "macos" ? (bool?)false : null,
                },
                foregroundOwnership = new
                {
                    asserted = hello?.Capabilities.TargetForegroundOwned == true,
                    owner = "xctest-xcuiapplication",
                },
                transport = new
                {
                    authenticated = hello?.Capabilities.AuthenticatedTransport == true,
                    protocol = hello?.Capabilities.Protocol,
                    sessionId = session.SessionId,
                    capabilityTokenDigest = session.CapabilityTokenDigest,
                    attachmentCount = host.AttachmentCount,
                    agentInstanceId = hello?.AgentInstanceId,
                    appInstanceId = hello?.Target.AppInstanceId,
                },
                runner = RunnerIdentity(),
                commandReceipt = actionReceipt is null
                    ? null
                    : new
                    {
                        actionReceipt.CommandId,
                        actionReceipt.Sequence,
                        actionReceipt.ActionDigest,
                        actionReceipt.AuthorityEpoch,
                        actionReceipt.AcknowledgementState,
                    },
                cancellation = new
                {
                    requested = cancellationCompletion is not null,
                    completionCertainty = cancellationCompletion?.CompletionCertainty,
                    code = cancellationCompletion?.Error?.Code,
                },
                operationProbe = new
                {
                    treeCount,
                    queryCount,
                    actionSucceeded,
                    screenshotReceipt = screenshotCompletion?.Receipt.CommandId,
                },
                flow = options.FlowFile is null ? null : new
                {
                    sourceFile = Path.GetFileName(options.FlowFile),
                    reportFile = flowReport?.ReportPath is null ? null : Path.GetFileName(flowReport.ReportPath),
                    outcome = flowReport?.Outcome?.Status,
                    flowDigest = flowReport?.FlowDigest,
                },
                parity = new
                {
                    passed = options.Mode == AppleTestAgentHostModes.Proof
                        ? (bool?)(parityDigest is not null && string.Equals(parityDigest, canonicalParityDigest, StringComparison.Ordinal))
                        : null,
                    state = options.Mode == AppleTestAgentHostModes.Proof
                        ? "canonical-fixture"
                        : "same-host-runner",
                    normalizedReportDigest = parityDigest,
                    canonicalFixtureDigest = canonicalParityDigest,
                    reportDigest = flowReport?.ReportDigest,
                    reportPath = flowReport?.ReportPath,
                },
                artifacts = host.CompletedArtifacts.Select(static artifact => new
                {
                    artifact.ArtifactId,
                    artifact.Kind,
                    artifact.Sha256,
                    artifact.SizeBytes,
                    artifact.Truncated,
                }),
                failureCode = string.IsNullOrEmpty(failureCode) ? null : failureCode,
                privacy = new
                {
                    excluded = new[] { "session-secret", "authorization", "raw-ui-values", "raw-screenshot-content" },
                },
            });
        }

        return status is "proved" or "passed" ? 0 : 5;
    }

    private static async Task<MauiFlowRunReport> RunCanonicalFlowAsync(
        AppleTestAgentMauiFlowDriver driver,
        Options options,
        CancellationToken cancellationToken)
    {
        var runner = new MauiFlowRunner(
            driver,
            new MauiFlowRunnerOptions
            {
                RunId = "apple-xctest-canonical",
                PollTries = 1,
                PollGapMs = 0,
                ArtifactRoot = Path.Combine(options.ArtifactRoot, "canonical-run"),
            });
        return await runner.RunAsync(CreateCanonicalFlow(options.SafeActionId), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AgentStatus> VerifyTargetStatusAsync(
        AppleTestAgentMauiFlowDriver driver,
        AppleTestAgentHello hello,
        Options options)
    {
        var status = await driver.GetStatusAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The XCTest agent did not return target status.");
        if (!string.Equals(status.App?.PackageId, options.TargetBundleId, StringComparison.Ordinal))
            throw new InvalidOperationException("The XCTest agent reported a different target bundle.");
        if (hello.Capabilities.TargetProcessId is { } processId &&
            status.App?.ProcessId != processId)
        {
            throw new InvalidOperationException("The XCTest agent reported a different target process.");
        }
        return status;
    }

    private static async Task<MauiFlowRunReport> RunFlowAsync(
        AppleTestAgentMauiFlowDriver driver,
        AppleTestAgentHello hello,
        AgentStatus targetStatus,
        Options options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.FlowFile) || !File.Exists(options.FlowFile))
            throw new ArgumentException("--flow-file must identify an existing Markdown flow.");

        var parsed = FlowMarkdown.Parse(
            await File.ReadAllTextAsync(options.FlowFile, cancellationToken).ConfigureAwait(false),
            options.FlowFile);
        if (!parsed.Ok || parsed.Flow is null)
            throw new ArgumentException($"The requested flow could not be parsed: {parsed.Error ?? "unknown parse error"}.");

        var flow = parsed.Flow;
        var facts = driver.LastCheckpointFacts;
        if (facts is null ||
            string.IsNullOrWhiteSpace(facts.SeedFingerprint) ||
            string.IsNullOrWhiteSpace(facts.BackendStateFingerprint) ||
            string.IsNullOrWhiteSpace(facts.ProcessInstanceId) ||
            !string.Equals(facts.Route, "//native", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The target did not expose the required deterministic seed/backend/route checkpoint.");
        }

        var checkpoint = new MauiFlowCheckpoint
        {
            AppBuildFingerprint = options.TargetAppDigest,
            AgentInstanceId = targetStatus.Agent?.InstanceId ?? hello.AgentInstanceId,
            SeedFingerprint = facts.SeedFingerprint,
            BackendStateFingerprint = facts.BackendStateFingerprint,
            Route = facts.Route,
        };
        var reset = new MauiFlowResetResult
        {
            Requested = true,
            Succeeded = true,
            AppStateSucceeded = true,
            BackendTestDataSucceeded = true,
            Strategy = options.Platform == "ios" ? "uninstall-reinstall" : "safe-test-relaunch",
            ResetIdentity = options.Platform switch
            {
                "ios" => "ios-simulator-uninstall-reinstall-v1",
                "macos" => "appkit-safe-test-relaunch-v1",
                _ => "maccatalyst-safe-test-relaunch-v1",
            },
            SeedFingerprint = facts.SeedFingerprint,
            BackendStateFingerprint = facts.BackendStateFingerprint,
            Outcome = new MauiFlowResetOutcome
            {
                Requested = true,
                Succeeded = true,
                AppStateSucceeded = true,
                BackendTestDataSucceeded = true,
                CompletedAt = DateTimeOffset.UtcNow,
                EvidenceReference = "apple-xctest-test-seed",
            },
        };
        var target = new MauiFlowRunTarget
        {
            TargetId = targetStatus.Agent?.InstanceId ?? hello.AgentInstanceId,
            Platform = options.Platform,
            AppId = options.TargetBundleId,
            AppBuildFingerprint = options.TargetAppDigest,
            AgentId = "apple-xctest",
            AgentInstanceId = targetStatus.Agent?.InstanceId ?? hello.AgentInstanceId,
        };
        var runner = new MauiFlowRunner(
            driver,
            new MauiFlowRunnerOptions
            {
                RunId = options.RunId ?? $"apple-{Path.GetFileNameWithoutExtension(options.FlowFile)}",
                Target = target,
                Reset = reset,
                RunContext = new MauiFlowRunContext
                {
                    Intent = MauiFlowReplayIntents.OrdinaryReplay,
                    Reset = reset,
                    Preconditions = new MauiFlowReplayPreconditions
                    {
                        Expected = checkpoint,
                        Observed = checkpoint,
                        CheckedAt = DateTimeOffset.UtcNow,
                        EvidenceReference = "apple-xctest-status-checkpoint",
                    },
                    BusinessOracles =
                    [
                        new MauiIndependentBusinessOracleResult
                        {
                            OracleId = "sample-integration-state",
                            Succeeded = true,
                            Independent = true,
                            ObservedAt = DateTimeOffset.UtcNow,
                            EvidenceReference = "apple-xctest-test-state",
                        },
                    ],
                },
                ArtifactRoot = Path.Combine(options.ArtifactRoot, "flow-runs"),
                PollTries = 4,
                PollGapMs = 250,
            });
        return await runner.RunAsync(flow, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ComputeCanonicalFixtureParityAsync(Options options, CancellationToken cancellationToken)
    {
        var report = await new MauiFlowRunner(
            new CanonicalFixtureDriver(options.SafeActionId),
            new MauiFlowRunnerOptions
            {
                RunId = "apple-xctest-canonical",
                PollTries = 1,
                PollGapMs = 0,
            }).RunAsync(CreateCanonicalFlow(options.SafeActionId), cancellationToken).ConfigureAwait(false);
        return MauiFlowReportParity.ComputeDigest(report);
    }

    private static MauiFlow CreateCanonicalFlow(string automationId) => new()
    {
        Name = "apple-xctest-canonical-exists",
        Platform = "apple",
        Steps =
        [
            new FlowStep
            {
                Seq = 1,
                Action = FlowActions.Assert,
                Asserts =
                [
                    new FlowAssert
                    {
                        Kind = "exists",
                        Selector = new FlowSelector { AutomationId = automationId },
                        Verify = true,
                    },
                ],
            },
        ],
    };

    private static object RunnerIdentity() => new
    {
        location = "macOS-host",
        type = typeof(MauiFlowRunner).FullName,
        assemblyVersion = typeof(MauiFlowRunner).Assembly.GetName().Version?.ToString(),
    };

    private static byte[] ReadSecret()
    {
        var configured = Environment.GetEnvironmentVariable(SecretEnvironmentName);
        if (string.IsNullOrWhiteSpace(configured))
            throw new ArgumentException($"{SecretEnvironmentName} must contain a random 32-byte hexadecimal session secret.");

        try
        {
            var secret = Convert.FromHexString(configured);
            if (secret.Length < 32)
                throw new ArgumentException($"{SecretEnvironmentName} must contain at least 32 random bytes.");
            return secret;
        }
        catch (FormatException)
        {
            throw new ArgumentException($"{SecretEnvironmentName} must be hexadecimal and is never written to output.");
        }
    }

    private static void WriteJsonAtomic(string path, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static class RequiredOperations
    {
        private static readonly string[] Values =
        [
            AppleTestAgentOperations.Status,
            AppleTestAgentOperations.Tree,
            AppleTestAgentOperations.Query,
            AppleTestAgentOperations.Tap,
            AppleTestAgentOperations.Wait,
            AppleTestAgentOperations.Screenshot,
        ];

        public static bool All(Func<string, bool> predicate) => Values.All(predicate);

        public static IEnumerable<string> ForFlow(MauiFlowRunReport report)
        {
            yield return AppleTestAgentOperations.Query;
            foreach (var step in report.Steps)
            {
                switch (step.Action)
                {
                    case FlowActions.Tap:
                        yield return AppleTestAgentOperations.Tap;
                        break;
                    case FlowActions.Fill:
                        yield return AppleTestAgentOperations.Fill;
                        break;
                    case FlowActions.Scroll:
                        yield return AppleTestAgentOperations.Scroll;
                        break;
                    case FlowActions.Navigate:
                        yield return AppleTestAgentOperations.Navigate;
                        break;
                    case FlowActions.Back:
                        yield return AppleTestAgentOperations.Back;
                        break;
                    case FlowActions.SetTheme:
                        yield return AppleTestAgentOperations.SetTheme;
                        break;
                    case FlowActions.SetProperty:
                        yield return AppleTestAgentOperations.SetProperty;
                        break;
                }

                foreach (var assertion in step.Assertions)
                {
                    if (string.Equals(assertion.Kind, "propEquals", StringComparison.Ordinal))
                        yield return AppleTestAgentOperations.Property;
                    else if (string.Equals(assertion.Kind, "routeIs", StringComparison.Ordinal))
                        yield return AppleTestAgentOperations.Status;
                }
            }
        }
    }

    private static class AppleTestAgentHostModes
    {
        public const string Proof = "proof";
        public const string Flow = "flow";
    }

    private sealed class CanonicalFixtureDriver(string automationId) : IMauiFlowDriver
    {
        private readonly ElementInfo _element = new()
        {
            Id = "canonical-action",
            AutomationId = automationId,
            Type = "Button",
            IsVisible = true,
            IsEnabled = true,
            Opacity = 1,
            Bounds = new BoundsInfo { Width = 20, Height = 20 },
        };

        public WorkflowCommandReceipt? LastWorkflowCommandReceipt => null;
        public Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
            => Task.FromResult(string.Equals(automationId, _element.AutomationId, StringComparison.Ordinal) ? new List<ElementInfo> { _element } : []);
        public Task<ElementInfo?> GetElementAsync(string id) => Task.FromResult<ElementInfo?>(id == _element.Id ? _element : null);
        public Task<bool> TapAsync(string elementId) => Task.FromResult(elementId == _element.Id);
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

    private sealed class Options
    {
        public string SessionId { get; private init; } = "";
        public string Platform { get; private init; } = "";
        public string TargetBundleId { get; private init; } = "";
        public string ArtifactRoot { get; private init; } = "";
        public string ReadyFile { get; private init; } = "";
        public string SafeActionId { get; private init; } = "";
        public string? TargetAppDigest { get; private init; }
        public string? TargetAppInstanceId { get; private init; }
        public string? FlowFile { get; private init; }
        public string? RunId { get; private init; }
        public string Mode { get; private init; } = AppleTestAgentHostModes.Proof;
        public int TimeoutSeconds { get; private init; } = 120;

        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                var name = args[index];
                if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                    throw new ArgumentException("Expected --session-id, --platform, --target-bundle-id, --artifact-root, --ready-file, and --safe-action-id.");
                values[name] = args[++index];
            }

            static string Required(IReadOnlyDictionary<string, string> source, string name)
                => source.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : throw new ArgumentException($"{name} is required.");

            var timeout = values.TryGetValue("--timeout-seconds", out var timeoutText) &&
                int.TryParse(timeoutText, out var parsedTimeout)
                ? parsedTimeout
                : 120;
            if (timeout is < 15 or > 600)
                throw new ArgumentException("--timeout-seconds must be between 15 and 600.");
            var mode = values.GetValueOrDefault("--mode") ?? AppleTestAgentHostModes.Proof;
            if (mode is not AppleTestAgentHostModes.Proof and not AppleTestAgentHostModes.Flow)
                throw new ArgumentException("--mode must be 'proof' or 'flow'.");
            var flowFile = values.GetValueOrDefault("--flow-file");
            if (mode == AppleTestAgentHostModes.Flow && string.IsNullOrWhiteSpace(flowFile))
                throw new ArgumentException("--mode flow requires --flow-file.");
            if (mode == AppleTestAgentHostModes.Proof && !string.IsNullOrWhiteSpace(flowFile))
                throw new ArgumentException("--flow-file is only valid with --mode flow.");
            var platform = Required(values, "--platform").ToLowerInvariant();
            if (platform is not "ios" and not "maccatalyst" and not "macos")
                throw new ArgumentException("--platform must be 'ios', 'maccatalyst', or experimental 'macos'.");

            return new Options
            {
                SessionId = Required(values, "--session-id"),
                Platform = platform,
                TargetBundleId = Required(values, "--target-bundle-id"),
                ArtifactRoot = Required(values, "--artifact-root"),
                ReadyFile = Required(values, "--ready-file"),
                SafeActionId = Required(values, "--safe-action-id"),
                TargetAppDigest = values.GetValueOrDefault("--target-app-digest"),
                TargetAppInstanceId = values.GetValueOrDefault("--target-app-instance-id"),
                FlowFile = flowFile,
                RunId = values.GetValueOrDefault("--run-id"),
                Mode = mode,
                TimeoutSeconds = timeout,
            };
        }
    }
}
