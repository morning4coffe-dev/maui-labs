using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Analyzers;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class XamlSourceProposalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "xaml-source-proposal-tests",
        Guid.NewGuid().ToString("N"));

    public XamlSourceProposalTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task BuildAndApplyAsync_StaticLiteralAdd_PreservesBomNewlinesAndSupportsRollback()
    {
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Button Text="Save" />
            </ContentPage>
            """;
        var project = await CreateProjectAsync(xaml, useCrLf: true);
        var persistedXaml = xaml.ReplaceLineEndings("\r\n");
        var element = Element(project.SourcePath, persistedXaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);

        var built = await service.BuildAsync(element, "SaveButton", [element]);

        Assert.True(built.Ok, built.Error);
        var proposal = Assert.IsType<MauiXamlSourceProposal>(built.Proposal);
        Assert.Equal("add-literal-automation-id", proposal.Operation.Kind);
        Assert.Equal("MainPage.xaml", proposal.Operation.FileRelativePath);
        Assert.Contains("+<Button Text=\"Save\" AutomationId=\"SaveButton\" />", proposal.Diff, StringComparison.Ordinal);
        Assert.Contains("SaveButton", proposal.Patch.Replacement, StringComparison.Ordinal);
        Assert.Equal(xaml.ReplaceLineEndings("\r\n"), await File.ReadAllTextAsync(project.SourcePath));

        var applied = await service.ApplyAsync(proposal);

        Assert.True(applied.Ok, applied.Error);
        var bytes = await File.ReadAllBytesAsync(project.SourcePath);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        var updated = await File.ReadAllTextAsync(project.SourcePath);
        Assert.Contains("AutomationId=\"SaveButton\"", updated);
        Assert.Contains("\r\n", updated);

        var reverted = await service.RollbackAsync(
            proposal,
            Assert.IsType<byte[]>(applied.OriginalBytes),
            Assert.IsType<string>(applied.ContentDigest));

        Assert.True(reverted.Ok, reverted.Error);
        Assert.Equal(xaml.ReplaceLineEndings("\r\n"), await File.ReadAllTextAsync(project.SourcePath));
    }

    [Fact]
    public async Task BuildAsync_LiteralReplacement_ProducesMinimalValuePatch()
    {
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Button AutomationId="OldSave" Text="Save" />
            </ContentPage>
            """;
        var project = await CreateProjectAsync(xaml);
        var element = Element(project.SourcePath, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);

        var built = await service.BuildAsync(element, "SaveButton", [element]);

        Assert.True(built.Ok, built.Error);
        var proposal = built.Proposal!;
        Assert.Equal("replace-literal-automation-id", proposal.Operation.Kind);
        Assert.Equal("OldSave", proposal.Operation.OldLiteral);
        Assert.Equal("SaveButton", proposal.Operation.NewLiteral);
        Assert.Equal("SaveButton".Length, proposal.Patch.Replacement!.Length);
        Assert.Contains("-<Button AutomationId=\"OldSave\" Text=\"Save\" />", proposal.Diff, StringComparison.Ordinal);
        Assert.Contains("+<Button AutomationId=\"SaveButton\" Text=\"Save\" />", proposal.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_ReplacementDiscoversAffectedFlowButDoesNotEditIt()
    {
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Button AutomationId="OldSave" Text="Save" />
            </ContentPage>
            """;
        var project = await CreateProjectAsync(xaml);
        var workflowRoot = Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "maui-tests");
        Directory.CreateDirectory(workflowRoot);
        var flowPath = Path.Combine(workflowRoot, "save.md");
        var flow = new MauiFlow
        {
            Name = "save",
            Steps =
            [
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Tap,
                    Target = new FlowSelector { AutomationId = "OldSave" },
                },
            ],
        };
        var markdown = FlowMarkdown.Serialize(flow);
        await File.WriteAllTextAsync(flowPath, markdown);
        var element = Element(project.SourcePath, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);

        var built = await service.BuildAsync(element, "SaveButton", [element]);

        Assert.True(built.Ok, built.Error);
        var followUp = Assert.Single(built.Proposal!.AffectedFlows);
        Assert.Equal("maui-tests/save.md", followUp.FlowPath);
        Assert.Equal("1", Assert.Single(followUp.StepIds));
        Assert.Equal("SaveButton", followUp.RecommendedSelector!.AutomationId);
        Assert.True(followUp.RequiresSeparateApproval);
        Assert.Equal(markdown, await File.ReadAllTextAsync(flowPath));
    }

    [Fact]
    public async Task BuildAsync_RejectsAutomationIdAlreadyDeclaredInProjectCSharp()
    {
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Button Text="Save" />
            </ContentPage>
            """;
        var project = await CreateProjectAsync(xaml);
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "CodePage.cs"),
            """
            public class CodePage
            {
                void Build()
                {
                    button.AutomationId = "SaveButton";
                }
            }
            """);
        var element = Element(project.SourcePath, xaml, "<Button");

        var result = await new XamlAutomationIdProposalService(project.ProjectPath)
            .BuildAsync(element, "SaveButton", [element]);

        Assert.False(result.Ok);
        Assert.Contains(
            result.Analysis!.Decision.Reasons,
            reason => reason.Code == MauiXamlSourceIneligibilityCodes.AutomationIdDuplicateProject);
    }

    [Fact]
    public async Task ApplyAsync_ExternalEditAfterPreview_FailsCompareAndSwap()
    {
        const string xaml = """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui">
                <Button Text="Save" />
            </ContentPage>
            """;
        var project = await CreateProjectAsync(xaml);
        var element = Element(project.SourcePath, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);
        var built = await service.BuildAsync(element, "SaveButton", [element]);
        Assert.True(built.Ok, built.Error);

        await File.AppendAllTextAsync(project.SourcePath, "\n<!-- external write -->");
        var applied = await service.ApplyAsync(built.Proposal!);

        Assert.False(applied.Ok);
        Assert.Equal(MauiXamlSourceProposalStates.Stale, applied.Code);
        Assert.Contains("external write", await File.ReadAllTextAsync(project.SourcePath));
    }

    [Fact]
    public async Task BuildAsync_SourceOutsideRegisteredProject_FailsClosed()
    {
        const string xaml = "<ContentPage><Button Text=\"Save\" /></ContentPage>";
        var project = await CreateProjectAsync(xaml);
        var outside = Path.Combine(_root, "outside.xaml");
        await File.WriteAllTextAsync(outside, xaml);
        var element = Element(outside, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);

        var built = await service.BuildAsync(element, "SaveButton", [element]);

        Assert.False(built.Ok);
        Assert.Equal(MauiXamlSourceIneligibilityCodes.SourceFileOutsideProject, built.Code);
    }

    [Theory]
    [InlineData("<ContentPage><Button Text=\"{Binding Save}\" /></ContentPage>", "<Button", MauiXamlSourceIneligibilityCodes.BindingOrMarkup)]
    [InlineData("<ContentPage><ContentPage.Resources><Style TargetType=\"Button\"><Setter Property=\"AutomationId\" Value=\"SaveButton\" /></Style></ContentPage.Resources></ContentPage>", "<Setter", MauiXamlSourceIneligibilityCodes.TemplateOrStyle)]
    [InlineData("<ContentPage><CollectionView><CollectionView.ItemTemplate><DataTemplate><Button Text=\"Save\" /></DataTemplate></CollectionView.ItemTemplate></CollectionView></ContentPage>", "<Button", MauiXamlSourceIneligibilityCodes.TemplateOrStyle)]
    [InlineData("<ContentPage><WebView /></ContentPage>", "<WebView", MauiXamlSourceIneligibilityCodes.NativeOrWebViewSynthetic)]
    public void EligibilityAnalyzer_UnsafeDeclarations_ReturnExplicitCodes(
        string xaml,
        string marker,
        string expectedCode)
    {
        var (line, column) = LineColumn(xaml, marker);
        var result = MauiXamlSourceEligibilityAnalyzer.Analyze(new MauiXamlSourceEligibilityInput
        {
            SourceText = xaml,
            FileRelativePath = "MainPage.xaml",
            ExpectedSourceHash = MauiXamlSourceEligibilityAnalyzer.ComputeSourceHash(xaml),
            SourceLine = line,
            SourceColumn = column,
            SourceConfidence = "mapped",
            IsProjectContained = true,
            ProposedAutomationId = "SaveButton",
            ProjectAutomationIds = [],
            LiveAutomationIds = [],
            LiveUniquenessAvailable = true,
        });

        Assert.False(result.Decision.Eligible);
        Assert.Contains(result.Decision.Reasons, reason => reason.Code == expectedCode);
    }

    [Fact]
    public void EligibilityAnalyzer_RejectsGeneratedLinkedReparseInvalidAndDuplicateIds()
    {
        const string xaml = "<ContentPage><Button Text=\"Save\" /></ContentPage>";
        var (line, column) = LineColumn(xaml, "<Button");
        var result = MauiXamlSourceEligibilityAnalyzer.Analyze(new MauiXamlSourceEligibilityInput
        {
            SourceText = xaml,
            FileRelativePath = "MainPage.xaml",
            ExpectedSourceHash = "0000000000000000",
            SourceLine = line,
            SourceColumn = column,
            SourceConfidence = "mapped",
            IsProjectContained = false,
            IsGenerated = true,
            IsLinked = true,
            HasReparsePoint = true,
            ProposedAutomationId = "保存",
            ProjectAutomationIds = ["SaveButton"],
            LiveAutomationIds = ["SaveButton"],
            LiveUniquenessAvailable = true,
        });

        Assert.False(result.Decision.Eligible);
        var codes = result.Decision.Reasons.Select(reason => reason.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(MauiXamlSourceIneligibilityCodes.SourceHashMismatch, codes);
        Assert.Contains(MauiXamlSourceIneligibilityCodes.SourceFileOutsideProject, codes);
        Assert.Contains(MauiXamlSourceIneligibilityCodes.SourceFileGenerated, codes);
        Assert.Contains(MauiXamlSourceIneligibilityCodes.SourceFileLinked, codes);
        Assert.Contains(MauiXamlSourceIneligibilityCodes.SourcePathReparsePoint, codes);
        Assert.Contains(MauiXamlSourceIneligibilityCodes.AutomationIdLocalizedOrUserDerived, codes);
    }

    [Theory]
    [InlineData("save button")]
    [InlineData("{Binding UserName}")]
    [InlineData("UserNameEntry")]
    [InlineData("保存")]
    [InlineData("9Save")]
    [InlineData("Save..Button")]
    public void AutomationIdGrammar_RejectsLocalizedDynamicAndUnsafeValues(string value)
    {
        Assert.False(MauiXamlAutomationIdGrammar.TryValidate(value, out _));
    }

    [Fact]
    public void SourceApplyRoutes_AreBlockedDuringAFlowReplay()
    {
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/proposal/apply"));
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/proposal/rollback"));
        Assert.False(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/proposal/preview"));
    }

    [Fact]
    public async Task Store_RequiresSeparateHumanGrantRejectsHostUnsupportedAndConsumesGrant()
    {
        const string xaml = "<ContentPage><Button Text=\"Save\" /></ContentPage>";
        var project = await CreateProjectAsync(xaml);
        var element = Element(project.SourcePath, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);
        var built = await service.BuildAsync(
            element,
            "SaveButton",
            [element],
            affectedPlatforms:
            [
                new MauiXamlSourcePlatformVerification
                {
                    Platform = "windows",
                    TargetFramework = "net10.0-windows10.0.19041.0",
                },
            ]);
        Assert.True(built.Ok, built.Error);
        var store = new WorkflowXamlSourceProposalStore();
        Assert.True(store.Propose(built.Proposal!).Ok);
        Assert.True(store.Preview(built.Proposal!.ProposalId).Ok);
        var binding = Binding(built.Proposal!);

        var grant = store.IssueGrant(new WorkflowXamlSourceGrantIssueRequest
        {
            ProposalId = built.Proposal!.ProposalId,
            Kind = WorkflowXamlSourceGrantKinds.Apply,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = binding,
        });
        Assert.True(grant.Ok, grant.Error);
        Assert.Equal(MauiXamlSourceProposalStates.Approved, grant.Proposal!.State);
        var denied = store.AwaitHostApply(
            built.Proposal!.ProposalId,
            binding,
            new WorkflowXamlSourceHostCapability { HostKind = "canvas" });
        Assert.False(denied.Ok);
        Assert.Equal("host-apply-unsupported", denied.Code);

        // Source apply is a positive allowlist: an unrecognised host identity is never trusted,
        // even if it claims every capability. A denylist would let a renamed or spoofed surface
        // inherit apply rights it was never granted.
        var unknownHost = store.AwaitHostApply(
            built.Proposal!.ProposalId,
            binding,
            new WorkflowXamlSourceHostCapability
            {
                HostKind = "some-new-surface",
                CanApplySource = true,
                CanOpenNativeDiff = true,
                IsExplicitLocalHostAction = true,
            });
        Assert.False(unknownHost.Ok);
        Assert.Equal("host-apply-unsupported", unknownHost.Code);

        var browserHost = store.AwaitHostApply(
            built.Proposal!.ProposalId,
            binding,
            new WorkflowXamlSourceHostCapability
            {
                HostKind = "browser",
                CanApplySource = true,
                IsExplicitLocalHostAction = true,
            });
        Assert.False(browserHost.Ok);

        var host = new WorkflowXamlSourceHostCapability
        {
            HostKind = "vscode",
            CanApplySource = true,
            CanOpenNativeDiff = true,
            IsExplicitLocalHostAction = true,
        };
        Assert.True(store.AwaitHostApply(built.Proposal!.ProposalId, binding, host).Ok);
        var begun = store.BeginApply(built.Proposal!.ProposalId, grant.Grant, binding, host);
        Assert.True(begun.Ok, begun.Error);
        Assert.Equal(MauiXamlSourceProposalStates.Applying, begun.Proposal!.State);
        Assert.False(store.BeginApply(built.Proposal!.ProposalId, grant.Grant, binding, host).Ok);
    }

    [Fact]
    public async Task Store_ExpiredSourceGrantFailsClosedAndMarksProposalStale()
    {
        const string xaml = "<ContentPage><Button Text=\"Save\" /></ContentPage>";
        var project = await CreateProjectAsync(xaml);
        var element = Element(project.SourcePath, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);
        var built = await service.BuildAsync(element, "SaveButton", [element]);
        Assert.True(built.Ok, built.Error);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        var store = new WorkflowXamlSourceProposalStore(
            new WorkflowXamlSourceProposalStoreOptions
            {
                DefaultGrantLifetime = TimeSpan.FromMinutes(1),
                MaximumGrantLifetime = TimeSpan.FromMinutes(5),
            },
            clock);
        Assert.True(store.Propose(built.Proposal!).Ok);
        Assert.True(store.Preview(built.Proposal!.ProposalId).Ok);
        var binding = Binding(built.Proposal!);
        var grant = store.IssueGrant(new WorkflowXamlSourceGrantIssueRequest
        {
            ProposalId = built.Proposal!.ProposalId,
            Kind = WorkflowXamlSourceGrantKinds.Apply,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = binding,
        });
        Assert.True(grant.Ok, grant.Error);
        clock.Advance(TimeSpan.FromMinutes(2));
        var state = store.Get(built.Proposal!.ProposalId);
        Assert.True(state.Ok);
        Assert.Equal(MauiXamlSourceProposalStates.Stale, state.Proposal!.State);
        var host = new WorkflowXamlSourceHostCapability
        {
            HostKind = "vscode",
            CanApplySource = true,
            IsExplicitLocalHostAction = true,
        };
        Assert.False(store.BeginApply(built.Proposal!.ProposalId, grant.Grant, binding, host).Ok);
    }

    [Fact]
    public async Task Store_ExperimentalAppKitVerificationFailureRequiresAtomicRollbackAndHistoryIsRedacted()
    {
        const string xaml = "<ContentPage><Button AutomationId=\"SecretCustomer\" Text=\"Save\" /></ContentPage>";
        var project = await CreateProjectAsync(xaml);
        var element = Element(project.SourcePath, xaml, "<Button");
        var service = new XamlAutomationIdProposalService(project.ProjectPath);
        var built = await service.BuildAsync(
            element,
            "SaveButton",
            [element],
            affectedPlatforms:
            [
                new MauiXamlSourcePlatformVerification
                {
                    Platform = "macos",
                    TargetFramework = "net10.0-macos",
                },
            ]);
        Assert.True(built.Ok, built.Error);
        var proposal = built.Proposal!;
        var store = new WorkflowXamlSourceProposalStore();
        var proposed = store.Propose(proposal);
        Assert.True(proposed.Ok);
        Assert.True(store.Preview(proposal.ProposalId).Ok);
        var binding = Binding(proposal);
        var host = new WorkflowXamlSourceHostCapability
        {
            HostKind = "vscode",
            CanApplySource = true,
            IsExplicitLocalHostAction = true,
        };
        var grant = store.IssueGrant(new WorkflowXamlSourceGrantIssueRequest
        {
            ProposalId = proposal.ProposalId,
            Kind = WorkflowXamlSourceGrantKinds.Apply,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = binding,
        });
        Assert.True(store.BeginApply(proposal.ProposalId, grant.Grant, binding, host).Ok);
        var write = await service.ApplyAsync(proposal);
        Assert.True(write.Ok, write.Error);
        Assert.True(store.CompleteApply(proposal.ProposalId, new WorkflowXamlSourceApplyRecord
        {
            Applied = true,
            AppliedContentDigest = write.ContentDigest,
            OriginalBytes = write.OriginalBytes,
            OriginalContentDigest = write.OriginalContentDigest,
        }).Ok);

        var failed = store.RecordVerification(proposal.ProposalId, new WorkflowXamlSourceVerificationRecord
        {
            Platforms =
            [
                new WorkflowXamlSourcePlatformVerificationResult
                {
                    Platform = "macos",
                    TargetFramework = "net10.0-macos",
                    BuildSucceeded = true,
                    RuntimeRemapConfirmed = false,
                    AutomationIdUnique = false,
                },
            ],
            AffectedFlowsReplayed = false,
            IndependentOracleSucceeded = false,
        });
        Assert.True(failed.Ok);
        Assert.Equal(MauiXamlSourceProposalStates.RollbackRequired, failed.Proposal!.State);
        Assert.Equal(MauiXamlSourceProposalStates.VerificationFailed, failed.Proposal.LastRecoveryState);

        var history = new WorkflowXamlSourceHistoryStore(Path.GetDirectoryName(project.ProjectPath)!);
        var historyResult = history.Append(failed.Proposal);
        Assert.True(historyResult.Ok, historyResult.Error);
        var contents = await File.ReadAllTextAsync(historyResult.HistoryPath!);
        Assert.DoesNotContain("SecretCustomer", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveButton", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(proposal.Diff!, contents, StringComparison.Ordinal);

        var rollbackBinding = Binding(proposal, write.ContentDigest);
        var rollbackGrant = store.IssueGrant(new WorkflowXamlSourceGrantIssueRequest
        {
            ProposalId = proposal.ProposalId,
            Kind = WorkflowXamlSourceGrantKinds.Rollback,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = rollbackBinding,
        });
        Assert.True(rollbackGrant.Ok, rollbackGrant.Error);
        var begunRollback = store.BeginRollback(proposal.ProposalId, rollbackGrant.Grant, rollbackBinding, host);
        Assert.True(begunRollback.Ok, begunRollback.Error);
        Assert.True(store.TryGetRollbackBytes(proposal.ProposalId, out var original, out var expected));
        var reverted = await service.RollbackAsync(proposal, original!, expected!);
        Assert.True(reverted.Ok, reverted.Error);
        var complete = store.CompleteRollback(proposal.ProposalId, new WorkflowXamlSourceRollbackRecord
        {
            Reverted = true,
            ContentDigest = reverted.ContentDigest,
        });
        Assert.True(complete.Ok);
        Assert.Equal(MauiXamlSourceProposalStates.Reverted, complete.Proposal!.State);
    }

    [Fact]
    public void Store_ExperimentalAppKitExternalQaRemainsAppliedUntilMacVerificationArrives()
    {
        var proposal = StoreProposal("macos", "net10.0-macos");
        var store = new WorkflowXamlSourceProposalStore();
        Assert.True(store.Propose(proposal).Ok);
        Assert.True(store.Preview(proposal.ProposalId).Ok);
        var binding = Binding(proposal);
        var host = new WorkflowXamlSourceHostCapability
        {
            HostKind = "vscode",
            CanApplySource = true,
            IsExplicitLocalHostAction = true,
        };
        var grant = store.IssueGrant(new WorkflowXamlSourceGrantIssueRequest
        {
            ProposalId = proposal.ProposalId,
            Kind = WorkflowXamlSourceGrantKinds.Apply,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = binding,
        });
        Assert.True(grant.Ok, grant.Error);
        Assert.True(store.BeginApply(proposal.ProposalId, grant.Grant, binding, host).Ok);
        Assert.True(store.CompleteApply(proposal.ProposalId, new WorkflowXamlSourceApplyRecord
        {
            Applied = true,
            AppliedContentDigest = "sha256:" + new string('b', 64),
            OriginalContentDigest = proposal.BaseContentDigest,
            OriginalBytes = [1, 2, 3],
        }).Ok);

        var recorded = store.RecordVerification(proposal.ProposalId, new WorkflowXamlSourceVerificationRecord
        {
            Platforms =
            [
                new WorkflowXamlSourcePlatformVerificationResult
                {
                    Platform = "macos",
                    TargetFramework = "net10.0-macos",
                    PendingExternalQa = true,
                },
            ],
            AffectedFlowsReplayed = true,
            IndependentOracleSucceeded = true,
        });

        Assert.True(recorded.Ok);
        Assert.Equal(MauiXamlSourceProposalStates.Applied, recorded.Proposal!.State);
        Assert.Contains("pending-external-qa", recorded.Proposal.Verification!.Reasons);
    }

    [Fact]
    public async Task AdvisoryAnalyzer_ReportsMissingDuplicateAndTemplateDiagnosticsWithoutFixes()
    {
        var additional = ImmutableArray.Create<AdditionalText>(
            new TestAdditionalText(
                Path.Combine(_root, "MainPage.xaml"),
                """
                <ContentPage>
                  <Button Text="Missing" />
                  <Button AutomationId="Duplicate" />
                  <Button AutomationId="Duplicate" />
                  <CollectionView>
                    <CollectionView.ItemTemplate>
                      <DataTemplate><Button AutomationId="TemplateButton" /></DataTemplate>
                    </CollectionView.ItemTemplate>
                  </CollectionView>
                </ContentPage>
                """));
        var compilation = CSharpCompilation.Create(
            "XamlAdvisory",
            [CSharpSyntaxTree.ParseText("public sealed class Placeholder {}")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new AnalyzerOptions(additional);
        var diagnostics = await compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new XamlAutomationIdAdvisoryAnalyzer()),
            new CompilationWithAnalyzersOptions(options, null, true, false, false))
            .GetAnalyzerDiagnosticsAsync();

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "DFXAML001");
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "DFXAML002"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "DFXAML003");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.NotConfigurable));
    }

    private async Task<ProjectFixture> CreateProjectAsync(string xaml, bool useCrLf = false)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "TestApp.csproj");
        var sourcePath = Path.Combine(directory, "MainPage.xaml");
        await File.WriteAllTextAsync(projectPath, "<Project />");
        var text = useCrLf ? xaml.ReplaceLineEndings("\r\n") : xaml;
        await File.WriteAllTextAsync(
            sourcePath,
            text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return new ProjectFixture(projectPath, sourcePath);
    }

    private static ElementInfo Element(string sourcePath, string source, string marker)
    {
        var (line, column) = LineColumn(source, marker);
        return new ElementInfo
        {
            Id = "element",
            Type = marker.Contains("WebView", StringComparison.Ordinal) ? "WebView" : "Button",
            FullType = marker.Contains("WebView", StringComparison.Ordinal)
                ? "Microsoft.Maui.Controls.WebView"
                : "Microsoft.Maui.Controls.Button",
            Framework = "maui",
            SourceFile = sourcePath,
            SourceLine = line,
            SourceColumn = column,
            SourceHash = MauiXamlSourceEligibilityAnalyzer.ComputeSourceHash(source),
            SourceConfidence = "mapped",
        };
    }

    private static (int Line, int Column) LineColumn(string source, string marker)
    {
        var offset = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"Could not locate '{marker}'.");
        var line = 1;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }
        // XDocument source map coordinates start at the element name, after '<'.
        return (line, offset - lineStart + 2);
    }

    private static WorkflowXamlSourceGrantBinding Binding(
        MauiXamlSourceProposal proposal,
        string? contentDigest = null) => new()
        {
            FileRelativePath = proposal.Operation.FileRelativePath,
            BaseContentDigest = contentDigest ?? proposal.BaseContentDigest,
            SourceHash = proposal.Operation.SourceHash,
            PatchDigest = proposal.PatchDigest,
            ProjectIdentity = "sha256:" + new string('a', 64),
            FlowReferencesDigest = WorkflowXamlSourceProposalStore.ComputeFlowReferencesDigest(proposal.AffectedFlows),
            HostKind = "vscode",
        };

    private static MauiXamlSourceProposal StoreProposal(string platform, string targetFramework)
    {
        var baseDigest = "sha256:" + new string('a', 64);
        return new MauiXamlSourceProposal
        {
            ProposalId = "xamlproposal_store",
            Operation = new MauiXamlSourceOperation
            {
                OperationId = "xamlop_store",
                Kind = "add-literal-automation-id",
                FileRelativePath = "MainPage.xaml",
                SourceHash = "0123456789abcdef",
                SourceAnchor = "sha256:" + new string('c', 64),
                Attribute = "AutomationId",
                NewLiteral = "SaveButton",
            },
            Element = new MauiXamlSourceElementIdentity
            {
                ElementType = "Button",
                Line = 1,
                Column = 2,
                SourceAnchor = "sha256:" + new string('c', 64),
            },
            BaseContentDigest = baseDigest,
            Patch = new MauiXamlSourcePatch
            {
                Format = "text-replace-v1",
                Operation = "add-literal-automation-id",
                BeforeDigest = baseDigest,
                AfterDigest = "sha256:" + new string('b', 64),
                Start = 0,
                Length = 0,
                Replacement = " AutomationId=\"SaveButton\"",
            },
            PatchDigest = "sha256:" + new string('d', 64),
            DiffDigest = "sha256:" + new string('e', 64),
            Diff = "--- a/MainPage.xaml\n+++ b/MainPage.xaml\n",
            Eligibility = new MauiXamlSourceEligibilityDecision { Eligible = true },
            AffectedPlatforms =
            [
                new MauiXamlSourcePlatformVerification
                {
                    Platform = platform,
                    TargetFramework = targetFramework,
                },
            ],
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    private sealed record ProjectFixture(string ProjectPath, string SourcePath);

    private sealed class TestAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public TestAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
    }
}
