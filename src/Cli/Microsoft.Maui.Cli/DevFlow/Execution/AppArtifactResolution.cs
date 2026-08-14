using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.Commands;
using Microsoft.Maui.Cli.Utils;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal sealed record AppArtifactInspectionLimits
{
    public long MaximumFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int MaximumFiles { get; init; } = 100_000;
    public int MaximumDepth { get; init; } = 64;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

internal sealed class MsBuildAppArtifactResolver : IAppArtifactResolver
{
    private const string Separator = "|||DEVFLOW_ARTIFACT|||";
    private const int MaximumMetadataBytes = 1_048_576;
    private const int MaximumArtifactRecords = 256;
    private const int MaximumBuildOutputCharacters = 1_048_576;
    private const int MaximumBuildDiagnosticCharacters = 2_048;
    private static readonly string[] MsBuildEnvironmentVariablesToRemove =
    [
        "MSBuildSDKsPath",
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildToolsPath",
    ];

    private readonly IExecutionProcessRunner _processRunner;
    private readonly AppArtifactInspectionLimits _inspectionLimits;

    public MsBuildAppArtifactResolver(
        IExecutionProcessRunner processRunner,
        AppArtifactInspectionLimits? inspectionLimits = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _inspectionLimits = inspectionLimits ?? new AppArtifactInspectionLimits();
        if (_inspectionLimits.MaximumFileBytes <= 0 ||
            _inspectionLimits.MaximumTotalBytes < _inspectionLimits.MaximumFileBytes ||
            _inspectionLimits.MaximumFiles <= 0 ||
            _inspectionLimits.MaximumDepth <= 0 ||
            _inspectionLimits.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inspectionLimits));
        }
    }

    public async Task<ResolvedAppArtifact> ResolveAsync(
        AppArtifactResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentSessionId) ||
            request.AgentSessionId.Length > 64 ||
            request.AgentSessionId.Any(static character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw FlowExecutionException.Invalid(
                "agent-session-id-invalid",
                "The opaque DevFlow agent session identity is invalid.");
        }
        var projectPath = ValidateProjectPath(request.ProjectPath);
        var targetFramework = ResolveTargetFramework(
            projectPath,
            request.TargetFramework,
            request.Platform,
            request.TargetFrameworkPlatformIdentifiers);
        var configuration = string.IsNullOrWhiteSpace(request.Configuration)
            ? "Debug"
            : request.Configuration.Trim();
        if (!IsSafeMsBuildValue(configuration))
            throw FlowExecutionException.Invalid("configuration-invalid", "The build configuration is not valid.");
        var runtimeIdentifier = string.IsNullOrWhiteSpace(request.RuntimeIdentifier)
            ? null
            : request.RuntimeIdentifier.Trim();
        if (runtimeIdentifier is not null && !IsSafeMsBuildValue(runtimeIdentifier))
            throw FlowExecutionException.Invalid("runtime-identifier-invalid", "The runtime identifier is not valid.");
        ValidateRuntimeIdentifier(
            runtimeIdentifier,
            targetFramework,
            request.Platform,
            request.TargetFrameworkPlatformIdentifiers);

        var targetsPath = FindAppProjectReferenceTargets();
        if (targetsPath is null)
        {
            throw FlowExecutionException.Infrastructure(
                "app-project-reference-targets-missing",
                "The AppProjectReference artifact targets were not found in the installed MAUI CLI.");
        }
        var propsPath = Path.ChangeExtension(targetsPath, ".props");
        if (!File.Exists(propsPath))
        {
            throw FlowExecutionException.Infrastructure(
                "app-project-reference-props-missing",
                "The AppProjectReference artifact props were not found in the installed MAUI CLI.");
        }

        ExecutionPathSafety.ValidateOutputDirectory(request.WorkDirectory);
        var resolutionRoot = Path.Combine(Path.GetFullPath(request.WorkDirectory), ".resolved-app");
        if (ExecutionPathSafety.EntryExists(resolutionRoot))
        {
            throw FlowExecutionException.Invalid(
                "artifact-work-directory-not-empty",
                "The execution output already contains an app-artifact resolution directory.");
        }

        Directory.CreateDirectory(resolutionRoot);
        ExecutionPathSafety.RejectReparsePoints(
            resolutionRoot,
            "artifact-build-root-reparse-point",
            "The invocation-owned build root cannot contain or traverse a symbolic link or reparse point.");
        var hostProjectPath = Path.Combine(resolutionRoot, "ResolveAppArtifact.proj");
        var metadataPath = Path.Combine(resolutionRoot, "resolved-artifacts.txt");
        var outputRoot = Path.Combine(resolutionRoot, "build");

        try
        {
            var windowsTarget = request.CandidateArtifactTypes.Any(static type =>
                    string.Equals(type, "exe", StringComparison.OrdinalIgnoreCase))
                ? await EvaluateWindowsTargetPathAsync(
                    projectPath,
                    targetFramework,
                    configuration,
                    runtimeIdentifier,
                    outputRoot,
                    cancellationToken).ConfigureAwait(false)
                : null;
            if (windowsTarget is not null)
            {
                return await BuildWindowsArtifactAsync(
                    request,
                    projectPath,
                    targetFramework,
                    configuration,
                    runtimeIdentifier,
                    outputRoot,
                    resolutionRoot,
                    windowsTarget,
                    cancellationToken).ConfigureAwait(false);
            }
            var expectedArtifactPath = windowsTarget?.TargetPath;
            ExecutionPathSafety.ValidateOutputDirectory(request.WorkDirectory);
            ExecutionPathSafety.RejectReparsePoints(
                resolutionRoot,
                "artifact-build-root-reparse-point",
                "The invocation-owned build root cannot contain or traverse a symbolic link or reparse point.");
            await using (var stream = new FileStream(
                hostProjectPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            await using (var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(
                    CreateHostProject(
                    propsPath,
                    targetsPath,
                    projectPath,
                    request.AgentSessionId,
                    targetFramework,
                    configuration,
                    runtimeIdentifier,
                    outputRoot,
                    expectedArtifactPath,
                    windowsTarget?.TargetPlatformMinVersion,
                    metadataPath)
                    .AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ExecutionPathSafety.ValidateOutputDirectory(request.WorkDirectory);
            ExecutionPathSafety.RejectReparsePoints(
                resolutionRoot,
                "artifact-build-root-reparse-point",
                "The invocation-owned build root cannot contain or traverse a symbolic link or reparse point.");
            var result = await _processRunner.RunAsync(
                "dotnet",
                [
                    "msbuild",
                    hostProjectPath,
                    "-nologo",
                    "-verbosity:minimal",
                    "-maxcpucount:1",
                    "-target:ResolveDevFlowAppArtifact",
                ],
                workingDirectory: Path.GetDirectoryName(projectPath),
                timeout: TimeSpan.FromMinutes(10),
                environmentVariablesToRemove: MsBuildEnvironmentVariablesToRemove,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.StandardOutput.Length > MaximumBuildOutputCharacters ||
                result.StandardError.Length > MaximumBuildOutputCharacters)
            {
                throw FlowExecutionException.Infrastructure(
                    "app-build-diagnostics-too-large",
                    "MSBuild diagnostics exceeded the bounded response size.");
            }
            if (!result.Success)
            {
                throw FlowExecutionException.Infrastructure(
                    "app-build-failed",
                    FormatBuildFailure(result));
            }
            if (!File.Exists(metadataPath))
            {
                throw FlowExecutionException.Infrastructure(
                    "artifact-metadata-missing",
                    "MSBuild completed without producing AppProjectReference artifact metadata.");
            }

            var metadataInfo = new FileInfo(metadataPath);
            ExecutionPathSafety.RejectReparsePoints(
                metadataPath,
                "artifact-metadata-reparse-point",
                "MSBuild returned artifact metadata through a symbolic link or reparse point.");
            if (metadataInfo.Length is <= 0 or > MaximumMetadataBytes)
            {
                throw FlowExecutionException.Infrastructure(
                    "artifact-metadata-size-invalid",
                    "MSBuild returned artifact metadata outside the bounded response size.");
            }
            var records = (await File.ReadAllLinesAsync(metadataPath, cancellationToken).ConfigureAwait(false))
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Take(MaximumArtifactRecords + 1)
                .Select(line => ParseRecord(line, request.AgentSessionId))
                .ToArray();
            if (records.Length > MaximumArtifactRecords)
            {
                throw FlowExecutionException.Infrastructure(
                    "artifact-metadata-count-invalid",
                    "MSBuild returned too many app artifact candidates.");
            }
            RejectExplicitlyUnsupportedArtifacts(
                records,
                projectPath,
                targetFramework,
                configuration,
                request);
            var selected = SelectSingleArtifact(
                records,
                projectPath,
                targetFramework,
                configuration,
                runtimeIdentifier,
                request.CandidateArtifactTypes);
            ExecutionPathSafety.ValidateConfinedArtifactPath(outputRoot, selected.Path);
            var digest = await ComputeArtifactDigestAsync(
                outputRoot,
                selected.Path,
                cancellationToken).ConfigureAwait(false);
            return selected with
            {
                PackageDigest = digest,
                OwnedOutputRoot = resolutionRoot,
            };
        }
        catch
        {
            TryDeleteDirectory(resolutionRoot);
            throw;
        }
    }

    internal static ResolvedAppArtifact SelectSingleArtifact(
        IEnumerable<ResolvedAppArtifact> candidates,
        string projectPath,
        string targetFramework,
        string configuration,
        string? runtimeIdentifier = null,
        IReadOnlyCollection<string>? candidateArtifactTypes = null)
    {
        var materialized = candidates.ToArray();
        var matches = materialized
            .Where(candidate =>
                PathsEqual(candidate.ProjectPath, projectPath) &&
                string.Equals(candidate.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Configuration, configuration, StringComparison.OrdinalIgnoreCase) &&
                (runtimeIdentifier is null ||
                 string.Equals(
                     candidate.RuntimeIdentifier,
                     runtimeIdentifier,
                     StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (candidateArtifactTypes is { Count: > 0 })
        {
            matches = matches
                .Where(candidate => candidateArtifactTypes.Any(type =>
                    string.Equals(type, candidate.ArtifactType, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        if (matches.Length == 0)
        {
            var facts = materialized
                .Take(4)
                .Select(candidate =>
                    $"tfm={candidate.TargetFramework},configuration={candidate.Configuration},rid={candidate.RuntimeIdentifier ?? "none"},projectMatch={PathsEqual(candidate.ProjectPath, projectPath)}")
                .ToArray();
            throw FlowExecutionException.Invalid(
                "artifact-not-found",
                "No AppProjectReference artifact matched the requested project, target framework, and configuration." +
                (facts.Length == 0 ? "" : " Candidate facts: " + string.Join("; ", facts)));
        }
        if (matches.Length > 1)
        {
            throw FlowExecutionException.Invalid(
                "artifact-ambiguous",
                "Multiple AppProjectReference artifacts matched the requested build. Specify build inputs that produce exactly one deployable artifact.");
        }
        return matches[0];
    }

    private static void RejectExplicitlyUnsupportedArtifacts(
        IEnumerable<ResolvedAppArtifact> candidates,
        string projectPath,
        string targetFramework,
        string configuration,
        AppArtifactResolutionRequest request)
    {
        if (request.UnsupportedArtifactTypes.Count == 0)
            return;
        var unsupported = candidates
            .Where(candidate =>
                PathsEqual(candidate.ProjectPath, projectPath) &&
                string.Equals(candidate.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Configuration, configuration, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(request.RuntimeIdentifier) ||
                 string.Equals(
                     candidate.RuntimeIdentifier,
                     request.RuntimeIdentifier,
                     StringComparison.OrdinalIgnoreCase)) &&
                request.UnsupportedArtifactTypes.Any(type =>
                    string.Equals(type, candidate.ArtifactType, StringComparison.OrdinalIgnoreCase)))
            .Select(static candidate => candidate.ArtifactType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length == 0)
            return;
        throw FlowExecutionException.Unsupported(
            request.UnsupportedArtifactCode ?? $"{SafeCode(request.Platform)}-artifact-type-unsupported",
            request.UnsupportedArtifactMessage ??
            $"The resolved {request.Platform} build produced unsupported artifact types: {string.Join(", ", unsupported)}.");
    }

    private static ResolvedAppArtifact ParseRecord(string line, string agentSessionId)
    {
        var fields = line.Split(Separator, StringSplitOptions.None);
        if (fields.Length != 17)
        {
            throw FlowExecutionException.Infrastructure(
                "artifact-metadata-invalid",
                "MSBuild returned malformed AppProjectReference artifact metadata.");
        }

        var path = Path.GetFullPath(fields[0]);
        var projectPath = Path.GetFullPath(fields[2]);
        if (!bool.TryParse(fields[15], out var installable) ||
            !bool.TryParse(fields[16], out var launchable))
        {
            throw FlowExecutionException.Infrastructure(
                "artifact-metadata-invalid",
                "MSBuild returned invalid AppProjectReference installable or launchable metadata.");
        }

        return new ResolvedAppArtifact
        {
            Path = path,
            ReferenceName = EmptyToNull(fields[1]),
            ProjectPath = projectPath,
            AgentSessionId = agentSessionId,
            TargetFramework = fields[3],
            TargetPlatformIdentifier = EmptyToNull(fields[4]),
            RuntimeIdentifier = EmptyToNull(fields[5]),
            Configuration = fields[6],
            ApplicationId = EmptyToNull(fields[7]),
            ArtifactType = fields[8],
            ArtifactContractVersion = EmptyToNull(fields[9]),
            ArtifactRole = EmptyToNull(fields[10]),
            TargetRuntimeKind = EmptyToNull(fields[11]),
            DeploymentModel = EmptyToNull(fields[12]),
            LaunchIdentityKind = EmptyToNull(fields[13]),
            LaunchIdentity = EmptyToNull(fields[14]),
            Installable = installable,
            Launchable = launchable,
            PackageDigest = "",
        };
    }

    private static string CreateHostProject(
        string propsPath,
        string targetsPath,
        string projectPath,
        string agentSessionId,
        string targetFramework,
        string configuration,
        string? runtimeIdentifier,
        string outputRoot,
        string? expectedArtifactPath,
        string? targetPlatformMinVersion,
        string metadataPath)
    {
        static string Escape(string value) => SecurityElement.Escape(value) ?? "";

        var transform = string.Join(
            Separator,
            [
                "%(FullPath)",
                "%(ReferenceName)",
                "%(ProjectPath)",
                "%(TargetFramework)",
                "%(TargetPlatformIdentifier)",
                "%(RuntimeIdentifier)",
                "%(Configuration)",
                "%(ApplicationId)",
                "%(ArtifactType)",
                "%(ArtifactContractVersion)",
                "%(ArtifactRole)",
                "%(TargetRuntimeKind)",
                "%(DeploymentModel)",
                "%(LaunchIdentityKind)",
                "%(LaunchIdentity)",
                "%(Installable)",
                "%(Launchable)",
            ]);
        var childProperties =
            $"MauiDevFlowEnabled=true;MauiDevFlowSessionId={agentSessionId}";
        if (!string.IsNullOrWhiteSpace(targetPlatformMinVersion))
            childProperties += $";TargetPlatformMinVersion={targetPlatformMinVersion}";

        return $"""
            <Project>
              <Import Project="{Escape(propsPath)}" />
              <Import Project="{Escape(targetsPath)}" />
              <ItemGroup>
                <MauiAppProjectReference Include="{Escape(projectPath)}">
                  <TargetFramework>{Escape(targetFramework)}</TargetFramework>
                  <Configuration>{Escape(configuration)}</Configuration>
                  <RuntimeIdentifier>{Escape(runtimeIdentifier ?? "")}</RuntimeIdentifier>
                  <OutputRoot>{Escape(outputRoot)}</OutputRoot>
                  <ExpectedArtifact>{Escape(expectedArtifactPath ?? "")}</ExpectedArtifact>
                  <SetPlatformOutputPaths>true</SetPlatformOutputPaths>
                  <Properties>{Escape(childProperties)}</Properties>
                </MauiAppProjectReference>
              </ItemGroup>
              <Target Name="ResolveDevFlowAppArtifact"
                      DependsOnTargets="BuildAppProjectReferences">
                <WriteLinesToFile File="{Escape(metadataPath)}"
                                  Lines="@(MauiAppArtifact->'{Escape(transform)}')"
                                  Overwrite="true"
                                  Encoding="UTF-8" />
              </Target>
            </Project>
            """;
    }

    private static string ValidateProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw FlowExecutionException.Invalid("project-path-missing", "An app project path is required.");
        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw FlowExecutionException.Invalid("project-not-found", "The app project must be an existing .csproj file.");
        }
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw FlowExecutionException.Invalid("project-reparse-point", "The app project cannot be a symbolic link or reparse point.");
        return fullPath;
    }

    private static string ResolveTargetFramework(
        string projectPath,
        string? requested,
        string platform,
        IReadOnlyCollection<string> targetPlatformIdentifiers)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var value = requested.Trim();
            if (!IsSafeMsBuildValue(value))
                throw FlowExecutionException.Invalid("target-framework-invalid", "The target framework is not valid.");
            if (targetPlatformIdentifiers.Count > 0 &&
                !targetPlatformIdentifiers.Any(identifier => TargetFrameworkMatchesPlatform(value, identifier)))
            {
                throw FlowExecutionException.Unsupported(
                    $"{SafeCode(platform)}-target-framework-unsupported",
                    $"The requested target framework does not target {platform}.");
            }
            return value;
        }

        var platformFrameworks = MauiProjectResolver.GetTargetFrameworks(projectPath)
            .Where(framework =>
                targetPlatformIdentifiers.Count == 0 ||
                targetPlatformIdentifiers.Any(identifier => TargetFrameworkMatchesPlatform(framework, identifier)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return platformFrameworks.Length switch
        {
            1 => platformFrameworks[0],
            0 => throw FlowExecutionException.Invalid(
                $"{SafeCode(platform)}-target-framework-missing",
                $"The app project does not expose a {platform} target framework."),
            _ => throw FlowExecutionException.Invalid(
                $"{SafeCode(platform)}-target-framework-ambiguous",
                $"The app project exposes multiple {platform} target frameworks. Specify one with --framework."),
        };
    }

    private static bool TargetFrameworkMatchesPlatform(string targetFramework, string identifier)
        => targetFramework.Contains("-" + identifier, StringComparison.OrdinalIgnoreCase);

    private static string? FindAppProjectReferenceTargets()
    {
        var packaged = Path.Combine(
            AppContext.BaseDirectory,
            "Build",
            "AppProjectReference",
            "Microsoft.Maui.Build.AppProjectReference.targets");
        if (File.Exists(packaged))
            return packaged;

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current is not null)
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "src",
                    "AppProjectReference",
                    "Microsoft.Maui.Build.AppProjectReference",
                    "build",
                    "Microsoft.Maui.Build.AppProjectReference.targets");
                if (File.Exists(candidate))
                    return candidate;
                current = current.Parent;
            }
        }
        return null;
    }

    private static bool IsSafeMsBuildValue(string value)
        => value is { Length: > 0 and <= 256 } &&
           value.All(static character =>
               char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '+');

    private static void ValidateRuntimeIdentifier(
        string? runtimeIdentifier,
        string targetFramework,
        string platform,
        IReadOnlyCollection<string> targetPlatformIdentifiers)
    {
        if (runtimeIdentifier is null || targetPlatformIdentifiers.Count == 0)
            return;

        var compatible = targetPlatformIdentifiers.Any(identifier =>
            identifier.ToLowerInvariant() switch
            {
                "android" => runtimeIdentifier.StartsWith("android-", StringComparison.OrdinalIgnoreCase),
                "ios" => runtimeIdentifier.StartsWith("ios-", StringComparison.OrdinalIgnoreCase) ||
                    runtimeIdentifier.StartsWith("iossimulator-", StringComparison.OrdinalIgnoreCase),
                "maccatalyst" => runtimeIdentifier.StartsWith("maccatalyst-", StringComparison.OrdinalIgnoreCase),
                "macos" => runtimeIdentifier.StartsWith("osx-", StringComparison.OrdinalIgnoreCase),
                "windows" => runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase),
                _ => false,
            });
        if (!compatible)
        {
            throw FlowExecutionException.Invalid(
                "runtime-identifier-platform-mismatch",
                $"The runtime identifier does not match the requested {platform} target framework '{targetFramework}'.");
        }
    }

    private async Task<string> ComputeArtifactDigestAsync(
        string ownedRoot,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_inspectionLimits.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            ExecutionPathSafety.ValidateConfinedArtifactPath(ownedRoot, path);
            byte[] bytes;
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                ValidateFileSize(info);
                var initialLength = info.Length;
                await using var stream = new FileStream(
                    path,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    });
                bytes = await SHA256.HashDataAsync(stream, linked.Token).ConfigureAwait(false);
                info.Refresh();
                ValidateFileSize(info);
                if (info.Length != initialLength)
                {
                    throw FlowExecutionException.Infrastructure(
                        "artifact-changed-during-inspection",
                        "The resolved app artifact changed while it was being inspected.");
                }
            }
            else
            {
                bytes = await ComputeDirectoryDigestAsync(path, linked.Token).ConfigureAwait(false);
            }
            return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeout.IsCancellationRequested)
        {
            throw FlowExecutionException.Infrastructure(
                "artifact-inspection-timeout",
                "The resolved app artifact could not be inspected within the bounded timeout.");
        }
        catch (FlowExecutionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FlowExecutionException.Infrastructure(
                "artifact-inspection-failed",
                "The resolved app artifact changed or could not be read safely.",
                ex);
        }
    }

    private async Task<byte[]> ComputeDirectoryDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(Path.GetFullPath(path)), 0));
        var buffer = new byte[81920];
        var fileCount = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            if (depth > _inspectionLimits.MaximumDepth)
            {
                throw FlowExecutionException.Infrastructure(
                    "artifact-directory-depth-exceeded",
                    "The resolved app artifact exceeded the bounded directory depth.");
            }
            directory.Refresh();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw FlowExecutionException.Infrastructure(
                    "artifact-reparse-point",
                    "The resolved app artifact cannot contain a symbolic link or reparse point.");
            }
            var entries = directory
                .EnumerateFileSystemInfos()
                .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            for (var index = entries.Length - 1; index >= 0; index--)
            {
                var entry = entries[index];
                var relativePath = Path.GetRelativePath(path, entry.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw FlowExecutionException.Infrastructure(
                        "artifact-reparse-point",
                        "The resolved app artifact cannot contain a symbolic link or reparse point.");
                }
                if (entry is DirectoryInfo childDirectory)
                {
                    AppendHashString(hash, "directory:" + relativePath);
                    pending.Push((childDirectory, depth + 1));
                    continue;
                }

                var file = (FileInfo)entry;
                ValidateFileSize(file);
                var initialLength = file.Length;
                fileCount++;
                if (fileCount > _inspectionLimits.MaximumFiles)
                {
                    throw FlowExecutionException.Infrastructure(
                        "artifact-file-count-exceeded",
                        "The resolved app artifact exceeded the bounded file count.");
                }
                if (file.Length > _inspectionLimits.MaximumTotalBytes - totalBytes)
                {
                    throw FlowExecutionException.Infrastructure(
                        "artifact-total-size-exceeded",
                        "The resolved app artifact exceeded the bounded total size.");
                }
                totalBytes += file.Length;
                AppendHashString(hash, "file:" + relativePath);
                hash.AppendData(BitConverter.GetBytes(file.Length));
                await using var stream = new FileStream(
                    entry.FullName,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    });
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    hash.AppendData(buffer, 0, read);
                file.Refresh();
                ValidateFileSize(file);
                if (file.Length != initialLength)
                {
                    throw FlowExecutionException.Infrastructure(
                        "artifact-changed-during-inspection",
                        "The resolved app artifact changed while it was being inspected.");
                }
            }
        }
        return hash.GetHashAndReset();
    }

    private void ValidateFileSize(FileInfo info)
    {
        if (info.Length < 0 || info.Length > _inspectionLimits.MaximumFileBytes)
        {
            throw FlowExecutionException.Infrastructure(
                "artifact-file-size-exceeded",
                "The resolved app artifact contains a file outside the bounded size.");
        }
    }

    private static string FormatBuildFailure(ProcessResult result)
    {
        var combined = string.Join(
                "\n",
                new[] { result.StandardError, result.StandardOutput }
                    .Where(static value => !string.IsNullOrWhiteSpace(value)))
            .Trim();
        if (combined.Length == 0)
            return $"MSBuild could not resolve the app artifact (exit code {result.ExitCode}).";

        var safe = new string(combined
            .Select(static character =>
                character is '\r' or '\n' or '\t' || !char.IsControl(character)
                    ? character
                    : ' ')
            .ToArray());
        if (safe.Length > MaximumBuildDiagnosticCharacters)
            safe = safe[^MaximumBuildDiagnosticCharacters..];
        return $"MSBuild could not resolve the app artifact (exit code {result.ExitCode}). Diagnostics: {safe}";
    }

    private static void AppendHashString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private async Task<WindowsTargetEvaluation> EvaluateWindowsTargetPathAsync(
        string projectPath,
        string targetFramework,
        string configuration,
        string? runtimeIdentifier,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var expectedOutputRoot = Path.Combine(outputRoot, "bin");
        var arguments = new List<string>
        {
            "msbuild",
            projectPath,
            "-nologo",
            "-verbosity:quiet",
            $"-property:TargetFramework={targetFramework}",
            $"-property:Configuration={configuration}",
            $"-property:OutputPath={expectedOutputRoot}{Path.DirectorySeparatorChar}",
        };
        if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
            arguments.Add($"-property:RuntimeIdentifier={runtimeIdentifier}");
        arguments.Add("-getProperty:TargetPath,AssemblyName,TargetFramework,Configuration,RuntimeIdentifier,TargetPlatformMinVersion,ApplicationId");

        var result = await _processRunner.RunAsync(
            "dotnet",
            arguments,
            workingDirectory: Path.GetDirectoryName(projectPath),
            timeout: TimeSpan.FromMinutes(2),
            environmentVariablesToRemove: MsBuildEnvironmentVariablesToRemove,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success ||
            result.StandardOutput.Length > MaximumBuildOutputCharacters ||
            result.StandardError.Length > MaximumBuildOutputCharacters)
        {
            throw FlowExecutionException.Infrastructure(
                "windows-target-evaluation-failed",
                "MSBuild could not evaluate the exact Windows TargetPath and AssemblyName.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                result.StandardOutput.Trim(),
                new JsonDocumentOptions { MaxDepth = 8 });
            var properties = document.RootElement.GetProperty("Properties");
            var targetPath = properties.GetProperty("TargetPath").GetString();
            var assemblyName = properties.GetProperty("AssemblyName").GetString();
            var evaluatedTargetFramework = properties.GetProperty("TargetFramework").GetString();
            var evaluatedConfiguration = properties.GetProperty("Configuration").GetString();
            var evaluatedRuntimeIdentifier = properties.GetProperty("RuntimeIdentifier").GetString();
            var targetPlatformMinVersion = properties.GetProperty("TargetPlatformMinVersion").GetString();
            var applicationId = properties.GetProperty("ApplicationId").GetString();
            if (string.IsNullOrWhiteSpace(targetPath) ||
                string.IsNullOrWhiteSpace(assemblyName) ||
                !string.Equals(evaluatedTargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(evaluatedConfiguration, configuration, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(runtimeIdentifier) &&
                 !string.Equals(
                     evaluatedRuntimeIdentifier,
                     runtimeIdentifier,
                     StringComparison.OrdinalIgnoreCase)))
            {
                throw InvalidWindowsTargetEvaluation();
            }
            targetPlatformMinVersion = ResolveWindowsTargetPlatformMinVersion(
                targetPlatformMinVersion,
                targetFramework);

            var fullTargetPath = Path.GetFullPath(
                targetPath,
                Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory);
            var relative = Path.GetRelativePath(expectedOutputRoot, fullTargetPath);
            var targetExtension = Path.GetExtension(fullTargetPath);
            if (Path.IsPathRooted(relative) ||
                relative.StartsWith("..", StringComparison.Ordinal) ||
                (!string.Equals(targetExtension, ".dll", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(targetExtension, ".exe", StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(fullTargetPath),
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidWindowsTargetEvaluation();
            }
            return new WindowsTargetEvaluation(
                string.Equals(targetExtension, ".exe", StringComparison.OrdinalIgnoreCase)
                    ? fullTargetPath
                    : Path.Combine(Path.GetDirectoryName(fullTargetPath)!, assemblyName + ".exe"),
                targetPlatformMinVersion,
                EmptyToNull(evaluatedRuntimeIdentifier),
                EmptyToNull(applicationId));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw FlowExecutionException.Infrastructure(
                "windows-target-evaluation-invalid",
                "MSBuild returned incomplete or invalid Windows TargetPath and AssemblyName properties.",
                ex);
        }
    }

    private static FlowExecutionException InvalidWindowsTargetEvaluation()
        => FlowExecutionException.Infrastructure(
            "windows-target-evaluation-invalid",
            "MSBuild returned Windows TargetPath or AssemblyName values that did not match the requested build.");

    private static string ResolveWindowsTargetPlatformMinVersion(
        string? evaluated,
        string targetFramework)
    {
        var windowsMarker = targetFramework.IndexOf("-windows", StringComparison.OrdinalIgnoreCase);
        var value = evaluated?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            if (windowsMarker < 0 ||
                windowsMarker + "-windows".Length >= targetFramework.Length)
            {
                throw InvalidWindowsTargetEvaluation();
            }
            value = targetFramework[(windowsMarker + "-windows".Length)..];
        }
        if (!IsSafeMsBuildValue(value) ||
            !Version.TryParse(value, out _))
        {
            throw InvalidWindowsTargetEvaluation();
        }
        return value;
    }

    private async Task<ResolvedAppArtifact> BuildWindowsArtifactAsync(
        AppArtifactResolutionRequest request,
        string projectPath,
        string targetFramework,
        string configuration,
        string? requestedRuntimeIdentifier,
        string outputRoot,
        string resolutionRoot,
        WindowsTargetEvaluation target,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "build",
            projectPath,
            "-nologo",
            "-verbosity:minimal",
            $"--framework={targetFramework}",
            $"--configuration={configuration}",
            $"-property:OutputPath={Path.Combine(outputRoot, "bin")}{Path.DirectorySeparatorChar}",
            $"-property:TargetPlatformMinVersion={target.TargetPlatformMinVersion}",
            $"-property:MauiDevFlowEnabled=true",
            $"-property:MauiDevFlowSessionId={request.AgentSessionId}",
        };
        var result = await _processRunner.RunAsync(
            "dotnet",
            arguments,
            workingDirectory: Path.GetDirectoryName(projectPath),
            timeout: TimeSpan.FromMinutes(10),
            environmentVariablesToRemove: MsBuildEnvironmentVariablesToRemove,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.StandardOutput.Length > MaximumBuildOutputCharacters ||
            result.StandardError.Length > MaximumBuildOutputCharacters)
        {
            throw FlowExecutionException.Infrastructure(
                "app-build-diagnostics-too-large",
                "MSBuild diagnostics exceeded the bounded response size.");
        }
        if (!result.Success)
            throw FlowExecutionException.Infrastructure("app-build-failed", FormatBuildFailure(result));
        if (!File.Exists(target.TargetPath))
        {
            throw FlowExecutionException.Infrastructure(
                "artifact-not-found",
                "The exact Windows build completed without producing its evaluated executable.");
        }

        ExecutionPathSafety.ValidateConfinedArtifactPath(outputRoot, target.TargetPath);
        return new ResolvedAppArtifact
        {
            Path = target.TargetPath,
            ProjectPath = projectPath,
            AgentSessionId = request.AgentSessionId,
            TargetFramework = targetFramework,
            TargetPlatformIdentifier = "windows",
            RuntimeIdentifier = requestedRuntimeIdentifier ?? target.RuntimeIdentifier,
            Configuration = configuration,
            ApplicationId = target.ApplicationId,
            ArtifactType = "exe",
            ArtifactContractVersion = "1",
            ArtifactRole = "launcher",
            TargetRuntimeKind = "windows",
            DeploymentModel = "executable",
            LaunchIdentityKind = "file-path",
            LaunchIdentity = target.TargetPath,
            Installable = false,
            Launchable = true,
            PackageDigest = await ComputeArtifactDigestAsync(
                outputRoot,
                target.TargetPath,
                cancellationToken).ConfigureAwait(false),
            OwnedOutputRoot = resolutionRoot,
        };
    }

    private sealed record WindowsTargetEvaluation(
        string TargetPath,
        string TargetPlatformMinVersion,
        string? RuntimeIdentifier,
        string? ApplicationId);


    private static string SafeCode(string value)
        => new(value
            .Trim()
            .Select(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                    ? char.ToLowerInvariant(character)
                    : '-')
            .ToArray());

    private static bool PathsEqual(string first, string second)
        => string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? EmptyToNull(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        try
        {
            if (Directory.Exists(path))
            {
                ExecutionPathSafety.RejectReparsePoints(
                    path,
                    "artifact-cleanup-reparse-point",
                    "The invocation-owned build root changed to a symbolic link or reparse point.");
                var pending = new Stack<DirectoryInfo>();
                pending.Push(new DirectoryInfo(Path.GetFullPath(path)));
                while (pending.Count > 0)
                {
                    foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                            return false;
                        if (entry is DirectoryInfo directory)
                            pending.Push(directory);
                    }
                }
                Directory.Delete(path, recursive: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
