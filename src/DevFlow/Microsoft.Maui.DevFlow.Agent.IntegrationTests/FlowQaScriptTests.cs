using System.Diagnostics;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests;

public sealed class FlowQaScriptTests
{
    static readonly string RepositoryRoot = AppFixtureBase.FindRepoRoot();

    [Theory]
    [InlineData("android", "Category=FlowPilot")]
    [InlineData("windows", "Category=WindowsFlowQa")]
    [InlineData("ios", "Category=AppleTestAgent")]
    [InlineData("maccatalyst", "Category=AppleTestAgent")]
    public void PowerShellDryRun_UsesPlatformFilterAndSafeArtifactRoots(string platform, string expectedFilter)
    {
        var result = RunPowerShell(
            "--platform", platform,
            "--repeat", "2",
            "--results-root", ResultsRoot(platform),
            "--no-build",
            "--dry-run");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("dry-run", root.GetProperty("status").GetString());
        Assert.Equal(platform, root.GetProperty("platform").GetString());
        Assert.Equal(2, root.GetProperty("repeat").GetInt32());
        Assert.Equal(expectedFilter, root.GetProperty("testFilter").GetString());
        Assert.True(root.GetProperty("noBuild").GetBoolean());
        Assert.Equal(
            $"artifacts/TestResults/devflow-flow/{platform}",
            root.GetProperty("artifactPaths").GetProperty("testResults").GetString());

        var arguments = root.GetProperty("command").GetProperty("arguments")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("--no-build", arguments);
        Assert.Contains(expectedFilter, arguments);
    }

    [Fact]
    public void PowerShellDryRun_RejectsUnsafeOutputAndExperimentalGate()
    {
        var unsafeRoot = RunPowerShell(
            "--platform", "android",
            "--results-root", ".",
            "--dry-run");
        Assert.Equal(2, unsafeRoot.ExitCode);
        Assert.Contains("--results-root", unsafeRoot.StandardError, StringComparison.Ordinal);

        var missingExperimental = RunPowerShell(
            "--platform", "macos",
            "--results-root", ResultsRoot("macos"),
            "--dry-run");
        Assert.Equal(2, missingExperimental.ExitCode);
        Assert.Contains("--experimental", missingExperimental.StandardError, StringComparison.Ordinal);

        var experimental = RunPowerShell(
            "--platform", "macos",
            "--experimental",
            "--results-root", ResultsRoot("macos"),
            "--dry-run");
        Assert.Equal(0, experimental.ExitCode);
        using var document = JsonDocument.Parse(experimental.StandardOutput);
        Assert.Equal(
            "samples/DevFlow.Sample.MacOS/DevFlow.Sample.MacOS.csproj",
            document.RootElement.GetProperty("appProject").GetString());
        Assert.Equal("Category=AppKitFlowQa", document.RootElement.GetProperty("testFilter").GetString());
        Assert.Equal("appkit", document.RootElement.GetProperty("backend").GetString());
        Assert.False(document.RootElement.GetProperty("officialCoverage").GetBoolean());
        Assert.False(document.RootElement.GetProperty("macCatalystEquivalent").GetBoolean());

        var qualification = RunPowerShell(
            "--platform", "macos",
            "--experimental",
            "--qualification",
            "--results-root", ResultsRoot("macos"),
            "--dry-run");
        Assert.Equal(2, qualification.ExitCode);
        Assert.Contains("--qualification", qualification.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellDryRun_PhysicalIosRequiresInputsAndRedactsThem()
    {
        var missingInputs = RunPowerShell(
            "--platform", "ios",
            "--physical-device",
            "--results-root", ResultsRoot("ios"),
            "--dry-run");
        Assert.Equal(2, missingInputs.ExitCode);
        Assert.Contains("--device-id", missingInputs.StandardError, StringComparison.Ordinal);

        const string signingIdentity = "FLOW-QA-SIGNING-IDENTITY-SENTINEL";
        const string provisioningProfile = "FLOW-QA-PROFILE-SENTINEL";
        const string keychain = "FLOW-QA-KEYCHAIN-SENTINEL";
        var physical = RunPowerShell(
            "--platform", "ios",
            "--physical-device",
            "--device-id", "FLOW-QA-DEVICE-SENTINEL",
            "--signing-identity", signingIdentity,
            "--provisioning-profile", provisioningProfile,
            "--keychain", keychain,
            "--results-root", ResultsRoot("ios"),
            "--dry-run");

        Assert.Equal(0, physical.ExitCode);
        Assert.DoesNotContain(signingIdentity, physical.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(provisioningProfile, physical.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(keychain, physical.StandardOutput, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(physical.StandardOutput);
        Assert.True(document.RootElement.GetProperty("physicalDevice").GetBoolean());
        Assert.True(document.RootElement.GetProperty("signingInputsConfigured").GetBoolean());
    }

    [Fact]
    public void PowerShellDryRun_ReportsQualificationIntentWithoutClaimingSuccess()
    {
        var result = RunPowerShell(
            "--platform", "android",
            "--qualification",
            "--results-root", ResultsRoot("android"),
            "--dry-run");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("dry-run", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("qualificationRequested").GetBoolean());
    }

    [Fact]
    public void PowerShellWindowsDryRun_UsesFixtureOwnedCleanRepetitions()
    {
        var result = RunPowerShell(
            "--platform", "windows",
            "--repeat", "3",
            "--results-root", ResultsRoot("windows"),
            "--dry-run");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("Category=WindowsFlowQa", document.RootElement.GetProperty("testFilter").GetString());

        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "eng",
            "devflow",
            "Run-DevFlowFlowQa.ps1"));
        Assert.Contains("DEVFLOW_RUN_WINDOWS_FLOW_QA", script, StringComparison.Ordinal);
        Assert.Contains("windows-fixture-per-flow-clean-attempts", script, StringComparison.Ordinal);
        Assert.Contains("cleanRepetitionsPerFlow", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Measure-Object -Character -Sum", script, StringComparison.Ordinal);
        Assert.DoesNotContain("adb kill-server", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adb start-server", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellWindowsSessionPreflight_DisconnectedSessionFailsClosedBeforeTestProcessOrReplay()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var runId = $"flow-qa-windows-session-{Guid.NewGuid():N}";
        var artifactRunRoot = Path.Combine(RepositoryRoot, "artifacts", "devflow", runId, "windows");
        try
        {
            var result = RunPowerShell(
                new Dictionary<string, string?>
                {
                    ["DEVFLOW_FLOW_QA_RUN_ID"] = runId,
                    ["DEVFLOW_WINDOWS_SESSION_PREFLIGHT_TEST_STATE"] = "disconnected",
                },
                "--platform", "windows",
                "--results-root", ResultsRoot("windows"));

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("active, unlocked desktop session", result.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(
                RepositoryRoot,
                "artifacts",
                "TestResults",
                "devflow-flow",
                "windows",
                $"devflow-flow-windows-{runId}.trx")));
            Assert.Empty(Directory.GetFiles(
                artifactRunRoot,
                "test-output-*.txt",
                SearchOption.AllDirectories));

            var sessionPath = Path.Combine(
                artifactRunRoot,
                "host-diagnostics",
                "windows-session.json");
            Assert.True(File.Exists(sessionPath));
            using var sessionDocument = JsonDocument.Parse(File.ReadAllText(sessionPath));
            var session = sessionDocument.RootElement;
            Assert.Equal("disconnected", session.GetProperty("wtsConnectionState").GetString());
            Assert.Equal("rejected", session.GetProperty("admissionResult").GetString());
            Assert.Equal("wts-connection-state-disconnected", session.GetProperty("reason").GetString());
            Assert.DoesNotContain("quser", File.ReadAllText(sessionPath), StringComparison.OrdinalIgnoreCase);

            var manifestPath = Path.Combine(artifactRunRoot, "manifest.json");
            Assert.True(File.Exists(manifestPath));
            using var manifestDocument = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var hostQa = manifestDocument.RootElement.GetProperty("hostQa");
            var firstAttempt = hostQa.GetProperty("firstAttempt");
            Assert.Equal("infrastructure-error", firstAttempt.GetProperty("outcome").GetString());
            Assert.False(firstAttempt.GetProperty("mutationDispatched").GetBoolean());
        }
        finally
        {
            var runDirectory = Path.Combine(RepositoryRoot, "artifacts", "devflow", runId);
            if (Directory.Exists(runDirectory))
                Directory.Delete(runDirectory, recursive: true);
        }
    }

    [Fact]
    public void PowerShellPreflight_WritesManifestAndSeparatesPendingFromHostPrerequisites()
    {
        var runId = $"flow-qa-script-test-{Guid.NewGuid():N}";
        var artifactRunRoot = Path.Combine(RepositoryRoot, "artifacts", "devflow", runId);
        try
        {
            var result = RunPowerShell(
                new Dictionary<string, string?> { ["DEVFLOW_FLOW_QA_RUN_ID"] = runId },
                "--platform", "ios",
                "--flow-filter", "FLOW-QA-FILTER-SENTINEL",
                "--results-root", ResultsRoot("ios"));

            var expectedExitCode = OperatingSystem.IsMacOS() ? 5 : 3;
            var expectedStatus = OperatingSystem.IsMacOS() ? "pending-spike" : "failed";
            var expectedClassification = OperatingSystem.IsMacOS() ? "capability-missing" : "prerequisite-missing";
            Assert.Equal(expectedExitCode, result.ExitCode);

            var manifestPath = Path.Combine(artifactRunRoot, "ios", "manifest.json");
            var flowRunPath = Path.Combine(artifactRunRoot, "ios", "flow-run.json");
            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(flowRunPath));
            var manifestText = File.ReadAllText(manifestPath);
            Assert.DoesNotContain("FLOW-QA-FILTER-SENTINEL", manifestText, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(manifestText);
            var hostQa = document.RootElement.GetProperty("hostQa");
            Assert.Equal(expectedStatus, hostQa.GetProperty("status").GetString());
            Assert.Equal(expectedClassification, hostQa.GetProperty("classification").GetString());
            Assert.Equal(JsonValueKind.Null, hostQa.GetProperty("firstAttempt").ValueKind);
            Assert.Contains(
                "diagnostic-rerun",
                document.RootElement.GetProperty("omissions").EnumerateArray()
                    .Select(static item => item.GetProperty("kind").GetString()));
            Assert.Contains(
                "package-digest",
                document.RootElement.GetProperty("omissions").EnumerateArray()
                    .Select(static item => item.GetProperty("kind").GetString()));
        }
        finally
        {
            if (Directory.Exists(artifactRunRoot))
                Directory.Delete(artifactRunRoot, recursive: true);
        }
    }

    [Fact]
    public void ShellScript_HasValidSyntaxAndMatchesPowerShellDryRunContract()
    {
        var shellScript = Path.Combine(RepositoryRoot, "eng", "devflow", "Run-DevFlowFlowQa.sh");
        var source = File.ReadAllText(shellScript);
        Assert.Contains("--platform", source, StringComparison.Ordinal);
        Assert.Contains("Category=FlowPilot", source, StringComparison.Ordinal);
        Assert.Contains("Category=AppleTestAgent", source, StringComparison.Ordinal);
        Assert.Contains("Category=AppKitFlowQa", source, StringComparison.Ordinal);
        Assert.Contains("capability-missing", source, StringComparison.Ordinal);
        Assert.Contains("--experimental", source, StringComparison.Ordinal);
        Assert.DoesNotContain("adb kill-server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("adb start-server", source, StringComparison.OrdinalIgnoreCase);

        var bash = FindBash();
        if (bash is null)
            return;

        var syntax = RunProcess(bash, "-n", "eng/devflow/Run-DevFlowFlowQa.sh");
        Assert.Equal(0, syntax.ExitCode);

        var shell = RunProcess(
            bash,
            "eng/devflow/Run-DevFlowFlowQa.sh",
            "--platform", "android",
            "--repeat", "2",
            "--results-root", "./artifacts/TestResults/devflow-flow/android",
            "--no-build",
            "--qualification",
            "--dry-run");
        Assert.Equal(0, shell.ExitCode);

        var powerShell = RunPowerShell(
            "--platform", "android",
            "--repeat", "2",
            "--results-root", ResultsRoot("android"),
            "--no-build",
            "--qualification",
            "--dry-run");

        using var shellDocument = JsonDocument.Parse(shell.StandardOutput);
        using var powerShellDocument = JsonDocument.Parse(powerShell.StandardOutput);
        Assert.Equal(
            powerShellDocument.RootElement.GetProperty("testFilter").GetString(),
            shellDocument.RootElement.GetProperty("testFilter").GetString());
        Assert.Equal(
            powerShellDocument.RootElement.GetProperty("artifactPaths").GetProperty("testResults").GetString(),
            shellDocument.RootElement.GetProperty("artifactPaths").GetProperty("testResults").GetString());
        Assert.Equal(
            powerShellDocument.RootElement.GetProperty("noBuild").GetBoolean(),
            shellDocument.RootElement.GetProperty("noBuild").GetBoolean());
        Assert.True(shellDocument.RootElement.GetProperty("qualificationRequested").GetBoolean());
        Assert.Equal(
            powerShellDocument.RootElement.GetProperty("qualificationRequested").GetBoolean(),
            shellDocument.RootElement.GetProperty("qualificationRequested").GetBoolean());
    }

    [Fact]
    public void ShellAppleSpikeDryRun_ConstructsHostProofWithoutExposingSessionSecret()
    {
        var bash = FindBash();
        if (bash is null)
            return;

        var shell = RunProcess(
            bash,
            "eng/devflow/Run-DevFlowFlowQa.sh",
            "--platform", "ios",
            "--apple-spike",
            "--target-app", "./artifacts/apple-spike-fake.app",
            "--target-bundle-id", "com.example.devflow.spike",
            "--simulator-id", "SIMULATOR-SENTINEL",
            "--safe-action-id", "AddButton",
            "--results-root", "./artifacts/TestResults/devflow-flow/ios",
            "--dry-run");

        Assert.Equal(0, shell.ExitCode);
        Assert.DoesNotContain("DEVFLOW_APPLE_AGENT_SESSION_SECRET", shell.StandardOutput, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(shell.StandardOutput);
        var root = document.RootElement;
        Assert.True(root.GetProperty("appleSpike").GetBoolean());

        var rejectedQualification = RunProcess(
            bash,
            "eng/devflow/Run-DevFlowFlowQa.sh",
            "--platform", "macos",
            "--experimental",
            "--qualification",
            "--results-root", "./artifacts/TestResults/devflow-flow/macos",
            "--dry-run");
        Assert.Equal(2, rejectedQualification.ExitCode);
        Assert.Contains("--qualification", rejectedQualification.StandardError, StringComparison.Ordinal);
        Assert.Equal("proof-required", root.GetProperty("capability").GetProperty("state").GetString());
        var arguments = root.GetProperty("command").GetProperty("arguments")
            .EnumerateArray()
            .Select(static item => item.GetString())
            .ToArray();
        Assert.Contains("--target-bundle-id", arguments);
        Assert.Contains("com.example.devflow.spike", arguments);
        Assert.DoesNotContain(arguments, static argument =>
            string.Equals(argument, "--session-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void ShellAppleQaDryRun_AutomaticallyRequiresProofBeforeCategoryExecution()
    {
        var bash = FindBash();
        if (bash is null)
            return;

        var shell = RunProcess(
            bash,
            "eng/devflow/Run-DevFlowFlowQa.sh",
            "--platform", "maccatalyst",
            "--results-root", "./artifacts/TestResults/devflow-flow/maccatalyst",
            "--dry-run");

        Assert.Equal(0, shell.ExitCode);
        using var document = JsonDocument.Parse(shell.StandardOutput);
        Assert.True(document.RootElement.GetProperty("appleSpike").GetBoolean());
        Assert.Equal("proof-required", document.RootElement.GetProperty("capability").GetProperty("state").GetString());
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "eng", "devflow", "Run-DevFlowFlowQa.sh"));
        Assert.Contains("run_apple_flow_qa", script, StringComparison.Ordinal);
        Assert.Contains("DEVFLOW_RUN_APPLE_FLOW_QA", script, StringComparison.Ordinal);
        Assert.Contains("apple-flow-qa.json", script, StringComparison.Ordinal);
        Assert.Contains("Apple evidence is adapted read-only", script, StringComparison.Ordinal);
        Assert.Contains("run_qualification || true", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellExperimentalAppKitDryRun_UsesDedicatedFixtureAndManifestLabels()
    {
        var bash = FindBash();
        if (bash is null)
            return;

        var shell = RunProcess(
            bash,
            "eng/devflow/Run-DevFlowFlowQa.sh",
            "--platform", "macos",
            "--experimental",
            "--repeat", "2",
            "--results-root", "./artifacts/TestResults/devflow-flow/macos",
            "--dry-run");

        Assert.Equal(0, shell.ExitCode);
        using var document = JsonDocument.Parse(shell.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("Category=AppKitFlowQa", root.GetProperty("testFilter").GetString());
        Assert.Equal(
            "samples/DevFlow.Sample.MacOS/DevFlow.Sample.MacOS.csproj",
            root.GetProperty("appProject").GetString());
        Assert.True(root.GetProperty("experimental").GetBoolean());
        Assert.Equal("appkit", root.GetProperty("backend").GetString());
        Assert.False(root.GetProperty("officialCoverage").GetBoolean());
        Assert.False(root.GetProperty("macCatalystEquivalent").GetBoolean());
        Assert.True(root.GetProperty("appleSpike").GetBoolean());
    }

    [Fact]
    public void FlowQaDocumentation_ContainsRequiredPlatformHandoffsAndArtifactInstructions()
    {
        var documentation = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "DevFlow", "flow-qa.md"));
        var appleSpike = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "DevFlow", "apple-xctest-spike.md"));

        Assert.Contains("--platform android", documentation, StringComparison.Ordinal);
        Assert.Contains("--platform windows", documentation, StringComparison.Ordinal);
        Assert.Contains("--platform ios", documentation, StringComparison.Ordinal);
        Assert.Contains("--platform maccatalyst", documentation, StringComparison.Ordinal);
        Assert.Contains("--platform macos", documentation, StringComparison.Ordinal);
        Assert.Contains("--physical-device", documentation, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive", documentation, StringComparison.Ordinal);
        Assert.Contains("zip -r", documentation, StringComparison.Ordinal);
        Assert.Contains("capability-missing", documentation, StringComparison.Ordinal);
        Assert.Contains("AppKit", documentation, StringComparison.Ordinal);
        Assert.Contains("DevFlow.Sample.MacOS", documentation, StringComparison.Ordinal);
        Assert.Contains("macCatalystEquivalent: false", documentation, StringComparison.Ordinal);
        Assert.Contains("python3", documentation, StringComparison.Ordinal);
        Assert.Contains("openssl", documentation, StringComparison.Ordinal);
        Assert.Contains("10.0.301", documentation, StringComparison.Ordinal);
        Assert.Contains("10.0.203", documentation, StringComparison.Ordinal);
        Assert.Contains("verify-apple-qa", documentation, StringComparison.Ordinal);
        Assert.Contains("--import-diagnostics", documentation, StringComparison.Ordinal);
        Assert.Contains("signed-device harness", documentation, StringComparison.Ordinal);
        Assert.Contains("--qualification", documentation, StringComparison.Ordinal);
        Assert.Contains("fixture-initialization.json", documentation, StringComparison.Ordinal);
        Assert.Contains("adb kill-server", documentation, StringComparison.Ordinal);
        Assert.Contains("adb start-server", documentation, StringComparison.Ordinal);
        Assert.Contains("never run", documentation, StringComparison.Ordinal);
        Assert.Contains("verify-apple-qa", appleSpike, StringComparison.Ordinal);
        Assert.Contains("--import-diagnostics", appleSpike, StringComparison.Ordinal);
        Assert.Contains("signed-device", appleSpike, StringComparison.Ordinal);
    }

    static string ResultsRoot(string platform)
        => Path.Combine(RepositoryRoot, "artifacts", "TestResults", "devflow-flow", platform);

    static ProcessResult RunPowerShell(params string[] arguments)
    {
        return RunPowerShell(null, arguments);
    }

    static ProcessResult RunPowerShell(
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var script = Path.Combine(RepositoryRoot, "eng", "devflow", "Run-DevFlowFlowQa.ps1");
        var executable = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var invocation = new List<string> { "-NoProfile", "-File", script };
        invocation.AddRange(arguments);
        return RunProcess(executable, environment, invocation.ToArray());
    }

    static string? FindBash()
    {
        if (!OperatingSystem.IsWindows())
            return "bash";

        var gitBash = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Git",
            "bin",
            "bash.exe");
        return File.Exists(gitBash) ? gitBash : null;
    }

    static ProcessResult RunProcess(string fileName, params string[] arguments)
        => RunProcess(fileName, null, arguments);

    static ProcessResult RunProcess(
        string fileName,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                process.StartInfo.Environment[key] = value;
        }
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), $"Timed out running {fileName}.");
        return new ProcessResult(process.ExitCode, standardOutput.Trim(), standardError.Trim());
    }

    sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
