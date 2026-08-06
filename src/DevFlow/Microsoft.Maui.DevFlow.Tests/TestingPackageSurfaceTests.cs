using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Maui.DevFlow.Testing;
using YamlDotNet.Serialization;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed partial class TestingPackageSurfaceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TestingPackage_DeclaresShippingPreviewMetadataAndNoBundledDependencies()
    {
        var project = ReadRepositoryFile("src/DevFlow/Microsoft.Maui.DevFlow.Testing/Microsoft.Maui.DevFlow.Testing.csproj");

        Assert.Contains("<TargetFramework>net9.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<IsPackable>true</IsPackable>", project, StringComparison.Ordinal);
        Assert.Contains("<IsShipping>true</IsShipping>", project, StringComparison.Ordinal);
        Assert.Contains("<PackageId>Microsoft.Maui.DevFlow.Testing</PackageId>", project, StringComparison.Ordinal);
        Assert.Contains("Experimental preview", project, StringComparison.Ordinal);
        Assert.Contains("preview experimental", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", project, StringComparison.Ordinal);
        Assert.Contains("<PackRepoRootReadme>false</PackRepoRootReadme>", project, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Maui.DevFlow.Driver", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Maui.Cli", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Broker", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider", project, StringComparison.Ordinal);

        var buildProperties = ReadRepositoryFile("Directory.Build.props");
        var packageProperties = ReadRepositoryFile("eng/Common.props");
        Assert.Contains("<DebugType>embedded</DebugType>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("<DebugSymbols>true</DebugSymbols>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("<PublishRepositoryUrl>true</PublishRepositoryUrl>", packageProperties, StringComparison.Ordinal);
        Assert.Contains("<EmbedUntrackedSources>true</EmbedUntrackedSources>", packageProperties, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "eng", "common", "post-build", "symbols-validation.ps1")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "eng", "common", "post-build", "sourcelink-validation.ps1")));
    }

    [Fact]
    public void TestingPackage_SourceGeneratedSerialization_RoundTripsPublicFlowContract()
    {
        var original = new MauiFlow
        {
            Name = "package-consumer-smoke",
            Platform = "android",
        };

        var json = JsonSerializer.Serialize(original, MauiFlowJsonContext.Default.MauiFlow);
        var roundTripped = JsonSerializer.Deserialize(json, MauiFlowJsonContext.Default.MauiFlow);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Name, roundTripped!.Name);
        Assert.Equal(original.Platform, roundTripped.Platform);
    }

    [Fact]
    public void PackageConsumer_UsesOnlyPackedLocalFeedAndDeclaresTheFullCompileMatrix()
    {
        var project = ReadRepositoryFile("tests/DevFlow/PackageConsumer/Microsoft.Maui.DevFlow.Testing.PackageConsumer.csproj");
        var packageVersions = ReadRepositoryFile("tests/DevFlow/PackageConsumer/Directory.Packages.props");
        var script = ReadRepositoryFile("tests/DevFlow/PackageConsumer/Validate-TestingPackage.ps1");

        Assert.Contains(
            "net9.0;net10.0;net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041.0;net10.0-macos",
            project,
            StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Microsoft.Maui.DevFlow.Testing\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", project, StringComparison.Ordinal);
        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", packageVersions, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Microsoft.Maui.DevFlow.Testing\"", packageVersions, StringComparison.Ordinal);
        Assert.Contains("devflow-testing-local", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Maui.DevFlow.Driver", script, StringComparison.Ordinal);
        Assert.Contains("Package-only local-feed restore", script, StringComparison.Ordinal);
        Assert.Contains("no app or device runtime was started", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionFiltersAndWorkflows_IncludeTestingPackageAndPackageValidation()
    {
        foreach (var solutionPath in new[]
        {
            "MauiLabs.slnx",
            "src/DevFlow/DevFlow.slnf",
            "src/Cli/Cli.slnf",
        })
        {
            Assert.Contains("Microsoft.Maui.DevFlow.Testing", ReadRepositoryFile(solutionPath), StringComparison.Ordinal);
        }

        var devFlowWorkflow = ReadRepositoryFile(".github/workflows/ci-devflow.yml");
        var cliWorkflow = ReadRepositoryFile(".github/workflows/ci-cli.yml");
        var buildWorkflow = ReadRepositoryFile(".github/workflows/_build.yml");
        var officialPipeline = ReadRepositoryFile("eng/pipelines/devflow-official.yml");

        Assert.Contains("tests/DevFlow/**", devFlowWorkflow, StringComparison.Ordinal);
        Assert.Contains("tests/DevFlow/PackageConsumer/**", cliWorkflow, StringComparison.Ordinal);
        Assert.Contains("pack-driver: true", devFlowWorkflow, StringComparison.Ordinal);
        Assert.Contains("PackDriverNuGet", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Maui.DevFlow.Testing", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("Validate-TestingPackage.ps1", buildWorkflow, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Maui.DevFlow.Testing", officialPipeline, StringComparison.Ordinal);
        Assert.Contains("MacOSPackageConsumer", officialPipeline, StringComparison.Ordinal);
        Assert.Contains("publish_devflow_nuget", officialPipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageWorkflowsAndOfficialPipeline_AreValidYaml()
    {
        var deserializer = new DeserializerBuilder().Build();
        foreach (var path in new[]
        {
            ".github/workflows/_build.yml",
            ".github/workflows/ci-devflow.yml",
            ".github/workflows/ci-cli.yml",
            "eng/pipelines/devflow-official.yml",
        })
        {
            var parsed = deserializer.Deserialize(new StringReader(ReadRepositoryFile(path)));
            Assert.NotNull(parsed);
        }
    }

    [Fact]
    public void ContractIndex_ListsStableIdsAndPreviewStatusForTestingSurfaces()
    {
        var index = ReadRepositoryFile("docs/DevFlow/spec/README.md");
        var schemas = new[]
        {
            "maui-test-plan-v1.json",
            "maui-flow-run-report-v1.json",
            "broker-workflow-run-v1.json",
            "maui-artifact-trust-v1.json",
            "broker-artifact-trust-v1.json",
            "maui-flow-repair-proposal-v1.json",
            "maui-flow-repair-outcome-v1.json",
            "maui-xaml-source-proposal-v1.json",
            "maui-csharp-source-proposal-v1.json",
            "maui-test-agent-protocol-v1.json",
            "maui-preview-qualification-v1.json",
        };

        foreach (var schemaName in schemas)
        {
            var schemaPath = Path.Combine(RepositoryRoot, "docs", "DevFlow", "spec", "schemas", schemaName);
            using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
            var id = document.RootElement.GetProperty("$id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Contains(schemaName, index, StringComparison.Ordinal);
            Assert.Contains(id!, index, StringComparison.Ordinal);
        }

        Assert.Contains("Preview contract", index, StringComparison.Ordinal);
        Assert.Contains("not-qualified", index, StringComparison.Ordinal);
        Assert.Contains("broker-workflow-runs-v1.yaml", index, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageAndPlatformDocumentation_StatesPreviewAndQualificationBoundaries()
    {
        var packageReadme = ReadRepositoryFile("src/DevFlow/Microsoft.Maui.DevFlow.Testing/README.md");
        var productReadme = ReadRepositoryFile("src/DevFlow/README.md");
        var testingGuide = ReadRepositoryFile("docs/DevFlow/testing.md");
        var appiumGuide = ReadRepositoryFile("docs/DevFlow/appium-smoke-testing.md");
        var compatibilityGuide = ReadRepositoryFile("docs/DevFlow/compatibility.md");

        Assert.Contains("xUnit", packageReadme, StringComparison.Ordinal);
        Assert.Contains("NUnit", packageReadme, StringComparison.Ordinal);
        Assert.Contains("MSTest", packageReadme, StringComparison.Ordinal);
        Assert.Contains("not-qualified", packageReadme, StringComparison.Ordinal);
        Assert.Contains("Android, iOS, Mac Catalyst, and Windows", productReadme, StringComparison.Ordinal);
        Assert.Contains("not-qualified", testingGuide, StringComparison.Ordinal);
        Assert.Contains("separate from DevFlow semantic flow execution", appiumGuide, StringComparison.Ordinal);
        Assert.Contains("cannot substitute for semantic flow coverage", appiumGuide, StringComparison.Ordinal);
        Assert.Contains("breaking-change", compatibilityGuide, StringComparison.Ordinal);
        Assert.Contains("binary", compatibilityGuide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source", compatibilityGuide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackageDocumentationLinks_Resolve()
    {
        foreach (var relativePath in new[]
        {
            "src/DevFlow/README.md",
            "src/DevFlow/Microsoft.Maui.DevFlow.Testing/README.md",
            "tests/DevFlow/PackageConsumer/README.md",
            "docs/DevFlow/testing.md",
            "docs/DevFlow/test-agent.md",
            "docs/DevFlow/inspector.md",
            "docs/DevFlow/evidence.md",
            "docs/DevFlow/appium-smoke-testing.md",
            "docs/DevFlow/compatibility.md",
            "docs/DevFlow/spec/README.md",
        })
        {
            var sourcePath = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var sourceDirectory = Path.GetDirectoryName(sourcePath)!;
            var text = File.ReadAllText(sourcePath);
            foreach (Match match in MarkdownLinkRegex().Matches(text))
            {
                if (IsInsideInlineCode(text, match.Index))
                    continue;

                var target = match.Groups["target"].Value;
                if (string.IsNullOrWhiteSpace(target) ||
                    target.StartsWith("#", StringComparison.Ordinal) ||
                    Uri.TryCreate(target, UriKind.Absolute, out _))
                {
                    continue;
                }

                var pathPart = target.Split('#', 2)[0].Replace('/', Path.DirectorySeparatorChar);
                var linkedPath = Path.GetFullPath(Path.Combine(sourceDirectory, pathPart));
                Assert.True(
                    File.Exists(linkedPath) || Directory.Exists(linkedPath),
                    $"{relativePath} links to missing local target '{target}'.");
            }
        }
    }

    [GeneratedRegex(@"(?<!!)\[[^\]]+\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLinkRegex();

    private static bool IsInsideInlineCode(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', index);
        var prefix = text[(lineStart + 1)..index];
        return prefix.Count(character => character == '`') % 2 != 0;
    }

    private static string ReadRepositoryFile(string relativePath)
        => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MauiLabs.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the repository root for package surface tests.");
    }
}
