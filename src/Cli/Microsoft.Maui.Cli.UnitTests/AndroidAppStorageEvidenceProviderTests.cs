using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Execution;
using Microsoft.Maui.Cli.UnitTests.Fakes;
using Microsoft.Maui.Cli.Utils;
using Microsoft.Maui.DevFlow.Testing;
using Xunit;

namespace Microsoft.Maui.Cli.UnitTests;

/// <summary>
/// Covers the repository's first concrete <c>IFlowStateEvidenceProvider</c>. The behaviour that
/// matters most here is the split between a failed oracle (the app did not commit the record) and
/// an unusable evidence channel (nothing can be certified either way) - collapsing those two would
/// let an infrastructure fault read as a business verdict.
/// </summary>
public class AndroidAppStorageEvidenceProviderTests : IDisposable
{
    private readonly string _sdkRoot;
    private readonly string _adbPath;

    public AndroidAppStorageEvidenceProviderTests()
    {
        _sdkRoot = Path.Combine(Path.GetTempPath(), "devflow-oracle-tests-" + Guid.NewGuid().ToString("N"));
        var platformTools = Path.Combine(_sdkRoot, "platform-tools");
        Directory.CreateDirectory(platformTools);
        _adbPath = Path.Combine(platformTools, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        File.WriteAllText(_adbPath, string.Empty);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_sdkRoot))
                Directory.Delete(_sdkRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Supports_RequiresAndroidArtifactAndDeclaredEvidenceKind()
    {
        var provider = CreateProvider(new QueueRunner());

        Assert.True(provider.Supports(CreateRequest(CreatePlan(), "android")));
        Assert.False(provider.Supports(CreateRequest(CreatePlan(), "ios")));
        Assert.False(provider.Supports(CreateRequest(CreatePlan(evidenceKind: "other-kind"), "android")));
    }

    [Fact]
    public void Supports_IgnoresOraclesThatAreNotRequiredAndIndependent()
    {
        var provider = CreateProvider(new QueueRunner());

        Assert.False(provider.Supports(CreateRequest(CreatePlan(required: false), "android")));
        Assert.False(provider.Supports(CreateRequest(CreatePlan(independent: false), "android")));
    }

    [Fact]
    public async Task PrepareAsync_AddsNoAdmissionObligation()
    {
        var provider = CreateProvider(new QueueRunner());

        var result = await provider.PrepareAsync(CreateRequest(CreatePlan(), "android"));

        Assert.True(result.Supported);
        var context = Assert.IsType<MauiFlowRunContext>(result.RunContext);
        Assert.Equal(MauiFlowReplayIntents.OrdinaryReplay, context.Intent);
        Assert.Empty(context.BusinessOracles ?? []);
        Assert.True(context.PriorMutationCompletionCertain);
    }

    [Theory]
    [InlineData("/data/user/0/pkg/files/ledger.jsonl", "android-app-storage-reference-invalid")]
    [InlineData("../files/ledger.jsonl", "android-app-storage-reference-invalid")]
    [InlineData("files/ledger.jsonl; rm -rf /", "android-app-storage-reference-invalid")]
    [InlineData("files/$(whoami).jsonl", "android-app-storage-reference-invalid")]
    // `cat --help` and `cat -` both exit zero without reading app storage, which would let an
    // absent-only oracle pass vacuously, so option-like references are refused outright.
    [InlineData("--help", "android-app-storage-reference-invalid")]
    [InlineData("-", "android-app-storage-reference-invalid")]
    [InlineData("-n", "android-app-storage-reference-invalid")]
    [InlineData("files/-n", "android-app-storage-reference-invalid")]
    [InlineData("etc/passwd", "android-app-storage-reference-invalid")]
    [InlineData("files", "android-app-storage-reference-invalid")]
    [InlineData("files/ledger.jsonl\n", "android-app-storage-reference-invalid")]
    public async Task PrepareAsync_RefusesUnsafeReference(string reference, string expectedCode)
    {
        var provider = CreateProvider(new QueueRunner());

        var ex = await Assert.ThrowsAsync<FlowExecutionException>(
            () => provider.PrepareAsync(CreateRequest(CreatePlan(reference: reference), "android")));

        Assert.Equal(expectedCode, ex.Code);
    }

    [Theory]
    [InlineData("""{ "notContains": ["x"], "contains": ["\"id\":\"todo-0001\""] }""")]
    [InlineData("""{ "absnet": ["x"], "contains": ["\"id\":\"todo-0001\""] }""")]
    [InlineData("""{ "equals": ["x"], "contains": ["\"id\":\"todo-0001\""] }""")]
    public async Task PrepareAsync_RefusesUnknownExpectationSoAPlanCannotReadStricterThanItChecks(string expect)
    {
        var provider = CreateProvider(new QueueRunner());

        var ex = await Assert.ThrowsAsync<FlowExecutionException>(
            () => provider.PrepareAsync(CreateRequest(CreatePlan(expect: expect), "android")));

        Assert.Equal("android-app-storage-expectation-invalid", ex.Code);
    }

    [Fact]
    public async Task PrepareAsync_RefusesMultiLinePredicate()
    {
        var provider = CreateProvider(new QueueRunner());
        var plan = CreatePlan(expect: """{ "contains": ["first\nsecond"] }""");

        var ex = await Assert.ThrowsAsync<FlowExecutionException>(
            () => provider.PrepareAsync(CreateRequest(plan, "android")));

        Assert.Equal("android-app-storage-expectation-invalid", ex.Code);
    }

    [Fact]
    public async Task PrepareAsync_RefusesOracleThatChecksNothing()
    {
        var provider = CreateProvider(new QueueRunner());
        var plan = CreatePlan(expect: """{ "contains": [] }""");

        var ex = await Assert.ThrowsAsync<FlowExecutionException>(
            () => provider.PrepareAsync(CreateRequest(plan, "android")));

        Assert.Equal("android-app-storage-expectation-missing", ex.Code);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_SucceedsWhenCommittedRecordIsPresent()
    {
        var runner = new QueueRunner(Ok("""{"event":"todo-added","id":"todo-0001"}"""));
        var provider = CreateProvider(runner);

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.True(result.Supported);
        var oracle = Assert.Single(result.BusinessOracles);
        Assert.True(oracle.Succeeded);
        Assert.True(oracle.Independent);
        Assert.Equal("todo-ledger-record", oracle.OracleId);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(_adbPath, call.FileName);
        Assert.Equal(
            ["-s", "emulator-5554", "shell", "run-as", "com.example.app", "cat", "files/ledger.jsonl"],
            call.Arguments);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_FailsWhenRequiredRecordIsAbsent()
    {
        var provider = CreateProvider(new QueueRunner(Ok("""{"event":"todo-added","id":"todo-9999"}""")));

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.True(result.Supported);
        var oracle = Assert.Single(result.BusinessOracles);
        Assert.False(oracle.Succeeded);
        Assert.Contains("expect.contains[0]", oracle.Message);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_FailsWhenForbiddenRecordIsPresent()
    {
        var plan = CreatePlan(expect: """{ "absent": ["todo-removed"] }""");
        var provider = CreateProvider(new QueueRunner(Ok("""{"event":"todo-removed","id":"todo-0001"}""")));

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(plan));

        var oracle = Assert.Single(result.BusinessOracles);
        Assert.False(oracle.Succeeded);
        Assert.Contains("expect.absent[0]", oracle.Message);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_NeverEchoesFileContentIntoTheReport()
    {
        const string Secret = "user-private-note-value";
        var provider = CreateProvider(new QueueRunner(Ok(Secret)));

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        var oracle = Assert.Single(result.BusinessOracles);
        Assert.False(oracle.Succeeded);
        Assert.DoesNotContain(Secret, oracle.Message);
        Assert.DoesNotContain(Secret, oracle.EvidenceReference);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_TreatsMissingFileAsFailedOracleNotBrokenChannel()
    {
        var runner = new QueueRunner(new ProcessResult
        {
            ExitCode = 1,
            StandardError = "cat: files/ledger.jsonl: No such file or directory",
        });
        var provider = CreateProvider(runner);

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.True(result.Supported);
        var oracle = Assert.Single(result.BusinessOracles);
        Assert.False(oracle.Succeeded);
        Assert.Contains("did not commit", oracle.Message);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_DoesNotBlameTheAppWhenAdbItselfCouldNotStart()
    {
        // ProcessRunner reports a launch failure as exit -1 carrying the OS message, which on Unix
        // is literally "No such file or directory" - the app must not be blamed for that.
        var runner = new QueueRunner(new ProcessResult
        {
            ExitCode = -1,
            StandardError = "An error occurred trying to start process 'adb'. No such file or directory",
        });
        var provider = CreateProvider(runner);

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-read-failed", result.DetailCode);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_DoesNotTreatAppContentAsAMissingFileDiagnostic()
    {
        var runner = new QueueRunner(new ProcessResult
        {
            ExitCode = 1,
            StandardOutput = """{"event":"error","detail":"No such file or directory"}""",
            StandardError = "read interrupted",
        });
        var provider = CreateProvider(runner);

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-read-failed", result.DetailCode);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_ReportsUnsupportedWhenEvidenceChannelIsUnusable()
    {
        var runner = new QueueRunner(new ProcessResult
        {
            ExitCode = 1,
            StandardError = "run-as: package not debuggable: com.example.app",
        });
        var provider = CreateProvider(runner);

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-read-failed", result.DetailCode);
        Assert.Empty(result.BusinessOracles);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_RefusesNonAndroidRun()
    {
        var provider = CreateProvider(new QueueRunner());
        var request = CreateEvaluationRequest(CreatePlan()) with { Platform = "ios" };

        var result = await provider.EvaluatePostRunAsync(request);

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-platform-mismatch", result.DetailCode);
    }

    [Theory]
    [InlineData("emulator-5554 && echo pwned", "com.example.app")]
    [InlineData("emulator-5554", "com.example.app; rm -rf /")]
    [InlineData("emulator-5554", "notapackage")]
    [InlineData("-foo", "com.example.app")]
    [InlineData("emulator-5554\n", "com.example.app")]
    [InlineData("emulator-5554", "com.example.app\n")]
    [InlineData("emulator-5554", "")]
    public async Task EvaluatePostRunAsync_RefusesUnsafeAdbTarget(string serial, string packageId)
    {
        var runner = new QueueRunner();
        var provider = CreateProvider(runner);
        var request = CreateEvaluationRequest(CreatePlan()) with
        {
            DeviceSerial = serial,
            PackageId = packageId,
        };

        var result = await provider.EvaluatePostRunAsync(request);

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-target-invalid", result.DetailCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_MatchesPredicatesAgainstDeviceLineEndings()
    {
        // ProcessRunner rejoins device output with the host newline, so on Windows every device
        // "\n" arrives as "\r\n". Predicates are single-line by construction, and normalizing keeps
        // a record that ends at a line boundary matching identically on every host.
        var plan = CreatePlan(expect: """{ "contains": ["{\"id\":\"a\"}"], "absent": ["{\"id\":\"c\"}"] }""");
        var provider = CreateProvider(new QueueRunner(Ok("{\"id\":\"a\"}\r\n{\"id\":\"b\"}\r\n")));

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(plan));

        var oracle = Assert.Single(result.BusinessOracles);
        Assert.True(oracle.Succeeded);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_EchoesRunBindingSoTheEvidenceCannotBeReattributed()
    {
        var provider = CreateProvider(new QueueRunner(Ok("""{"event":"todo-added","id":"todo-0001"}""")));
        var request = CreateEvaluationRequest(CreatePlan());

        var result = await provider.EvaluatePostRunAsync(request);

        Assert.Equal(request.RunId, result.RunId);
        Assert.Equal(request.FlowDigest, result.FlowDigest);
        Assert.Equal(request.DeviceIdentityFingerprint, result.DeviceIdentityFingerprint);
        Assert.Equal(request.AppBuildFingerprint, result.AppBuildFingerprint);
        Assert.Equal(request.PackageDigest, result.PackageDigest);
        Assert.Equal(request.StartedAt, result.StartedAt);
        Assert.Equal(request.EndedAt, result.EndedAt);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_ReportsUnsupportedWhenAdbIsNotInstalled()
    {
        var provider = new AndroidAppStorageEvidenceProvider(
            new FakeAndroidProvider { SdkPath = Path.Combine(_sdkRoot, "missing") },
            new QueueRunner());

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.False(result.Supported);
        Assert.Equal("android-adb-not-found", result.DetailCode);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_RefusesEvidenceLargerThanTheBoundedReadLimit()
    {
        var provider = CreateProvider(new QueueRunner(Ok(new string('x', (256 * 1024) + 1))));

        var result = await provider.EvaluatePostRunAsync(CreateEvaluationRequest(CreatePlan()));

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-evidence-too-large", result.DetailCode);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_RefusesToReadAfterTheEvaluationWindowClosed()
    {
        var runner = new QueueRunner();
        var provider = CreateProvider(runner);
        var request = CreateEvaluationRequest(CreatePlan()) with
        {
            EvaluationDeadline = DateTimeOffset.UnixEpoch,
        };

        var result = await provider.EvaluatePostRunAsync(request);

        Assert.False(result.Supported);
        Assert.Equal("android-app-storage-deadline-elapsed", result.DetailCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task EvaluatePostRunAsync_BoundsTheReadByTheRemainingEvaluationWindow()
    {
        var runner = new QueueRunner(Ok("""{"event":"todo-added","id":"todo-0001"}"""));
        var provider = CreateProvider(runner);
        var request = CreateEvaluationRequest(CreatePlan()) with
        {
            EvaluationDeadline = TimeProvider.System.GetUtcNow().AddSeconds(2),
        };

        var result = await provider.EvaluatePostRunAsync(request);

        Assert.True(result.Supported);
        Assert.True(runner.Timeouts[0] <= TimeSpan.FromSeconds(2));
    }

    private AndroidAppStorageEvidenceProvider CreateProvider(IExecutionProcessRunner runner)
        => new(new FakeAndroidProvider { SdkPath = _sdkRoot }, runner);

    private static ProcessResult Ok(string standardOutput)
        => new() { ExitCode = 0, StandardOutput = standardOutput };

    private static MauiTestPlan CreatePlan(
        string evidenceKind = "android-app-storage",
        string reference = "files/ledger.jsonl",
        bool required = true,
        bool independent = true,
        string expect = """{ "contains": ["\"id\":\"todo-0001\""] }""")
    {
        var declaration = new MauiIndependentBusinessOracleDeclaration
        {
            OracleId = "todo-ledger-record",
            Required = required,
            Independent = independent,
            EvidenceKind = evidenceKind,
            Reference = reference,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["expect"] = JsonDocument.Parse(expect).RootElement.Clone(),
            },
        };

        return new MauiTestPlan
        {
            Schema = 1,
            PlanId = "plan-oracle-test",
            IndependentBusinessOracles = [declaration],
        };
    }

    private static FlowStateEvidenceRequest CreateRequest(MauiTestPlan plan, string platform) => new()
    {
        Plan = plan,
        Flow = new MauiFlow { Schema = 2, Name = "flow" },
        Artifact = CreateArtifact(platform),
    };

    private static FlowPostRunOracleEvaluationRequest CreateEvaluationRequest(MauiTestPlan plan) => new()
    {
        Plan = plan,
        Flow = new MauiFlow { Schema = 2, Name = "flow" },
        Artifact = CreateArtifact("android"),
        RunId = "run-1",
        FlowDigest = "digest-1",
        DeviceIdentityFingerprint = "device-fingerprint",
        AppBuildFingerprint = "build-fingerprint",
        PackageDigest = "package-digest",
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(5),
        EvaluationDeadline = DateTimeOffset.MaxValue,
        Report = new MauiFlowRunReport
        {
            Outcome = new MauiFlowRunOutcome { Status = "passed", Terminal = true },
        },
        Platform = "android",
        DeviceSerial = "emulator-5554",
        PackageId = "com.example.app",
    };

    private static ResolvedAppArtifact CreateArtifact(string platform) => new()
    {
        Path = "app.apk",
        ProjectPath = "app.csproj",
        AgentSessionId = "session",
        TargetFramework = "net10.0-" + platform,
        TargetPlatformIdentifier = platform,
        Configuration = "Debug",
        ArtifactType = "apk",
        PackageDigest = "package-digest",
    };

    private sealed class QueueRunner(params ProcessResult[] results) : IExecutionProcessRunner
    {
        private readonly Queue<ProcessResult> _results = new(results);

        public List<(string FileName, string[] Arguments)> Calls { get; } = [];

        public List<TimeSpan?> Timeouts { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            IEnumerable<string>? environmentVariablesToRemove = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments.ToArray()));
            Timeouts.Add(timeout);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new ProcessResult { ExitCode = 0 });
        }
    }
}
