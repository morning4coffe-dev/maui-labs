using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Analyzers;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class CSharpSourceProposalTests : IDisposable
{
    private const string MauiStubs = """

        namespace Microsoft.Maui.Controls
        {
            public class Button
            {
                public string AutomationId { get; set; } = "";
                public string Text { get; set; } = "";
            }

            public class WebView
            {
                public string AutomationId { get; set; } = "";
            }
        }
        """;

    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "csharp-source-proposal-tests",
        Guid.NewGuid().ToString("N"));

    public CSharpSourceProposalTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AdvisoryAnalyzer_ReportsMissingAndDuplicateSafeDeclarations()
    {
        const string missing = """
            using Microsoft.Maui.Controls;
            public class Page
            {
                void Build()
                {
                    var save = new {|#0:Button|} { Text = "Save" };
                }
            }
            """;
        var missingExpected = new DiagnosticResult("DFCS001", DiagnosticSeverity.Info)
            .WithLocation(0)
            .WithArguments("Button");
        await CreateAnalyzerTest(missing, missingExpected).RunAsync();

        const string duplicate = """
            using Microsoft.Maui.Controls;
            public class Page
            {
                void Build()
                {
                    var first = new Button();
                    var second = new Button();
                    first.{|#0:AutomationId|} = "SaveButton";
                    second.{|#1:AutomationId|} = "SaveButton";
                }
            }
            """;
        var first = new DiagnosticResult("DFCS002", DiagnosticSeverity.Warning)
            .WithLocation(0)
            .WithArguments("SaveButton");
        var second = new DiagnosticResult("DFCS002", DiagnosticSeverity.Warning)
            .WithLocation(1)
            .WithArguments("SaveButton");
        await CreateAnalyzerTest(duplicate, first, second).RunAsync();
    }

    [Fact]
    public async Task AdvisoryAnalyzer_DoesNotOfferDiagnosisForSafeExistingLiteral()
    {
        const string source = """
            using Microsoft.Maui.Controls;
            public class Page
            {
                void Build()
                {
                    var save = new Button { AutomationId = "SaveButton", Text = "Save" };
                }
            }
            """;

        await CreateAnalyzerTest(source).RunAsync();
    }

    [Theory]
    [InlineData("""
        using Microsoft.Maui.Controls;
        public class Page { void Build() { var save = new Button { Text = "Save" }; } }
        """, "new Button", null)]
    [InlineData("""
        using Microsoft.Maui.Controls;
        public class Page { void Build() { var save = new Button { AutomationId = "OldSave", Text = "Save" }; } }
        """, "new Button", "OldSave")]
    [InlineData("""
        using Microsoft.Maui.Controls;
        public class Page { void Build() { var save = new Button(); save.AutomationId = "OldSave"; } }
        """, "save.AutomationId", "OldSave")]
    [InlineData("""
        using Microsoft.Maui.Controls;
        public class Page
        {
            private static Button Save = new Button { Text = "Save" };
            void Build() { Save.AutomationId = "OldSave"; }
        }
        """, "Save.AutomationId", "OldSave")]
    public void RoslynBuilder_ValidInitializerAndAssignment_ProducesMinimalPatch(
        string source,
        string marker,
        string? expectedOld)
    {
        var (model, position) = SemanticModelAt(source, marker);
        var result = CSharpAutomationIdProposalBuilder.Analyze(model, position, "SaveButton");

        Assert.True(result.CanCreateMinimalPatch, string.Join("; ", result.Reasons.Select(reason => reason.Message)));
        Assert.Equal(expectedOld, result.OldAutomationId);
        Assert.NotNull(result.PatchStart);
        Assert.NotNull(result.PatchLength);
        Assert.Contains("SaveButton", result.Replacement, StringComparison.Ordinal);
        Assert.True(result.IsSupportedActionableControl);
    }

    [Theory]
    [InlineData("""
        using Microsoft.Maui.Controls;
        class DataTemplate { public DataTemplate(System.Action action) {} }
        class Page { void Build() { var t = new DataTemplate(() => { var save = new Button { Text = "Save" }; }); } }
        """, "new Button", "template-or-repeater")]
    [InlineData("""
        using System.Collections.Generic;
        using Microsoft.Maui.Controls;
        class Page { void Build() { var buttons = new List<Button> { new Button { Text = "Save" } }; } }
        """, "new Button", "collection-lambda-or-factory")]
    [InlineData("""
        using Microsoft.Maui.Controls;
        class Page
        {
            void Build()
            {
        #if true
                var save = new Button { Text = "Save" };
        #endif
            }
        }
        """, "new Button", "conditional-or-preprocessor")]
    [InlineData("""
        using Microsoft.Maui.Controls;
        class Page { void Build() { dynamic save = new Button { Text = "Save" }; } }
        """, "new Button", "reflection-or-dynamic-construction")]
    [InlineData("""
        using Microsoft.Maui.Controls;
        class Page { string Id() => "SaveButton"; void Build() { var save = new Button { AutomationId = Id() }; } }
        """, "new Button", "computed-or-bound-automation-id")]
    [InlineData("""
        using Microsoft.Maui.Controls;
        class Page { void Build() { var view = new WebView { AutomationId = "Web" }; } }
        """, "new WebView", "unsupported-control-type")]
    public void RoslynBuilder_UnsafeConstructs_ReturnExplicitRejection(
        string source,
        string marker,
        string expectedCode)
    {
        var (model, position) = SemanticModelAt(source, marker);
        var result = CSharpAutomationIdProposalBuilder.Analyze(model, position, "SaveButton");

        Assert.Contains(result.Reasons, reason => reason.Code == expectedCode);
        Assert.False(result.CanCreateMinimalPatch);
    }

    [Fact]
    public void EligibilityAnalyzer_RejectsEveryNonRoslynSafetyGate()
    {
        const string source = "var save = new Button { Text = \"Save\" };";
        var result = MauiCSharpSourceEligibilityAnalyzer.Analyze(new MauiCSharpSourceEligibilityInput
        {
            SourceText = source,
            FileRelativePath = "obj/Generated.g.cs",
            ExpectedSourceHash = MauiAutomationIdProposalPolicy.ComputeSourceHash(source),
            SourceLine = 1,
            SourceColumn = 12,
            SourceSpanStart = 11,
            SourceSpanLength = 30,
            SourceConfidence = "roslyn-proven",
            IsProjectContained = false,
            IsRegisteredProjectFile = false,
            IsGenerated = true,
            IsLinked = true,
            HasReparsePoint = true,
            IsNativeOrWebViewSynthetic = true,
            IsVirtualizedOrTemplated = true,
            HasRoslynSemanticModel = false,
            HasResolvedSymbol = false,
            IsSupportedActionableControl = false,
            IsDirectObjectInitializer = false,
            IsDirectLiteralAssignment = false,
            IsSingleUnambiguousSite = false,
            IsInsideTemplateOrRepeater = true,
            IsInsideCollectionLambdaOrFactory = true,
            HasConditionalOrPreprocessorBranch = true,
            HasReflectionOrDynamicConstruction = true,
            HasComputedOrBoundAutomationId = true,
            ExistingAutomationId = "OldSave",
            ProposedAutomationId = "UserName",
            ProjectAutomationIds = ["UserName"],
            LiveAutomationIds = ["UserName"],
            LiveUniquenessAvailable = true,
        });

        Assert.False(result.Decision.Eligible);
        var codes = result.Decision.Reasons.Select(reason => reason.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var code in new[]
                 {
                     MauiCSharpSourceIneligibilityCodes.SourceFileOutsideProject,
                     MauiCSharpSourceIneligibilityCodes.SourceFileUnregistered,
                     MauiCSharpSourceIneligibilityCodes.SourceFileGenerated,
                     MauiCSharpSourceIneligibilityCodes.SourceFileLinked,
                     MauiCSharpSourceIneligibilityCodes.SourcePathReparsePoint,
                     MauiCSharpSourceIneligibilityCodes.NativeOrWebViewSynthetic,
                     MauiCSharpSourceIneligibilityCodes.RepeaterOrVirtualized,
                     MauiCSharpSourceIneligibilityCodes.RoslynSemanticModelUnavailable,
                     MauiCSharpSourceIneligibilityCodes.SemanticSymbolUnresolved,
                     MauiCSharpSourceIneligibilityCodes.UnsupportedControlType,
                     MauiCSharpSourceIneligibilityCodes.UnsupportedSyntax,
                     MauiCSharpSourceIneligibilityCodes.AmbiguousConstructionOrAssignment,
                     MauiCSharpSourceIneligibilityCodes.TemplateOrRepeater,
                     MauiCSharpSourceIneligibilityCodes.CollectionOrFactory,
                     MauiCSharpSourceIneligibilityCodes.ConditionalOrPreprocessor,
                     MauiCSharpSourceIneligibilityCodes.ReflectionOrDynamic,
                     MauiCSharpSourceIneligibilityCodes.ComputedOrBoundValue,
                     MauiCSharpSourceIneligibilityCodes.AutomationIdLocalizedOrUserDerived,
                     MauiCSharpSourceIneligibilityCodes.AutomationIdDuplicateProject,
                     MauiCSharpSourceIneligibilityCodes.AutomationIdDuplicateLive,
                 })
        {
            Assert.Contains(code, codes);
        }
    }

    [Fact]
    public void EligibilityAnalyzer_RejectsUnsafeExistingLiteralReplacement()
    {
        const string source = "var save = new Button { AutomationId = \"UserName\" };";
        var result = MauiCSharpSourceEligibilityAnalyzer.Analyze(new MauiCSharpSourceEligibilityInput
        {
            SourceText = source,
            FileRelativePath = "MainPage.cs",
            ExpectedSourceHash = MauiAutomationIdProposalPolicy.ComputeSourceHash(source),
            SourceLine = 1,
            SourceColumn = 12,
            SourceSpanStart = 11,
            SourceSpanLength = 42,
            SourceConfidence = "roslyn-proven",
            IsProjectContained = true,
            IsRegisteredProjectFile = true,
            HasRoslynSemanticModel = true,
            HasResolvedSymbol = true,
            IsSupportedActionableControl = true,
            IsDirectObjectInitializer = true,
            IsSingleUnambiguousSite = true,
            ExistingAutomationId = "UserName",
            ProposedAutomationId = "SaveButton",
            ProjectAutomationIds = [],
            LiveAutomationIds = [],
            LiveUniquenessAvailable = true,
        });

        Assert.False(result.Decision.Eligible);
        Assert.Contains(
            result.Decision.Reasons,
            reason => reason.Code == MauiCSharpSourceIneligibilityCodes.ComputedOrBoundValue);
    }

    [Fact]
    public async Task BuildAsync_ObjectInitializerPreservesTriviaAndCreatesRollbackPatch()
    {
        const string source = """
            using Microsoft.Maui.Controls;

            public class Page
            {
                void Build()
                {
                    var save = new Button
                    {
                        // Keep this hand-authored trivia.
                        Text = "Save",
                    };
                }
            }

            namespace Microsoft.Maui.Controls
            {
                public class Button
                {
                    public string AutomationId { get; set; } = "";
                    public string Text { get; set; } = "";
                }
            }
            """;
        var project = await CreateProjectAsync(source);
        var (line, column) = LineColumn(source, "new Button");
        var element = Element(project.SourcePath, source, line, column);
        var service = new CSharpAutomationIdProposalService(project.ProjectPath);

        var result = await service.BuildAsync(element, "SaveButton", [element]);

        Assert.True(result.Ok, result.Error);
        var proposal = Assert.IsType<MauiCSharpSourceProposal>(result.Proposal);
        Assert.Equal("CSharp", proposal.Language);
        Assert.Equal("add-literal-automation-id", proposal.Operation.Kind);
        Assert.Contains("AutomationId = \"SaveButton\"", proposal.Patch.Replacement, StringComparison.Ordinal);
        var preview = source.Remove(proposal.Patch.Start!.Value, proposal.Patch.Length!.Value)
            .Insert(proposal.Patch.Start.Value, proposal.Patch.Replacement!);
        Assert.Contains("// Keep this hand-authored trivia.", preview, StringComparison.Ordinal);
        Assert.Contains("AutomationId = \"SaveButton\"", preview, StringComparison.Ordinal);
        Assert.Equal(proposal.Patch.AfterDigest, proposal.RollbackPatch.BeforeDigest);
        Assert.Equal(proposal.BaseContentDigest, proposal.RollbackPatch.AfterDigest);
        Assert.Contains("broker-never-writes-csharp-source", proposal.RiskFlags);
        Assert.Equal(source, await File.ReadAllTextAsync(project.SourcePath));
    }

    [Fact]
    public async Task BuildAsync_RejectsGeneratedLinkedAndVirtualizedDeclarations()
    {
        const string source = """
            using Microsoft.Maui.Controls;
            public class Page
            {
                void Build()
                {
                    var save = new Button { Text = "Save" };
                }
            }
            namespace Microsoft.Maui.Controls
            {
                public class Button { public string AutomationId { get; set; } = ""; public string Text { get; set; } = ""; }
            }
            """;
        var project = await CreateProjectAsync(source, generatedFileName: "MainPage.g.cs");
        var (line, column) = LineColumn(source, "new Button");
        var element = Element(project.SourcePath, source, line, column);
        element.IsVirtualized = true;
        var service = new CSharpAutomationIdProposalService(project.ProjectPath);

        var result = await service.BuildAsync(element, "SaveButton", [element]);

        Assert.False(result.Ok);
        var reasons = result.Analysis!.Decision.Reasons.Select(reason => reason.Code).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(MauiCSharpSourceIneligibilityCodes.SourceFileGenerated, reasons);
        Assert.Contains(MauiCSharpSourceIneligibilityCodes.RepeaterOrVirtualized, reasons);
    }

    [Fact]
    public async Task BuildAsync_RejectsAutomationIdAlreadyDeclaredInProjectXaml()
    {
        const string source = """
            using Microsoft.Maui.Controls;
            public class Page
            {
                void Build()
                {
                    var save = new Button { Text = "Save" };
                }
            }
            namespace Microsoft.Maui.Controls
            {
                public class Button { public string AutomationId { get; set; } = ""; public string Text { get; set; } = ""; }
            }
            """;
        var project = await CreateProjectAsync(
            source,
            xaml: "<ContentPage><Button AutomationId=\"SaveButton\" /></ContentPage>");
        var (line, column) = LineColumn(source, "new Button");
        var element = Element(project.SourcePath, source, line, column);

        var result = await new CSharpAutomationIdProposalService(project.ProjectPath)
            .BuildAsync(element, "SaveButton", [element]);

        Assert.False(result.Ok);
        Assert.Contains(
            result.Analysis!.Decision.Reasons,
            reason => reason.Code == MauiCSharpSourceIneligibilityCodes.AutomationIdDuplicateProject);
    }

    [Fact]
    public async Task Store_RequiresIDEAcknowledgmentAndRollbackAfterFailedVerification()
    {
        var proposal = await BuildProposalAsync();
        var store = new WorkflowCSharpSourceProposalStore();
        Assert.True(store.Propose(proposal).Ok);
        Assert.True(store.Preview(proposal.ProposalId).Ok);
        var host = new WorkflowCSharpSourceHostCapability
        {
            HostKind = "vscode",
            CanApplyCSharpSource = true,
            CanOpenNativeDiff = true,
            IsExplicitLocalHostAction = true,
        };
        var binding = Binding(proposal, "vscode");
        var grant = store.IssueGrant(new WorkflowCSharpSourceGrantIssueRequest
        {
            ProposalId = proposal.ProposalId,
            Kind = WorkflowCSharpSourceGrantKinds.Apply,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = binding,
        });
        Assert.True(grant.Ok, grant.Error);
        Assert.True(store.AwaitHostApply(proposal.ProposalId, binding, host).Ok);
        Assert.True(store.BeginHostApply(proposal.ProposalId, grant.Grant, binding, host).Ok);

        var applied = store.CompleteHostApply(proposal.ProposalId, new WorkflowCSharpSourceHostApplyRecord
        {
            Applied = true,
            PreContentDigest = proposal.BaseContentDigest,
            AppliedContentDigest = proposal.Patch.AfterDigest,
            PatchDigest = proposal.PatchDigest,
            ApplyRunId = "vscode-run",
        });
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal(MauiCSharpSourceProposalStates.Applied, applied.Proposal!.State);

        var failed = store.RecordVerification(proposal.ProposalId, new WorkflowCSharpSourceVerificationRecord
        {
            Platforms = [],
            AffectedFlowsReplayed = false,
            IndependentOracleSucceeded = false,
        });
        Assert.True(failed.Ok, failed.Error);
        Assert.Equal(MauiCSharpSourceProposalStates.RollbackRequired, failed.Proposal!.State);

        var rollbackBinding = Binding(proposal, "vscode", proposal.Patch.AfterDigest);
        var rollbackGrant = store.IssueGrant(new WorkflowCSharpSourceGrantIssueRequest
        {
            ProposalId = proposal.ProposalId,
            Kind = WorkflowCSharpSourceGrantKinds.Rollback,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = rollbackBinding,
        });
        Assert.True(rollbackGrant.Ok, rollbackGrant.Error);
        Assert.True(store.BeginRollback(proposal.ProposalId, rollbackGrant.Grant, rollbackBinding, host).Ok);
        var reverted = store.CompleteRollback(proposal.ProposalId, new WorkflowCSharpSourceRollbackRecord
        {
            Reverted = true,
            PreContentDigest = proposal.Patch.AfterDigest,
            ContentDigest = proposal.BaseContentDigest,
            PatchDigest = proposal.RollbackPatchDigest,
        });
        Assert.True(reverted.Ok, reverted.Error);
        Assert.Equal(MauiCSharpSourceProposalStates.Reverted, reverted.Proposal!.State);
    }

    [Fact]
    public async Task Store_RejectsAgentOriginatedCSharpProposal()
    {
        var proposal = await BuildProposalAsync();
        var store = new WorkflowCSharpSourceProposalStore();

        var result = store.Propose(proposal, agentOriginated: true);

        Assert.False(result.Ok);
        Assert.Equal("agent-source-proposal-forbidden", result.Code);
    }

    [Fact]
    public void CSharpSourceApplyAndRollbackAcknowledgments_AreBlockedDuringReplay()
    {
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/csharp/proposal/begin-host-apply"));
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/csharp/proposal/apply-ack"));
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/csharp/proposal/begin-rollback"));
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/csharp/proposal/rollback-ack"));
        Assert.False(InspectorServer.IsBlockedDuringReplay("/api/workbench/source/csharp/proposal/preview"));
    }

    [Fact]
    public async Task Store_KeepsAppleExternalQaPendingWithoutClaimingVerification()
    {
        var proposal = await BuildProposalAsync();
        var store = new WorkflowCSharpSourceProposalStore();
        Assert.True(store.Propose(proposal).Ok);
        Assert.True(store.Preview(proposal.ProposalId).Ok);
        var host = new WorkflowCSharpSourceHostCapability
        {
            HostKind = "vscode",
            CanApplyCSharpSource = true,
            IsExplicitLocalHostAction = true,
        };
        var binding = Binding(proposal, "vscode");
        var grant = store.IssueGrant(new WorkflowCSharpSourceGrantIssueRequest
        {
            ProposalId = proposal.ProposalId,
            Kind = WorkflowCSharpSourceGrantKinds.Apply,
            Reviewer = "reviewer",
            HumanConfirmed = true,
            Binding = binding,
        });
        Assert.True(grant.Ok, grant.Error);
        Assert.True(store.AwaitHostApply(proposal.ProposalId, binding, host).Ok);
        Assert.True(store.BeginHostApply(proposal.ProposalId, grant.Grant, binding, host).Ok);
        Assert.True(store.CompleteHostApply(proposal.ProposalId, new WorkflowCSharpSourceHostApplyRecord
        {
            Applied = true,
            PreContentDigest = proposal.BaseContentDigest,
            AppliedContentDigest = proposal.Patch.AfterDigest,
            PatchDigest = proposal.PatchDigest,
        }).Ok);

        var verification = store.RecordVerification(proposal.ProposalId, new WorkflowCSharpSourceVerificationRecord
        {
            Platforms =
            [
                Platform("android", "net10.0-android", true),
                Platform("windows", "net10.0-windows10.0.19041.0", true),
                Platform("ios", "net10.0-ios", false, pendingExternalQa: true),
                Platform("maccatalyst", "net10.0-maccatalyst", false, pendingExternalQa: true),
                // Experimental AppKit is separately pending; it neither removes nor satisfies
                // the official Mac Catalyst verification requirement.
                Platform("macos", "net10.0-macos", false, pendingExternalQa: true),
            ],
            AffectedFlowsReplayed = true,
            IndependentOracleSucceeded = true,
        });

        Assert.True(verification.Ok, verification.Error);
        Assert.Equal(MauiCSharpSourceProposalStates.Applied, verification.Proposal!.State);
        Assert.Contains("pending-external-qa", verification.Proposal.Verification!.Reasons);
    }

    private async Task<MauiCSharpSourceProposal> BuildProposalAsync()
    {
        const string source = """
            using Microsoft.Maui.Controls;
            public class Page
            {
                void Build()
                {
                    var save = new Button { Text = "Save" };
                }
            }
            namespace Microsoft.Maui.Controls
            {
                public class Button
                {
                    public string AutomationId { get; set; } = "";
                    public string Text { get; set; } = "";
                }
            }
            """;
        var project = await CreateProjectAsync(source);
        var (line, column) = LineColumn(source, "new Button");
        var built = await new CSharpAutomationIdProposalService(project.ProjectPath)
            .BuildAsync(Element(project.SourcePath, source, line, column), "SaveButton", []);
        Assert.True(built.Ok, built.Error);
        return built.Proposal!;
    }

    private static WorkflowCSharpSourceGrantBinding Binding(
        MauiCSharpSourceProposal proposal,
        string hostKind,
        string? contentDigest = null)
        => new()
        {
            FileRelativePath = proposal.Operation.FileRelativePath,
            BaseContentDigest = contentDigest ?? proposal.BaseContentDigest,
            SourceHash = proposal.Operation.SourceHash,
            PatchDigest = proposal.PatchDigest,
            RollbackPatchDigest = proposal.RollbackPatchDigest,
            ProjectIdentity = "sha256:" + new string('1', 64),
            FlowReferencesDigest = WorkflowCSharpSourceProposalStore.ComputeFlowReferencesDigest(proposal.AffectedFlows),
            HostKind = hostKind,
        };

    private static WorkflowCSharpSourcePlatformVerificationResult Platform(
        string platform,
        string targetFramework,
        bool succeeded,
        bool pendingExternalQa = false)
        => new()
        {
            Platform = platform,
            TargetFramework = targetFramework,
            BuildSucceeded = succeeded,
            PendingExternalQa = pendingExternalQa,
            RuntimeRemapConfirmed = succeeded,
            AutomationIdUnique = succeeded,
            ReplaySucceeded = succeeded,
            IndependentOracleSucceeded = succeeded,
        };

    private static CSharpAnalyzerTest<CSharpAutomationIdAdvisoryAnalyzer, DefaultVerifier> CreateAnalyzerTest(
        string source,
        params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<CSharpAutomationIdAdvisoryAnalyzer, DefaultVerifier>
        {
            TestCode = source + MauiStubs,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test;
    }

    private static (SemanticModel Model, int Position) SemanticModelAt(string source, string marker)
    {
        var tree = CSharpSyntaxTree.ParseText(source + MauiStubs);
        var compilation = CSharpCompilation.Create(
            "CSharpProposalTests",
            [tree],
            TrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var position = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(position >= 0);
        return (compilation.GetSemanticModel(tree), position);
    }

    private static IEnumerable<MetadataReference> TrustedPlatformReferences()
        => ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => MetadataReference.CreateFromFile(path));

    private async Task<(string ProjectPath, string SourcePath)> CreateProjectAsync(
        string source,
        string sourceFileName = "MainPage.cs",
        string? generatedFileName = null,
        string? xaml = null)
    {
        var root = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "TestApp.csproj");
        var sourcePath = Path.Combine(root, generatedFileName ?? sourceFileName);
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(sourcePath, source);
        if (xaml is not null)
            await File.WriteAllTextAsync(Path.Combine(root, "MainPage.xaml"), xaml);
        return (projectPath, sourcePath);
    }

    private static ElementInfo Element(string path, string source, int line, int column)
        => new()
        {
            Id = "save",
            Type = "Button",
            FullType = "Microsoft.Maui.Controls.Button",
            Framework = "maui",
            IsVisible = true,
            IsEnabled = true,
            SourceFile = path,
            SourceLine = line,
            SourceColumn = column,
            SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)), 0, 8).ToLowerInvariant(),
            SourceConfidence = "mapped",
        };

    private static (int Line, int Column) LineColumn(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var line = 1;
        var lineStart = 0;
        for (var offset = 0; offset < index; offset++)
        {
            if (source[offset] == '\n')
            {
                line++;
                lineStart = offset + 1;
            }
        }
        return (line, index - lineStart + 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
