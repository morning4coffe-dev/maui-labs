using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Inspector;

/// <summary>
/// Builds and applies the deliberately narrow XAML AutomationId source-proposal format. This
/// service is a local host component: it never changes flow selectors and it does not issue
/// approvals or grants.
/// </summary>
internal sealed class XamlAutomationIdProposalService
{
    private const long MaxSourceFileBytes = 5 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string? _project;
    private readonly string? _sessionId;

    internal XamlAutomationIdProposalService(string? project, string? sessionId = null)
    {
        _project = string.IsNullOrWhiteSpace(project) ? null : project;
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    internal async Task<XamlSourceProposalBuildResult> BuildAsync(
        ElementInfo element,
        string? proposedAutomationId,
        IEnumerable<ElementInfo>? liveElements,
        IReadOnlyList<MauiXamlSourceFlowFollowUp>? affectedFlows = null,
        IReadOnlyList<MauiXamlSourcePlatformVerification>? affectedPlatforms = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(element);
        var path = ResolveSourcePath(element.SourceFile);
        if (!path.Ok)
            return XamlSourceProposalBuildResult.Failure(path.Code!, path.Error!);

        var sourcePath = path.Path!;
        SourceFileSnapshot snapshot;
        try
        {
            snapshot = await SourceFileSnapshot.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceFileTooLargeException)
        {
            return XamlSourceProposalBuildResult.Failure(
                "source-file-too-large",
                "XAML source files larger than 5 MB are not eligible for source proposals.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return XamlSourceProposalBuildResult.Failure(
                "source-unavailable",
                "The mapped XAML source file could not be read.");
        }

        var projectRoot = path.ProjectRoot!;
        var relativePath = Path.GetRelativePath(projectRoot, sourcePath);
        var linked = IsLinkedXaml(projectRoot, sourcePath);
        var generated = IsGeneratedXaml(relativePath, snapshot.Text);
        var projectIds = await CollectProjectAutomationIdsAsync(
            projectRoot,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        var liveIds = liveElements is null
            ? null
            : Flatten(liveElements)
                .Select(static item => item.AutomationId)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToList();

        var analysis = MauiXamlSourceEligibilityAnalyzer.Analyze(new MauiXamlSourceEligibilityInput
        {
            SourceText = snapshot.Text,
            FileRelativePath = relativePath,
            ExpectedSourceHash = element.SourceHash,
            SourceLine = element.SourceLine,
            SourceColumn = element.SourceColumn,
            SourceConfidence = element.SourceConfidence,
            IsProjectContained = true,
            IsRegisteredProjectFile = IsRegisteredXaml(projectRoot, sourcePath),
            IsGenerated = generated,
            IsLinked = linked,
            HasReparsePoint = XamlSourcePropertyEditor.PathContainsReparsePoint(projectRoot, sourcePath),
            IsNativeOrWebViewSynthetic = IsNativeOrSynthetic(element),
            IsVirtualized = element.IsVirtualized == true,
            TemplateKind = element.TemplateKind,
            ProposedAutomationId = proposedAutomationId,
            ProjectAutomationIds = projectIds,
            LiveAutomationIds = liveIds,
            LiveUniquenessAvailable = liveElements is not null,
            RequireLiveUniqueness = true,
        });
        if (!analysis.Decision.Eligible ||
            analysis.Element is null ||
            analysis.StartTagEnd is null)
        {
            return XamlSourceProposalBuildResult.Ineligible(analysis);
        }

        var patch = CreatePatch(snapshot.Text, analysis, proposedAutomationId!);
        if (patch is null)
        {
            return XamlSourceProposalBuildResult.Failure(
                "xaml-patch-invalid",
                "The mapped XAML declaration could not be patched safely.");
        }

        var updated = snapshot.Text.Remove(patch.Value.Start, patch.Value.Length)
            .Insert(patch.Value.Start, patch.Value.Replacement);
        try
        {
            _ = XDocument.Parse(updated, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return XamlSourceProposalBuildResult.Failure(
                "xaml-patch-invalid",
                "The proposed literal would make the XAML invalid.");
        }

        var afterBytes = snapshot.Encode(updated);
        var beforeDigest = snapshot.ContentDigest;
        var afterDigest = MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(afterBytes);
        var sourceAnchor = analysis.Element.SourceAnchor;
        var patchDigest = Digest(string.Join("\n",
            "xaml-automation-id-v1",
            relativePath,
            element.SourceHash,
            beforeDigest,
            sourceAnchor,
            patch.Value.Start,
            patch.Value.Length,
            patch.Value.Replacement));
        var beforeTag = snapshot.Text[
            analysis.Element.StartTagOffset!.Value..
            (analysis.Element.StartTagOffset.Value + analysis.Element.StartTagLength!.Value)];
        var afterTagStart = analysis.Element.StartTagOffset.Value;
        var afterTag = updated[
            afterTagStart..
            (afterTagStart + AdjustedTagLength(
                analysis.Element.StartTagLength.Value,
                patch.Value,
                analysis.Element.StartTagOffset.Value))];
        var diff = CreateDiff(
            relativePath,
            analysis.Element.Line ?? element.SourceLine ?? 1,
            beforeTag,
            afterTag);
        var flowFollowUps = await DiscoverAffectedFlowsAsync(
            projectRoot,
            analysis.OldAutomationId,
            proposedAutomationId!,
            affectedFlows,
            cancellationToken).ConfigureAwait(false);
        var operationKind = analysis.OldAutomationId is null
            ? "add-literal-automation-id"
            : "replace-literal-automation-id";
        var proposal = new MauiXamlSourceProposal
        {
            ProposalId = OpaqueId("xamlproposal"),
            State = MauiXamlSourceProposalStates.Proposed,
            CreatedAt = DateTimeOffset.UtcNow,
            Operation = new MauiXamlSourceOperation
            {
                OperationId = OpaqueId("xamlop"),
                Kind = operationKind,
                FileRelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                SourceHash = element.SourceHash,
                SourceAnchor = sourceAnchor,
                OldLiteral = analysis.OldAutomationId,
                NewLiteral = proposedAutomationId,
                Attribute = "AutomationId",
            },
            Element = analysis.Element,
            BaseContentDigest = beforeDigest,
            Patch = new MauiXamlSourcePatch
            {
                Format = "text-replace-v1",
                Operation = operationKind,
                BeforeDigest = beforeDigest,
                AfterDigest = afterDigest,
                Start = patch.Value.Start,
                Length = patch.Value.Length,
                Replacement = patch.Value.Replacement,
            },
            PatchDigest = patchDigest,
            DiffDigest = Digest(diff),
            Diff = diff,
            Eligibility = analysis.Decision,
            Uniqueness = analysis.Uniqueness,
            AffectedFlows = flowFollowUps,
            AffectedPlatforms = EnsureOfficialPlatformCoverage(affectedPlatforms),
            RiskFlags =
            [
                "source-write-requires-separate-human-approval",
                "flow-selector-changes-require-separate-flow-repair",
                "build-remap-replay-and-oracle-verification-required",
            ],
        };
        return XamlSourceProposalBuildResult.Success(proposal, snapshot.ContentDigest, afterDigest, analysis);
    }

    /// <summary>
    /// Performs the local host's compare-and-swap source write. Callers must obtain a separate
    /// source-proposal grant before calling this method; this service deliberately has no grant
    /// issuance authority.
    /// </summary>
    internal async Task<XamlSourceProposalWriteResult> ApplyAsync(
        MauiXamlSourceProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!ValidateProposalShape(proposal, out var error))
            return XamlSourceProposalWriteResult.Failure("proposal-invalid", error!);

        var path = ResolveProposalPath(proposal);
        if (!path.Ok)
            return XamlSourceProposalWriteResult.Failure(path.Code!, path.Error!);

        var sourcePath = path.Path!;
        var gate = FileLocks.GetOrAdd(sourcePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SourceFileSnapshot snapshot;
            try
            {
                snapshot = await SourceFileSnapshot.ReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceFileTooLargeException)
            {
                return XamlSourceProposalWriteResult.Failure("source-file-too-large", "The XAML source file is too large.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                return XamlSourceProposalWriteResult.Failure("source-unavailable", "The XAML source file could not be read.");
            }

            if (!FixedEquals(snapshot.ContentDigest, proposal.BaseContentDigest) ||
                !string.Equals(
                    MauiXamlSourceEligibilityAnalyzer.ComputeSourceHash(snapshot.Text),
                    proposal.Operation.SourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return XamlSourceProposalWriteResult.Failure(
                    MauiXamlSourceProposalStates.Stale,
                    "The XAML source changed after preview. No source change was written.");
            }

            var patch = proposal.Patch;
            if (patch.Start is not >= 0 ||
                patch.Length is not >= 0 ||
                patch.Replacement is null ||
                patch.Start.Value > snapshot.Text.Length ||
                patch.Length.Value > snapshot.Text.Length - patch.Start.Value)
            {
                return XamlSourceProposalWriteResult.Failure("proposal-invalid", "The source proposal patch is invalid.");
            }

            var updated = snapshot.Text.Remove(patch.Start.Value, patch.Length.Value)
                .Insert(patch.Start.Value, patch.Replacement);
            var expectedAfterBytes = snapshot.Encode(updated);
            var expectedAfterDigest = MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(expectedAfterBytes);
            if (!FixedEquals(expectedAfterDigest, patch.AfterDigest))
            {
                return XamlSourceProposalWriteResult.Failure(
                    "proposal-invalid",
                    "The proposed source patch digest does not match its declared result.");
            }

            // Reparse before the write so a malformed patch is never persisted.
            try
            {
                _ = XDocument.Parse(updated, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return XamlSourceProposalWriteResult.Failure("proposal-invalid", "The source patch is not well-formed XAML.");
            }

            var write = await AtomicFileWriter.WriteIfDigestMatchesAsync(
                sourcePath,
                expectedAfterBytes,
                snapshot.ContentDigest,
                () => ValidateProposalPath(proposal, sourcePath),
                cancellationToken).ConfigureAwait(false);
            if (!write.Ok)
                return XamlSourceProposalWriteResult.Failure(write.Code!, write.Error!);

            return XamlSourceProposalWriteResult.Success(
                write.ContentDigest!,
                snapshot.Bytes,
                snapshot.ContentDigest);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Atomically restores the exact pre-apply bytes after failed verification.</summary>
    internal async Task<XamlSourceProposalWriteResult> RollbackAsync(
        MauiXamlSourceProposal proposal,
        byte[] originalBytes,
        string expectedAppliedContentDigest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(originalBytes);
        if (!ValidateProposalShape(proposal, out var error))
            return XamlSourceProposalWriteResult.Failure("proposal-invalid", error!);
        var path = ResolveProposalPath(proposal);
        if (!path.Ok)
            return XamlSourceProposalWriteResult.Failure(path.Code!, path.Error!);

        var sourcePath = path.Path!;
        var gate = FileLocks.GetOrAdd(sourcePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var write = await AtomicFileWriter.WriteIfDigestMatchesAsync(
                sourcePath,
                originalBytes,
                expectedAppliedContentDigest,
                () => ValidateProposalPath(proposal, sourcePath),
                cancellationToken).ConfigureAwait(false);
            return write.Ok
                ? XamlSourceProposalWriteResult.Success(
                    write.ContentDigest!,
                    originalBytes,
                    MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(originalBytes))
                : XamlSourceProposalWriteResult.Failure(write.Code!, write.Error!);
        }
        finally
        {
            gate.Release();
        }
    }

    private SourcePathResult ResolveSourcePath(string? sourceFile)
    {
        if (_project is null)
        {
            return SourcePathResult.Failure(
                "project-unregistered",
                "A broker-registered project identity is required for source proposals.");
        }
        if (string.IsNullOrWhiteSpace(sourceFile) || !Path.IsPathFullyQualified(sourceFile))
        {
            return SourcePathResult.Failure(
                MauiXamlSourceIneligibilityCodes.SourceMapUnavailable,
                "The element has no absolute mapped XAML source path.");
        }

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(sourceFile);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SourcePathResult.Failure("source-path-invalid", "The mapped XAML source path is invalid.");
        }

        var projectRoot = XamlSourcePropertyEditor.FindProjectRoot(sourcePath, _project, _sessionId);
        if (projectRoot is null || !XamlSourcePropertyEditor.IsUnderRoot(sourcePath, projectRoot))
        {
            return SourcePathResult.Failure(
                MauiXamlSourceIneligibilityCodes.SourceFileOutsideProject,
                "Only XAML files under the registered project are eligible.");
        }
        var validation = ValidateSourcePath(sourcePath, projectRoot);
        if (!validation.Ok)
            return validation;
        return IsRegisteredXaml(projectRoot, sourcePath)
            ? validation
            : SourcePathResult.Failure(
                MauiXamlSourceIneligibilityCodes.SourceFileUnregistered,
                "The mapped XAML file is not registered by the current project.");
    }

    private SourcePathResult ResolveProposalPath(MauiXamlSourceProposal proposal)
    {
        if (_project is null || string.IsNullOrWhiteSpace(proposal.Operation.FileRelativePath))
        {
            return SourcePathResult.Failure("project-unregistered", "A registered project and proposal path are required.");
        }
        var projectRoot = ResolveProjectRoot();
        if (projectRoot is null)
            return SourcePathResult.Failure("project-unregistered", "The registered project root is unavailable.");

        string path;
        try
        {
            path = Path.GetFullPath(Path.Combine(projectRoot, proposal.Operation.FileRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SourcePathResult.Failure("source-path-invalid", "The proposed source path is invalid.");
        }
        if (!XamlSourcePropertyEditor.IsUnderRoot(path, projectRoot))
        {
            return SourcePathResult.Failure(
                MauiXamlSourceIneligibilityCodes.SourceFileOutsideProject,
                "The proposal source path is outside the registered project.");
        }
        var validation = ValidateSourcePath(path, projectRoot);
        if (!validation.Ok)
            return validation;
        return IsRegisteredXaml(projectRoot, path)
            ? validation
            : SourcePathResult.Failure(
                MauiXamlSourceIneligibilityCodes.SourceFileUnregistered,
                "The proposal XAML file is not registered by the current project.");
    }

    private SourcePathResult ValidateProposalPath(MauiXamlSourceProposal proposal, string sourcePath)
    {
        var root = ResolveProjectRoot();
        if (root is null)
            return SourcePathResult.Failure("project-unregistered", "The registered project root is unavailable.");
        var validation = ValidateSourcePath(sourcePath, root);
        if (!validation.Ok)
            return validation;
        return IsRegisteredXaml(root, sourcePath)
            ? validation
            : SourcePathResult.Failure(
                MauiXamlSourceIneligibilityCodes.SourceFileUnregistered,
                "The proposal XAML file is not registered by the current project.");
    }

    private static SourcePathResult ValidateSourcePath(string sourcePath, string projectRoot)
    {
        try
        {
            if (!File.Exists(sourcePath) ||
                !string.Equals(Path.GetExtension(sourcePath), ".xaml", StringComparison.OrdinalIgnoreCase))
            {
                return SourcePathResult.Failure("source-unavailable", "The proposed XAML source file does not exist.");
            }
            var attributes = File.GetAttributes(sourcePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                XamlSourcePropertyEditor.PathContainsReparsePoint(projectRoot, sourcePath))
            {
                return SourcePathResult.Failure(
                    MauiXamlSourceIneligibilityCodes.SourcePathReparsePoint,
                    "Symbolic-link and reparse-point XAML paths are not eligible.");
            }
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                return SourcePathResult.Failure("source-read-only", "Read-only XAML source files are not eligible.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SourcePathResult.Failure("source-unavailable", "The XAML source file is not accessible.");
        }
        return SourcePathResult.Success(sourcePath, projectRoot);
    }

    private string? ResolveProjectRoot()
    {
        if (_project is null)
            return null;
        try
        {
            if (Path.IsPathFullyQualified(_project))
            {
                var full = Path.GetFullPath(_project);
                return Directory.Exists(full) ? full : File.Exists(full) ? Path.GetDirectoryName(full) : null;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static bool IsNativeOrSynthetic(ElementInfo element)
        => !string.Equals(element.Framework, "maui", StringComparison.OrdinalIgnoreCase) ||
           element.Type.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
           element.FullType.Contains("WebView", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(element.TemplateKind, "synthetic", StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedXaml(string relativePath, string sourceText)
        => relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)) ||
           relativePath.EndsWith(".g.xaml", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith(".generated.xaml", StringComparison.OrdinalIgnoreCase) ||
           sourceText.AsSpan().TrimStart().StartsWith("<!-- <auto-generated", StringComparison.OrdinalIgnoreCase);

    private static bool IsLinkedXaml(string projectRoot, string sourcePath)
    {
        var project = Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (project is null)
            return true;

        try
        {
            var relative = Path.GetRelativePath(projectRoot, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var document = XDocument.Load(project, LoadOptions.None);
            foreach (var item in document.Descendants().Where(element =>
                         element.Name.LocalName is "MauiXaml" or "Page" or "AdditionalFiles"))
            {
                var include = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
                var link = item.Attribute("Link")?.Value ?? item.Element(item.Name.Namespace + "Link")?.Value;
                if (string.IsNullOrWhiteSpace(include) || string.IsNullOrWhiteSpace(link))
                    continue;
                var includePath = include.Replace('\\', '/').TrimStart('.', '/');
                if (string.Equals(includePath, relative, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return true;
        }
        return false;
    }

    private static bool IsRegisteredXaml(string projectRoot, string sourcePath)
    {
        var project = Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (project is null)
            return false;

        try
        {
            var relative = Path.GetRelativePath(projectRoot, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var document = XDocument.Load(project, LoadOptions.None);
            foreach (var item in document.Descendants().Where(element =>
                         element.Name.LocalName is "MauiXaml" or "Page" or "EmbeddedResource"))
            {
                var remove = item.Attribute("Remove")?.Value;
                if (MatchesProjectItem(remove, relative))
                    return false;
                var include = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
                if (MatchesProjectItem(include, relative))
                    return true;
            }

            // MAUI SDK projects include project-contained .xaml files by default. An explicit
            // Remove above is the only project-file evidence that negates that default.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    private static bool MatchesProjectItem(string? item, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(item))
            return false;
        var normalized = item.Replace('\\', '/').TrimStart('.', '/');
        if (string.Equals(normalized, relativePath, StringComparison.OrdinalIgnoreCase))
            return true;
        // A wildcard can only establish inclusion. The surrounding source path already passed
        // project-root/reparse-point checks, so this avoids broadening the write scope.
        return normalized.EndsWith("**/*.xaml", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("*.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<string>> CollectProjectAutomationIdsAsync(
        string projectRoot,
        string excludedSourcePath,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        foreach (var path in Directory.EnumerateFiles(projectRoot, "*.xaml", SearchOption.AllDirectories)
                     .Take(4_096))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!XamlSourcePropertyEditor.IsUnderRoot(path, projectRoot) ||
                XamlSourcePropertyEditor.PathContainsReparsePoint(projectRoot, path))
            {
                continue;
            }

            string text;
            try
            {
                var file = await SourceFileSnapshot.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                text = file.Text;
            }
            catch
            {
                continue;
            }
            if (IsGeneratedXaml(Path.GetRelativePath(projectRoot, path), text))
                continue;

            try
            {
                var document = XDocument.Parse(text, LoadOptions.None);
                ids.AddRange((document.Root?.DescendantsAndSelf() ?? [])
                    .Attributes()
                    .Where(attribute => string.Equals(attribute.Name.LocalName, "AutomationId", StringComparison.Ordinal))
                    .Select(attribute => attribute.Value)
                    .Where(static value => !string.IsNullOrWhiteSpace(value)));
            }
            catch (XmlException)
            {
                // A malformed unrelated file cannot establish a duplicate. The selected document
                // is parsed by the evaluator; its malformed state fails closed.
            }
        }

        // C# proposals share the same durable AutomationId namespace. Scan only direct standard
        // string literals here; a dynamic C# value cannot prove uniqueness and is never a source
        // proposal candidate itself. This conservative cross-language scan prevents a reviewed
        // XAML addition from colliding with a code-created control.
        foreach (var path in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                     .Take(4_096))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!XamlSourcePropertyEditor.IsUnderRoot(path, projectRoot) ||
                XamlSourcePropertyEditor.PathContainsReparsePoint(projectRoot, path))
            {
                continue;
            }

            string text;
            try
            {
                var file = await SourceFileSnapshot.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                text = file.Text;
            }
            catch
            {
                continue;
            }
            if (IsGeneratedCSharp(Path.GetRelativePath(projectRoot, path), text))
                continue;

            var root = CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken).GetRoot(cancellationToken);
            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var isAutomationId = assignment.Left switch
                {
                    MemberAccessExpressionSyntax member => string.Equals(
                        member.Name.Identifier.ValueText,
                        "AutomationId",
                        StringComparison.Ordinal),
                    IdentifierNameSyntax identifier => string.Equals(
                        identifier.Identifier.ValueText,
                        "AutomationId",
                        StringComparison.Ordinal),
                    _ => false,
                };
                if (isAutomationId &&
                    assignment.Right is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression) &&
                    !string.IsNullOrWhiteSpace(literal.Token.ValueText))
                {
                    ids.Add(literal.Token.ValueText);
                }
            }
        }
        return ids;
    }

    private static bool IsGeneratedCSharp(string relativePath, string sourceText)
        => relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)) ||
           relativePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
           relativePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
           sourceText.AsSpan().TrimStart().StartsWith("// <auto-generated", StringComparison.OrdinalIgnoreCase) ||
           sourceText.AsSpan().TrimStart().StartsWith("/* <auto-generated", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ElementInfo> Flatten(IEnumerable<ElementInfo> roots)
    {
        var pending = new Stack<ElementInfo>(roots.Reverse());
        var count = 0;
        while (pending.Count > 0 && count++ < 10_000)
        {
            var current = pending.Pop();
            yield return current;
            if (current.Children is null)
                continue;
            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }
    }

    private static SourcePatch? CreatePatch(
        string source,
        MauiXamlSourceEligibilityAnalysis analysis,
        string newLiteral)
    {
        if (analysis.AttributeValueStart is { } valueStart &&
            analysis.AttributeValueLength is { } valueLength)
        {
            return new(valueStart, valueLength, newLiteral);
        }
        if (analysis.StartTagEnd is not { } end)
            return null;

        var insert = end;
        if (insert > 0 && source[insert - 1] == '/')
        {
            insert--;
            // Preserve the conventional ` />` spelling when it already exists. Inserting after
            // its whitespace avoids a needless double-space before the new attribute.
            var hasWhitespaceBeforeSlash = insert > 0 && char.IsWhiteSpace(source[insert - 1]);
            return new(
                insert,
                0,
                hasWhitespaceBeforeSlash
                    ? $"AutomationId=\"{newLiteral}\" "
                    : $" AutomationId=\"{newLiteral}\"");
        }
        return new(insert, 0, $" AutomationId=\"{newLiteral}\"");
    }

    private static int AdjustedTagLength(int originalLength, SourcePatch patch, int startTagOffset)
    {
        if (patch.Start < startTagOffset || patch.Start > startTagOffset + originalLength)
            return originalLength;
        return originalLength - patch.Length + patch.Replacement.Length;
    }

    private static string CreateDiff(string path, int line, string before, string after)
        => $"--- a/{path.Replace('\\', '/')}\n+++ b/{path.Replace('\\', '/')}\n@@ -{line},1 +{line},1 @@\n-{before}\n+{after}\n";

    private static List<MauiXamlSourceFlowFollowUp> NormalizeAffectedFlows(
        IReadOnlyList<MauiXamlSourceFlowFollowUp>? values,
        string? recommendedAutomationId = null)
        => (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value.FlowPath) ||
                                   !string.IsNullOrWhiteSpace(value.FlowId))
            .Take(64)
            .Select(value => new MauiXamlSourceFlowFollowUp
            {
                FlowPath = Bounded(value.FlowPath, 512),
                FlowId = Bounded(value.FlowId, 256),
                FlowDigest = Bounded(value.FlowDigest, 256),
                StepIds = value.StepIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Take(128).ToList(),
                RecommendedSelector = value.RecommendedSelector ??
                    (string.IsNullOrWhiteSpace(recommendedAutomationId)
                        ? null
                        : new FlowSelector { AutomationId = recommendedAutomationId }),
                RequiresSeparateApproval = true,
            })
            .ToList();

    private static async Task<List<MauiXamlSourceFlowFollowUp>> DiscoverAffectedFlowsAsync(
        string projectRoot,
        string? oldAutomationId,
        string proposedAutomationId,
        IReadOnlyList<MauiXamlSourceFlowFollowUp>? supplied,
        CancellationToken cancellationToken)
    {
        var results = NormalizeAffectedFlows(supplied, proposedAutomationId);
        if (string.IsNullOrWhiteSpace(oldAutomationId))
            return results;

        var workflowRoot = Path.Combine(projectRoot, "maui-tests");
        if (!Directory.Exists(workflowRoot) ||
            XamlSourcePropertyEditor.PathContainsReparsePoint(projectRoot, workflowRoot))
        {
            return results;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(workflowRoot, "*.md", SearchOption.AllDirectories)
                         .Take(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!XamlSourcePropertyEditor.IsUnderRoot(path, workflowRoot) ||
                    XamlSourcePropertyEditor.PathContainsReparsePoint(workflowRoot, path))
                {
                    continue;
                }
                var info = new FileInfo(path);
                if (info.Length > 1_048_576)
                    continue;

                string markdown;
                try
                {
                    markdown = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    continue;
                }

                var parsed = FlowMarkdown.Parse(markdown);
                if (!parsed.Ok || parsed.Flow is null)
                    continue;
                var matchingSteps = parsed.Flow.Steps
                    .Where(step => StepUsesAutomationId(step, oldAutomationId))
                    .Select(static step => step.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .ToList();
                if (matchingSteps.Count == 0)
                    continue;

                var relative = Path.GetRelativePath(projectRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                var flowId = ReadFlowId(parsed.Flow);
                if (results.Any(existing =>
                        string.Equals(existing.FlowPath, relative, StringComparison.Ordinal) ||
                        (!string.IsNullOrWhiteSpace(flowId) &&
                         string.Equals(existing.FlowId, flowId, StringComparison.Ordinal))))
                {
                    continue;
                }

                results.Add(new MauiXamlSourceFlowFollowUp
                {
                    FlowPath = relative,
                    FlowId = flowId,
                    FlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(parsed.Flow),
                    StepIds = matchingSteps,
                    RecommendedSelector = new FlowSelector { AutomationId = proposedAutomationId },
                    RequiresSeparateApproval = true,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Discovery is advisory. A filesystem issue means no unproven flow reference is added.
        }
        return results.Take(64).ToList();
    }

    private static bool StepUsesAutomationId(FlowStep step, string automationId)
        => string.Equals(step.Target?.AutomationId, automationId, StringComparison.Ordinal) ||
           string.Equals(step.Args?.Selector?.AutomationId, automationId, StringComparison.Ordinal) ||
           (step.Asserts ?? []).Any(assertion =>
               string.Equals(assertion.Selector?.AutomationId, automationId, StringComparison.Ordinal));

    private static string? ReadFlowId(MauiFlow flow)
        => flow.ExtensionData is not null &&
           flow.ExtensionData.TryGetValue("flowId", out var value) &&
           value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<MauiXamlSourcePlatformVerification> NormalizeAffectedPlatforms(
        IReadOnlyList<MauiXamlSourcePlatformVerification>? values)
        => (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value.Platform) ||
                                   !string.IsNullOrWhiteSpace(value.TargetFramework))
            .Take(16)
            .Select(value => new MauiXamlSourcePlatformVerification
            {
                Platform = Bounded(value.Platform, 64),
                TargetFramework = Bounded(value.TargetFramework, 128),
                BuildState = Bounded(value.BuildState, 128),
                RuntimeRemapState = Bounded(value.RuntimeRemapState, 128),
                UniquenessState = Bounded(value.UniquenessState, 128),
                ReplayState = Bounded(value.ReplayState, 128),
                OracleState = Bounded(value.OracleState, 128),
                ReasonCode = Bounded(value.ReasonCode, 128),
            })
            .ToList();

    private static List<MauiXamlSourcePlatformVerification> EnsureOfficialPlatformCoverage(
        IReadOnlyList<MauiXamlSourcePlatformVerification>? values)
    {
        var platforms = NormalizeAffectedPlatforms(values);
        var appleExternal = OperatingSystem.IsWindows();
        var required = new[]
        {
            ("android", "net10.0-android", false),
            ("windows", "net10.0-windows10.0.19041.0", false),
            ("ios", "net10.0-ios", appleExternal),
            ("maccatalyst", "net10.0-maccatalyst", appleExternal),
        };
        foreach (var (platform, tfm, externalQa) in required)
        {
            if (platforms.Any(existing =>
                    string.Equals(existing.Platform, platform, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existing.TargetFramework, tfm, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            platforms.Add(new MauiXamlSourcePlatformVerification
            {
                Platform = platform,
                TargetFramework = tfm,
                BuildState = externalQa ? "pending-external-qa" : "pending-host-build",
                RuntimeRemapState = externalQa ? "pending-external-qa" : "pending-runtime-remap",
                UniquenessState = externalQa ? "pending-external-qa" : "pending-runtime-uniqueness",
                ReplayState = externalQa ? "pending-external-qa" : "pending-flow-replay",
                OracleState = externalQa ? "pending-external-qa" : "pending-independent-oracle",
                ReasonCode = externalQa ? "apple-target-unavailable-on-windows" : null,
            });
        }
        return platforms;
    }

    private static bool ValidateProposalShape(MauiXamlSourceProposal proposal, out string? error)
    {
        error = null;
        if (proposal.Schema != 1 ||
            proposal.Operation is null ||
            proposal.Element is null ||
            proposal.Patch is null ||
            string.IsNullOrWhiteSpace(proposal.Operation.FileRelativePath) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SourceHash) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SourceAnchor) ||
            !string.Equals(proposal.Operation.Attribute, "AutomationId", StringComparison.Ordinal) ||
            !MauiXamlAutomationIdGrammar.TryValidate(proposal.Operation.NewLiteral, out _) ||
            string.IsNullOrWhiteSpace(proposal.BaseContentDigest) ||
            string.IsNullOrWhiteSpace(proposal.PatchDigest) ||
            string.IsNullOrWhiteSpace(proposal.Patch.BeforeDigest) ||
            string.IsNullOrWhiteSpace(proposal.Patch.AfterDigest) ||
            !FixedEquals(proposal.BaseContentDigest, proposal.Patch.BeforeDigest))
        {
            error = "The source proposal is not a valid bounded XAML AutomationId operation.";
            return false;
        }
        return true;
    }

    private static string OpaqueId(string prefix)
        => prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string Digest(string value)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(left),
               Encoding.UTF8.GetBytes(right));

    private static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : null;
    }

    private readonly record struct SourcePatch(int Start, int Length, string Replacement);

    private readonly record struct SourcePathResult(
        bool Ok,
        string? Path,
        string? ProjectRoot,
        string? Code,
        string? Error)
    {
        public static SourcePathResult Success(string path, string projectRoot)
            => new(true, path, projectRoot, null, null);

        public static SourcePathResult Failure(string code, string error)
            => new(false, null, null, code, error);
    }

    private sealed class SourceFileTooLargeException : Exception;

    private sealed class SourceFileSnapshot
    {
        private SourceFileSnapshot(byte[] bytes, string text, Encoding encoding, byte[] preamble)
        {
            Bytes = bytes;
            Text = text;
            _encoding = encoding;
            _preamble = preamble;
            ContentDigest = MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(bytes);
        }

        private readonly Encoding _encoding;
        private readonly byte[] _preamble;
        public byte[] Bytes { get; }
        public string Text { get; }
        public string ContentDigest { get; }

        public static async Task<SourceFileSnapshot> ReadAsync(string path, CancellationToken cancellationToken)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxSourceFileBytes)
                throw new SourceFileTooLargeException();

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var (encoding, preambleLength) = DetectEncoding(bytes);
            var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return new SourceFileSnapshot(bytes, text, encoding, bytes[..preambleLength]);
        }

        public byte[] Encode(string text)
        {
            var body = _encoding.GetBytes(text);
            var result = new byte[_preamble.Length + body.Length];
            _preamble.CopyTo(result, 0);
            body.CopyTo(result, _preamble.Length);
            return result;
        }

        private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
        {
            if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), 3);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
                return (new UnicodeEncoding(false, true, true), 2);
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
                return (new UnicodeEncoding(true, true, true), 2);
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), 0);
        }
    }

    private static class AtomicFileWriter
    {
        internal static async Task<AtomicWriteResult> WriteIfDigestMatchesAsync(
            string path,
            byte[] updatedBytes,
            string expectedDigest,
            Func<SourcePathResult> validatePath,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path)!;
            var fileName = Path.GetFileName(path);
            var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            var backup = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.bak");
            var rejection = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.rejected");
            var updatedDigest = MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(updatedBytes);
            var preserveBackup = false;
            try
            {
                var current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(
                        MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(current),
                        expectedDigest))
                {
                    return AtomicWriteResult.Failure(
                        MauiXamlSourceProposalStates.Stale,
                        "The XAML source changed before the local host write. No source change was written.");
                }

                var validation = validatePath();
                if (!validation.Ok)
                    return AtomicWriteResult.Failure(validation.Code!, validation.Error!);

                await File.WriteAllBytesAsync(temporary, updatedBytes, cancellationToken).ConfigureAwait(false);
                CopyMetadata(path, temporary);

                // File.Replace preserves the destination metadata and gives us the replaced content
                // to detect external writes that raced the immediate pre-write compare-and-swap.
                preserveBackup = true;
                File.Replace(temporary, path, backup, ignoreMetadataErrors: false);
                var replaced = await File.ReadAllBytesAsync(backup, cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(
                        MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(replaced),
                        expectedDigest))
                {
                    if (await TryRestoreAsync(path, backup, rejection, updatedDigest, cancellationToken).ConfigureAwait(false))
                    {
                        preserveBackup = false;
                        return AtomicWriteResult.Failure(
                            MauiXamlSourceProposalStates.Stale,
                            "The XAML source changed during the local host write. The external version was restored.");
                    }
                    return AtomicWriteResult.Failure(
                        "source-write-recovery-required",
                        "A concurrent source write could not be safely restored; the local host left a recovery copy.");
                }

                preserveBackup = false;
                TryDelete(backup);
                return AtomicWriteResult.Success(updatedDigest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                if (preserveBackup &&
                    await TryRestoreAsync(path, backup, rejection, updatedDigest, cancellationToken).ConfigureAwait(false))
                {
                    preserveBackup = false;
                    return AtomicWriteResult.Failure("source-write-failed", "The local host source write was rolled back.");
                }
                return AtomicWriteResult.Failure("source-write-failed", "The local host could not atomically write the XAML source.");
            }
            finally
            {
                TryDelete(temporary);
                TryDelete(rejection);
                if (!preserveBackup)
                    TryDelete(backup);
            }
        }

        private static async Task<bool> TryRestoreAsync(
            string path,
            string backup,
            string rejection,
            string expectedAppliedDigest,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(backup))
                    return false;
                var current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!FixedEquals(
                        MauiXamlSourceEligibilityAnalyzer.ComputeContentDigest(current),
                        expectedAppliedDigest))
                {
                    return false;
                }
                File.Replace(backup, path, rejection, ignoreMetadataErrors: false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        private static void CopyMetadata(string source, string destination)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
                return;
            }
            const FileAttributes copyable =
                FileAttributes.Hidden |
                FileAttributes.System |
                FileAttributes.Archive |
                FileAttributes.NotContentIndexed;
            var attributes = File.GetAttributes(source) & copyable;
            File.SetAttributes(destination, attributes == 0 ? FileAttributes.Normal : attributes);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private readonly record struct AtomicWriteResult(bool Ok, string? ContentDigest, string? Code, string? Error)
    {
        public static AtomicWriteResult Success(string contentDigest) => new(true, contentDigest, null, null);
        public static AtomicWriteResult Failure(string code, string error) => new(false, null, code, error);
    }
}

internal sealed class XamlSourceProposalBuildResult
{
    public bool Ok { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }
    public MauiXamlSourceEligibilityAnalysis? Analysis { get; private init; }
    public MauiXamlSourceProposal? Proposal { get; private init; }
    public string? BaseContentDigest { get; private init; }
    public string? AfterContentDigest { get; private init; }

    public static XamlSourceProposalBuildResult Success(
        MauiXamlSourceProposal proposal,
        string baseContentDigest,
        string afterContentDigest,
        MauiXamlSourceEligibilityAnalysis analysis) => new()
        {
            Ok = true,
            Proposal = proposal,
            Analysis = analysis,
            BaseContentDigest = baseContentDigest,
            AfterContentDigest = afterContentDigest,
        };

    public static XamlSourceProposalBuildResult Ineligible(MauiXamlSourceEligibilityAnalysis analysis) => new()
    {
        Ok = false,
        Code = "source-ineligible",
        Error = "The XAML declaration is not eligible for an AutomationId source proposal.",
        Analysis = analysis,
    };

    public static XamlSourceProposalBuildResult Failure(string code, string error) => new()
    {
        Code = code,
        Error = error,
    };
}

internal sealed class XamlSourceProposalWriteResult
{
    public bool Ok { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }
    public string? ContentDigest { get; private init; }
    public byte[]? OriginalBytes { get; private init; }
    public string? OriginalContentDigest { get; private init; }

    public static XamlSourceProposalWriteResult Success(
        string contentDigest,
        byte[] originalBytes,
        string originalContentDigest) => new()
        {
            Ok = true,
            ContentDigest = contentDigest,
            OriginalBytes = originalBytes,
            OriginalContentDigest = originalContentDigest,
        };

    public static XamlSourceProposalWriteResult Failure(string code, string error) => new()
    {
        Code = code,
        Error = error,
    };
}
