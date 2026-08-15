using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class DevFlowFailurePublisherScriptTests : IDisposable
{
    private const string Repository = "dotnet/maui-labs";
    private const string WorkflowName = "DevFlow Integration Tests";
    private const string WorkflowPath = ".github/workflows/devflow-integration.yml";
    private const string SourceEvent = "schedule";
    private const string HeadRef = "main";
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";
    private const long RunId = 123456789;
    private const int RunAttempt = 2;
    private const int PullRequestNumber = 0;

    private readonly string _repositoryRoot = FindRepositoryRoot();
    private readonly string _testRoot;

    public DevFlowFailurePublisherScriptTests()
    {
        _testRoot = Path.Combine(
            _repositoryRoot,
            "artifacts",
            "TestResults",
            "devflow-failure-publisher-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [Fact]
    public async Task VerifyOnly_ValidQualifiedFailure_ReturnsStableSafeFingerprint()
    {
        var archive = CreateArchive();

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("verified", result.Json.GetProperty("status").GetString());
        Assert.Matches("^sha256:[0-9a-f]{64}$", result.Json.GetProperty("fingerprint").GetString());
    }

    [Theory]
    [InlineData("pass", "qualified", "sufficient", "ignored-pass")]
    [InlineData("pending", "pending", "partial", "ignored-pending")]
    [InlineData("failure", "not-qualified", "partial", "ignored-not-qualified")]
    [InlineData("failure", "qualified", "insufficient", "ignored-not-qualified")]
    public async Task VerifyOnly_NonPublishableDisposition_PerformsReadOnlyNoOp(
        string outcome,
        string qualification,
        string evidenceSufficiency,
        string expectedStatus)
    {
        var archive = CreateArchive(
            outcome: outcome,
            qualification: qualification,
            evidenceSufficiency: evidenceSufficiency);

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedStatus, result.Json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task VerifyOnly_DeclaredHashMismatch_RejectsArchive()
    {
        var archive = CreateArchive(declaredSha256: $"sha256:{new string('0', 64)}");

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-malformed", result.Json.GetProperty("status").GetString());
        Assert.Equal("handoff-declaration-invalid", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_ManifestEntryObjectMasqueradingAsArray_IsRejected()
    {
        var archive = CreateArchive(manifestEntriesAsObject: true);

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-malformed", result.Json.GetProperty("status").GetString());
        Assert.Equal("manifest-schema-invalid", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_UnsafeJsonInteger_IsRejectedWithoutCasting()
    {
        var archive = CreateArchive(handoffRunId: 9_007_199_254_740_992L);

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-unverifiable", result.Json.GetProperty("status").GetString());
        Assert.Equal("provenance-mismatch", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_ProvenanceMismatch_RejectsAsUnverifiable()
    {
        var archive = CreateArchive(repository: "other/repository");

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-unverifiable", result.Json.GetProperty("status").GetString());
        Assert.Equal("provenance-mismatch", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_PullRequestEnvelope_IsDiagnosticOnly()
    {
        const string forkRepository = "contributor/maui-labs";
        const string pullRequestRef = "feature/security";
        const int pullRequestNumber = 456;
        var archive = CreateArchive(
            sourceEvent: "pull_request",
            headRepository: forkRepository,
            headRef: pullRequestRef,
            pullRequestNumber: pullRequestNumber);

        var result = await RunVerifierAsync(
            archive,
            sourceEvent: "pull_request",
            headRepository: forkRepository,
            headRef: pullRequestRef,
            pullRequestNumber: pullRequestNumber);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("verified-diagnostic-only", result.Json.GetProperty("status").GetString());
        Assert.Equal("source-event-not-publishable", result.Json.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(2, 1, "manifest-schema-invalid")]
    [InlineData(1, 2, "handoff-schema-invalid")]
    public async Task VerifyOnly_UnsupportedSchemaVersion_RejectsArchive(
        int manifestVersion,
        int handoffVersion,
        string expectedReason)
    {
        var archive = CreateArchive(
            manifestVersion: manifestVersion,
            handoffVersion: handoffVersion);

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-malformed", result.Json.GetProperty("status").GetString());
        Assert.Equal(expectedReason, result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_UnexpectedTraversingEntry_RejectsArchive()
    {
        var archive = CreateArchive(replaceHandoffName: "../handoff.json");

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-malformed", result.Json.GetProperty("status").GetString());
        Assert.Equal("entry-path-invalid", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_DuplicateEntry_RejectsArchive()
    {
        var archive = Path.Combine(_testRoot, "duplicate.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "handoff.json", "{}");
            WriteEntry(zip, "handoff.json", "{}");
        }

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-malformed", result.Json.GetProperty("status").GetString());
        Assert.Equal("entry-name-duplicate", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task VerifyOnly_CompressionBombRatio_RejectsArchive()
    {
        var archive = Path.Combine(_testRoot, "compression-ratio.zip");
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", new string('a', 100_000));
            WriteEntry(zip, "handoff.json", "{}");
        }

        var result = await RunVerifierAsync(archive);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-malformed", result.Json.GetProperty("status").GetString());
        Assert.Equal("entry-compression-ratio-exceeded", result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task PublishMode_UsesBearerTokenAndSafelyCreatesReopensCommentsAndRejectsAmbiguity()
    {
        const string token = "publisher-test-token";
        using var server = new MockGitHubApiServer { LabelExists = false };

        var firstArchive = CreateArchive(runId: RunId, runAttempt: RunAttempt);
        server.AddRun(RunId, RunAttempt, firstArchive);
        var created = await RunPublisherAsync(
            server,
            token,
            RunId,
            RunAttempt,
            SourceEvent,
            Repository,
            HeadRef,
            PullRequestNumber);

        Assert.True(
            created.ExitCode == 0,
            $"stdout={created.StandardOutput}; stderr={created.StandardError}; requests={string.Join(", ", server.Requests.Select(static request => $"{request.Method} {request.PathAndQuery}"))}");
        Assert.Equal("created", created.Json.GetProperty("status").GetString());
        var issue = Assert.Single(server.Issues);
        Assert.Contains(
            server.Requests,
            request => request.Method == "POST" &&
                request.PathAndQuery == $"/repos/{Repository}/labels");
        Assert.Contains("devflow-ci-failure", issue.Labels);
        Assert.Matches(
            "^<!-- devflow-ci-failure:v1 fingerprint=sha256:[0-9a-f]{64} body=sha256:[0-9a-f]{64} -->\n",
            issue.Body);

        var trustedBody = issue.Body;
        issue.Body += "\nmarker-squatting-tamper";
        var recurrenceRunId = RunId + 1;
        var recurrenceArchive = CreateArchive(runId: recurrenceRunId, runAttempt: 1);
        server.AddRun(recurrenceRunId, 1, recurrenceArchive);
        var tampered = await RunPublisherAsync(
            server,
            token,
            recurrenceRunId,
            1,
            SourceEvent,
            Repository,
            HeadRef,
            PullRequestNumber);

        Assert.Equal(0, tampered.ExitCode);
        Assert.Equal("ignored-unverifiable", tampered.Json.GetProperty("status").GetString());
        Assert.Equal("issue-match-untrusted", tampered.Json.GetProperty("reason").GetString());

        issue.Body = trustedBody;
        issue.State = "closed";
        var reopened = await RunPublisherAsync(
            server,
            token,
            recurrenceRunId,
            1,
            SourceEvent,
            Repository,
            HeadRef,
            PullRequestNumber);

        Assert.Equal(0, reopened.ExitCode);
        Assert.Equal("reopened-and-commented", reopened.Json.GetProperty("status").GetString());
        Assert.Equal("open", issue.State);
        var comment = Assert.Single(server.Comments);
        Assert.Matches(
            $"^<!-- devflow-ci-failure-occurrence:v1 run={recurrenceRunId} attempt=1 body=sha256:[0-9a-f]{{64}} -->\n",
            comment.Body);

        var duplicateRunId = RunId + 2;
        var duplicateArchive = CreateArchive(runId: duplicateRunId, runAttempt: 1);
        server.AddRun(duplicateRunId, 1, duplicateArchive);
        server.Issues.Add(issue.Clone(number: issue.Number + 1));
        var ambiguous = await RunPublisherAsync(
            server,
            token,
            duplicateRunId,
            1,
            SourceEvent,
            Repository,
            HeadRef,
            PullRequestNumber);

        Assert.Equal(0, ambiguous.ExitCode);
        Assert.Equal("ignored-unverifiable", ambiguous.Json.GetProperty("status").GetString());
        Assert.Equal("issue-match-ambiguous", ambiguous.Json.GetProperty("reason").GetString());
        Assert.All(
            server.Requests,
            request => Assert.Equal($"Bearer {token}", request.Authorization));
        Assert.DoesNotContain(token, created.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(token, reopened.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishMode_IssuesDisabled_ReturnsNoOpBeforeArtifactLookup()
    {
        using var server = new MockGitHubApiServer { IssuesEnabled = false };

        var result = await RunPublisherAsync(
            server,
            "publisher-test-token",
            RunId,
            RunAttempt,
            SourceEvent,
            Repository,
            HeadRef,
            PullRequestNumber);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-issues-disabled", result.Json.GetProperty("status").GetString());
        Assert.Equal("repository-issues-disabled", result.Json.GetProperty("reason").GetString());
        var request = Assert.Single(server.Requests);
        Assert.Equal($"/repos/{Repository}", request.PathAndQuery);
    }

    [Fact]
    public async Task PublishMode_UnauthorizedResponse_IsReportedWithoutLeakingToken()
    {
        const string token = "unauthorized-test-token";
        using var server = new MockGitHubApiServer { Unauthorized = true };

        var result = await RunPublisherAsync(
            server,
            token,
            RunId,
            RunAttempt,
            SourceEvent,
            Repository,
            HeadRef,
            PullRequestNumber);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("publisher-error", result.Json.GetProperty("status").GetString());
        Assert.Equal("github-api-unauthorized", result.Json.GetProperty("reason").GetString());
        Assert.Equal($"Bearer {token}", Assert.Single(server.Requests).Authorization);
        Assert.DoesNotContain(token, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pull_request", Repository, "feature/security", 123, "source-event-not-publishable")]
    [InlineData("schedule", "untrusted/fork", HeadRef, 0, "source-head-repository-untrusted")]
    [InlineData("workflow_dispatch", Repository, "feature/security", 0, "source-head-ref-untrusted")]
    public async Task PublishMode_UntrustedSource_IsRejectedBeforeApiAccess(
        string sourceEvent,
        string headRepository,
        string headRef,
        int pullRequestNumber,
        string expectedReason)
    {
        var result = await RunPublisherProcessAsync(
            apiBaseUrl: "http://127.0.0.1:1",
            token: "not-sent",
            runId: RunId,
            runAttempt: RunAttempt,
            sourceEvent: sourceEvent,
            headRepository: headRepository,
            headRef: headRef,
            pullRequestNumber: pullRequestNumber);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ignored-untrusted-source", result.Json.GetProperty("status").GetString());
        Assert.Equal(expectedReason, result.Json.GetProperty("reason").GetString());
    }

    [Fact]
    public void PublisherWorkflow_IsValidYamlAndUsesTrustedWorkflowRunBoundary()
    {
        var path = Path.Combine(_repositoryRoot, ".github", "workflows", "devflow-failure-publisher.yml");
        var text = File.ReadAllText(path);
        var yaml = new YamlStream();
        using (var reader = new StringReader(text))
            yaml.Load(reader);

        Assert.NotNull(Assert.Single(yaml.Documents).RootNode);
        Assert.Contains("workflow_run:", text, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.event.repository.default_branch }}", text, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", text, StringComparison.Ordinal);
        Assert.Contains("actions: read", text, StringComparison.Ordinal);
        Assert.Contains("contents: read", text, StringComparison.Ordinal);
        Assert.Contains("issues: write", text, StringComparison.Ordinal);
        Assert.Contains("group: devflow-failure-publisher-${{ github.repository_id }}", text, StringComparison.Ordinal);
        Assert.Contains("github.event.workflow_run.event == 'schedule'", text, StringComparison.Ordinal);
        Assert.Contains("github.event.workflow_run.event == 'workflow_dispatch'", text, StringComparison.Ordinal);
        Assert.Contains("github.event.workflow_run.head_repository.full_name == github.repository", text, StringComparison.Ordinal);
        Assert.Contains(
            "github.event.workflow_run.head_branch == github.event.repository.default_branch",
            text,
            StringComparison.Ordinal);
        Assert.Contains("github.event.workflow_run.pull_requests[0] == null", text, StringComparison.Ordinal);
        Assert.Contains("-SourceEvent $env:DEVFLOW_SOURCE_EVENT", text, StringComparison.Ordinal);
        Assert.Contains("-HeadRepository $env:DEVFLOW_HEAD_REPOSITORY", text, StringComparison.Ordinal);
        Assert.Contains("-HeadRef $env:DEVFLOW_HEAD_REF", text, StringComparison.Ordinal);
        Assert.Contains("-DefaultBranch $env:DEVFLOW_DEFAULT_BRANCH", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pull-requests:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", text, StringComparison.Ordinal);
        Assert.DoesNotContain("checks: write", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvestigatorWorkflow_IsDeterministicAndHasNoAgentActivationSurface()
    {
        var removedSourcePath = Path.Combine(
            _repositoryRoot,
            ".github",
            "workflows",
            "devflow-investigate.agent.md");
        var removedLockPath = Path.Combine(
            _repositoryRoot,
            ".github",
            "workflows",
            "devflow-investigate.agent.lock.yml");
        var workflowPath = Path.Combine(
            _repositoryRoot,
            ".github",
            "workflows",
            "devflow-investigate.yml");
        var workflow = File.ReadAllText(workflowPath);
        var yaml = new YamlStream();
        using (var reader = new StringReader(workflow))
            yaml.Load(reader);

        Assert.False(File.Exists(removedSourcePath));
        Assert.False(File.Exists(removedLockPath));
        Assert.NotNull(Assert.Single(yaml.Documents).RootNode);
        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("issues: write", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/github-script@373c709c69115d41ff229c7e5df9f8788daa9553", workflow, StringComparison.Ordinal);
        Assert.Contains("issue-body-digest-mismatch", workflow, StringComparison.Ordinal);
        Assert.Contains("devflow-ci-guidance:v1", workflow, StringComparison.Ordinal);
        Assert.Contains("github.rest.issues.createComment", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("issue_comment:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gh-aw", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copilot", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull-requests:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: read", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("upload-artifact", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("issues.create({", workflow, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private string CreateArchive(
        string outcome = "failure",
        string qualification = "qualified",
        string evidenceSufficiency = "sufficient",
        string repository = Repository,
        string sourceEvent = SourceEvent,
        string headRepository = Repository,
        string headRef = HeadRef,
        long runId = RunId,
        int runAttempt = RunAttempt,
        int pullRequestNumber = PullRequestNumber,
        string? declaredSha256 = null,
        int manifestVersion = 1,
        int handoffVersion = 1,
        string replaceHandoffName = "handoff.json",
        bool manifestEntriesAsObject = false,
        long? handoffRunId = null)
    {
        var archive = Path.Combine(_testRoot, $"{Guid.NewGuid():N}.zip");
        var handoff = JsonSerializer.Serialize(new
        {
            schema = "devflow-ci-failure-handoff",
            version = handoffVersion,
            provenance = new
            {
                repository,
                workflowName = WorkflowName,
                workflowPath = WorkflowPath,
                sourceEvent,
                headRepository,
                headRefSha256 = $"sha256:{Hash(Encoding.UTF8.GetBytes(headRef))}",
                runId = handoffRunId ?? runId,
                runAttempt,
                commitSha = CommitSha,
                pullRequestNumber,
            },
            outcome,
            qualification,
            category = "test-failure",
            platform = "android",
            testIdentitySha256 = $"sha256:{new string('1', 64)}",
            evidenceSufficiency,
        });
        var handoffBytes = Encoding.UTF8.GetBytes(handoff);
        var handoffHash = Convert.ToHexString(SHA256.HashData(handoffBytes)).ToLowerInvariant();
        var declaration = new
        {
            name = "handoff.json",
            sha256 = declaredSha256 ?? $"sha256:{handoffHash}",
            sizeBytes = handoffBytes.LongLength,
        };
        var manifest = JsonSerializer.Serialize(new
        {
            schema = "devflow-ci-failure-manifest",
            version = manifestVersion,
            entries = manifestEntriesAsObject ? (object)declaration : new[] { declaration },
        });

        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", manifest);
            WriteEntry(zip, replaceHandoffName, handoff);
        }

        return archive;
    }

    private async Task<(int ExitCode, JsonElement Json)> RunVerifierAsync(
        string archive,
        string sourceEvent = SourceEvent,
        string headRepository = Repository,
        string headRef = HeadRef,
        int pullRequestNumber = PullRequestNumber)
    {
        var result = await RunScriptAsync(
            verifyOnly: true,
            archivePath: archive,
            apiBaseUrl: "https://api.github.com",
            token: string.Empty,
            runId: RunId,
            runAttempt: RunAttempt,
            sourceEvent,
            headRepository,
            headRef,
            pullRequestNumber);
        return (result.ExitCode, result.Json);
    }

    private Task<ScriptResult> RunPublisherAsync(
        MockGitHubApiServer server,
        string token,
        long runId,
        int runAttempt,
        string sourceEvent,
        string headRepository,
        string headRef,
        int pullRequestNumber) =>
        RunPublisherProcessAsync(
            server.BaseUrl,
            token,
            runId,
            runAttempt,
            sourceEvent,
            headRepository,
            headRef,
            pullRequestNumber);

    private Task<ScriptResult> RunPublisherProcessAsync(
        string apiBaseUrl,
        string token,
        long runId,
        int runAttempt,
        string sourceEvent,
        string headRepository,
        string headRef,
        int pullRequestNumber) =>
        RunScriptAsync(
            verifyOnly: false,
            archivePath: Path.Combine(_testRoot, $"download-{runId}-{runAttempt}.zip"),
            apiBaseUrl,
            token,
            runId,
            runAttempt,
            sourceEvent,
            headRepository,
            headRef,
            pullRequestNumber);

    private async Task<ScriptResult> RunScriptAsync(
        bool verifyOnly,
        string archivePath,
        string apiBaseUrl,
        string token,
        long runId,
        int runAttempt,
        string sourceEvent,
        string headRepository,
        string headRef,
        int pullRequestNumber)
    {
        var script = Path.Combine(_repositoryRoot, "eng", "devflow", "Publish-DevFlowFailureIssue.ps1");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
            {
                WorkingDirectory = _repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        if (new Uri(apiBaseUrl).IsLoopback)
            process.StartInfo.Environment["DEVFLOW_PUBLISHER_ALLOW_LOOPBACK_TEST_API"] = "1";
        AddArgument("-NoLogo");
        AddArgument("-NoProfile");
        AddArgument("-File");
        AddArgument(script);
        if (verifyOnly)
            AddArgument("-VerifyOnly");
        AddArgument("-ArchivePath");
        AddArgument(archivePath);
        AddArgument("-Repository");
        AddArgument(Repository);
        AddArgument("-WorkflowName");
        AddArgument(WorkflowName);
        AddArgument("-WorkflowPath");
        AddArgument(WorkflowPath);
        AddArgument("-SourceEvent");
        AddArgument(sourceEvent);
        AddArgument("-HeadRepository");
        AddArgument(headRepository);
        AddArgument("-HeadRef");
        AddArgument(headRef);
        AddArgument("-DefaultBranch");
        AddArgument(HeadRef);
        AddArgument("-WorkflowConclusion");
        AddArgument("failure");
        AddArgument("-RunId");
        AddArgument(runId.ToString());
        AddArgument("-RunAttempt");
        AddArgument(runAttempt.ToString());
        AddArgument("-CommitSha");
        AddArgument(CommitSha);
        AddArgument("-PullRequestNumber");
        AddArgument(pullRequestNumber.ToString());
        AddArgument("-GitHubToken");
        AddArgument(token);
        AddArgument("-GitHubApiBaseUrl");
        AddArgument(apiBaseUrl);

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var jsonLine = stdout.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
        Assert.True(jsonLine is not null, $"No JSON result. stdout={stdout}; stderr={stderr}");
        using var document = JsonDocument.Parse(jsonLine);
        return new ScriptResult(process.ExitCode, stdout, stderr, document.RootElement.Clone());

        void AddArgument(string value) => process.StartInfo.ArgumentList.Add(value);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record ScriptResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        JsonElement Json);

    private sealed record RecordedRequest(
        string Method,
        string PathAndQuery,
        string? Authorization,
        string Body);

    private sealed class MockIssue
    {
        public required int Number { get; init; }
        public required string Title { get; init; }
        public required string Body { get; set; }
        public required string State { get; set; }
        public List<string> Labels { get; } = ["devflow-ci-failure"];

        public MockIssue Clone(int number) =>
            new()
            {
                Number = number,
                Title = Title,
                Body = Body,
                State = State,
            };

        public object ToApi() =>
            new
            {
                number = Number,
                title = Title,
                body = Body,
                state = State,
                user = new { login = "github-actions[bot]", type = "Bot" },
                labels = Labels.Select(static name => new { name }).ToArray(),
            };
    }

    private sealed class MockComment
    {
        public required string Body { get; init; }

        public object ToApi() =>
            new
            {
                body = Body,
                user = new { login = "github-actions[bot]", type = "Bot" },
            };
    }

    private sealed class MockGitHubApiServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _listenTask;
        private readonly Dictionary<long, MockRun> _runs = [];
        private readonly Dictionary<long, byte[]> _artifacts = [];

        public MockGitHubApiServer()
        {
            var port = GetAvailablePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _listenTask = Task.Run(ListenAsync);
        }

        public string BaseUrl { get; }
        public bool IssuesEnabled { get; set; } = true;
        public bool LabelExists { get; set; } = true;
        public bool Unauthorized { get; set; }
        public List<RecordedRequest> Requests { get; } = [];
        public List<MockIssue> Issues { get; } = [];
        public List<MockComment> Comments { get; } = [];

        public void AddRun(long runId, int runAttempt, string archivePath)
        {
            var artifactId = runId + 1_000_000;
            var bytes = File.ReadAllBytes(archivePath);
            _runs.Add(runId, new MockRun(runId, runAttempt, artifactId, bytes.LongLength));
            _artifacts.Add(artifactId, bytes);
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _listenTask.GetAwaiter().GetResult();
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task ListenAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (!_listener.IsListening)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                await HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var body = string.Empty;
            if (context.Request.HasEntityBody)
            {
                using var reader = new StreamReader(
                    context.Request.InputStream,
                    context.Request.ContentEncoding ?? Encoding.UTF8);
                body = await reader.ReadToEndAsync();
            }

            Requests.Add(
                new RecordedRequest(
                    context.Request.HttpMethod,
                    context.Request.RawUrl ?? string.Empty,
                    context.Request.Headers["Authorization"],
                    body));

            try
            {
                if (Unauthorized)
                {
                    await WriteJsonAsync(context, HttpStatusCode.Unauthorized, new { message = "Bad credentials" });
                    return;
                }

                var path = context.Request.Url!.AbsolutePath;
                var method = context.Request.HttpMethod;
                if (method == "GET" && path == $"/repos/{Repository}")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new { full_name = Repository, default_branch = HeadRef, has_issues = IssuesEnabled });
                    return;
                }
                if (method == "GET" && path == $"/repos/{Repository}/labels/devflow-ci-failure")
                {
                    if (LabelExists)
                        await WriteJsonAsync(context, HttpStatusCode.OK, new { name = "devflow-ci-failure" });
                    else
                        await WriteJsonAsync(context, HttpStatusCode.NotFound, new { message = "Not found" });
                    return;
                }
                if (method == "POST" && path == $"/repos/{Repository}/labels")
                {
                    LabelExists = true;
                    await WriteJsonAsync(context, HttpStatusCode.Created, new { name = "devflow-ci-failure" });
                    return;
                }

                var runMatch = System.Text.RegularExpressions.Regex.Match(
                    path,
                    $"^/repos/{System.Text.RegularExpressions.Regex.Escape(Repository)}/actions/runs/([0-9]+)$");
                if (method == "GET" && runMatch.Success)
                {
                    var run = _runs[long.Parse(runMatch.Groups[1].Value)];
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new
                        {
                            id = run.RunId,
                            repository = new { full_name = Repository },
                            name = WorkflowName,
                            path = WorkflowPath,
                            @event = SourceEvent,
                            conclusion = "failure",
                            run_attempt = run.RunAttempt,
                            head_sha = CommitSha,
                            head_repository = new { full_name = Repository },
                            head_branch = HeadRef,
                            pull_requests = Array.Empty<object>(),
                        });
                    return;
                }

                var artifactsMatch = System.Text.RegularExpressions.Regex.Match(
                    path,
                    $"^/repos/{System.Text.RegularExpressions.Regex.Escape(Repository)}/actions/runs/([0-9]+)/artifacts$");
                if (method == "GET" && artifactsMatch.Success)
                {
                    var run = _runs[long.Parse(artifactsMatch.Groups[1].Value)];
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        new
                        {
                            artifacts = new[]
                            {
                                new
                                {
                                    id = run.ArtifactId,
                                    name = $"devflow-failure-handoff-{run.RunId}-{run.RunAttempt}",
                                    expired = false,
                                    size_in_bytes = run.ArchiveSize,
                                    workflow_run = new { id = run.RunId },
                                },
                            },
                        });
                    return;
                }

                var artifactMatch = System.Text.RegularExpressions.Regex.Match(
                    path,
                    $"^/repos/{System.Text.RegularExpressions.Regex.Escape(Repository)}/actions/artifacts/([0-9]+)/zip$");
                if (method == "GET" && artifactMatch.Success)
                {
                    await WriteBytesAsync(
                        context,
                        HttpStatusCode.OK,
                        "application/zip",
                        _artifacts[long.Parse(artifactMatch.Groups[1].Value)]);
                    return;
                }

                if (method == "GET" && path == $"/repos/{Repository}/issues")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        Issues.Select(static issue => issue.ToApi()).ToArray());
                    return;
                }
                if (method == "POST" && path == $"/repos/{Repository}/issues")
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    var issue = new MockIssue
                    {
                        Number = 42,
                        Title = root.GetProperty("title").GetString()!,
                        Body = root.GetProperty("body").GetString()!,
                        State = "open",
                    };
                    issue.Labels.Clear();
                    issue.Labels.AddRange(
                        root.GetProperty("labels").EnumerateArray().Select(static label => label.GetString()!));
                    Issues.Add(issue);
                    await WriteJsonAsync(context, HttpStatusCode.Created, issue.ToApi());
                    return;
                }

                var commentsMatch = System.Text.RegularExpressions.Regex.Match(
                    path,
                    $"^/repos/{System.Text.RegularExpressions.Regex.Escape(Repository)}/issues/([0-9]+)/comments$");
                if (commentsMatch.Success && method == "GET")
                {
                    await WriteJsonAsync(
                        context,
                        HttpStatusCode.OK,
                        Comments.Select(static comment => comment.ToApi()).ToArray());
                    return;
                }
                if (commentsMatch.Success && method == "POST")
                {
                    using var document = JsonDocument.Parse(body);
                    var comment = new MockComment
                    {
                        Body = document.RootElement.GetProperty("body").GetString()!,
                    };
                    Comments.Add(comment);
                    await WriteJsonAsync(context, HttpStatusCode.Created, comment.ToApi());
                    return;
                }

                var issueMatch = System.Text.RegularExpressions.Regex.Match(
                    path,
                    $"^/repos/{System.Text.RegularExpressions.Regex.Escape(Repository)}/issues/([0-9]+)$");
                if (issueMatch.Success && method == "PATCH")
                {
                    var number = int.Parse(issueMatch.Groups[1].Value);
                    var issue = Issues.Single(candidate => candidate.Number == number);
                    issue.State = "open";
                    await WriteJsonAsync(context, HttpStatusCode.OK, issue.ToApi());
                    return;
                }

                await WriteJsonAsync(context, HttpStatusCode.NotFound, new { message = "Not found" });
            }
            catch (Exception exception)
            {
                await WriteJsonAsync(
                    context,
                    HttpStatusCode.InternalServerError,
                    new { message = exception.GetType().Name });
            }
        }

        private static async Task WriteJsonAsync(
            HttpListenerContext context,
            HttpStatusCode statusCode,
            object value) =>
            await WriteBytesAsync(
                context,
                statusCode,
                "application/json",
                JsonSerializer.SerializeToUtf8Bytes(value));

        private static async Task WriteBytesAsync(
            HttpListenerContext context,
            HttpStatusCode statusCode,
            string contentType,
            byte[] bytes)
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        private static int GetAvailablePort() => TestPorts.Reserve();

        private sealed record MockRun(
            long RunId,
            int RunAttempt,
            long ArtifactId,
            long ArchiveSize);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MauiLabs.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the maui-labs repository root.");
    }
}
