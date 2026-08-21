using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class LayoutDiagnosticsPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"devflow-layout-policy-{Guid.NewGuid():N}");

    public LayoutDiagnosticsPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void UpdateProjectPolicy_PreservesExistingConfiguration()
    {
        var path = Path.Combine(_root, ".mauidevflow");
        File.WriteAllText(path, """{"port":9223,"other":{"enabled":true}}""");

        LayoutDiagnosticsPolicyLoader.UpdateProjectPolicy(
            _root,
            policy => policy.Suppressions.Add(new LayoutSuppression
            {
                Fingerprint = "abc123",
                Reason = "Intentional overlap",
            }));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(9223, document.RootElement.GetProperty("port").GetInt32());
        Assert.True(document.RootElement.GetProperty("other").GetProperty("enabled").GetBoolean());
        var suppression = Assert.Single(
            document.RootElement
                .GetProperty("layoutDiagnostics")
                .GetProperty("suppressions")
                .EnumerateArray());
        Assert.Equal("abc123", suppression.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public void LoadProjectPolicy_FindsTheNearestAncestorConfig()
    {
        var child = Path.Combine(_root, "src", "Views");
        Directory.CreateDirectory(child);
        File.WriteAllText(
            Path.Combine(_root, ".mauidevflow"),
            """
            {
              "layoutDiagnostics": {
                "suppressions": [
                  { "ruleId": "layout.element-clipped", "automationId": "Hero" }
                ]
              }
            }
            """);

        var policy = LayoutDiagnosticsPolicyLoader.LoadProjectPolicy(child);

        var suppression = Assert.Single(policy.Suppressions);
        Assert.Equal("layout.element-clipped", suppression.RuleId);
        Assert.Equal("Hero", suppression.AutomationId);
    }

    [Fact]
    public void SuppressionMatcher_UsesFingerprintSourceAndRelatedElement()
    {
        var finding = new LayoutFinding
        {
            Id = "finding-id",
            SuppressionKey = "stable-key",
            RuleId = "layout.element-clipped",
            Element = new LayoutElementReference
            {
                Id = "child",
                Type = "Label",
                AutomationId = "Title",
                SourceFile = Path.Combine(_root, "Views", "MainPage.xaml"),
                SourceLine = 42,
            },
            RelatedElements =
            [
                new LayoutRelatedElement
                {
                    Relation = "clipper",
                    Element = new LayoutElementReference
                    {
                        Id = "host",
                        Type = "Grid",
                        AutomationId = "Host",
                    },
                }
            ],
        };

        Assert.True(LayoutDiagnosticsSuppressionMatcher.Matches(
            new LayoutSuppression
            {
                Fingerprint = "stable-key",
                SourceFile = "Views/MainPage.xaml",
                SourceLineStart = 40,
                SourceLineEnd = 45,
                RelatedAutomationId = "Host",
            },
            finding));
        Assert.False(LayoutDiagnosticsSuppressionMatcher.Matches(
            new LayoutSuppression { Fingerprint = "different" },
            finding));
    }

    [Fact]
    public void InvalidProjectPolicy_IsSurfacedInsteadOfSilentlyIgnored()
    {
        File.WriteAllText(Path.Combine(_root, ".mauidevflow"), "{ invalid json");

        var error = Assert.Throws<InvalidOperationException>(() =>
            LayoutDiagnosticsPolicyLoader.LoadProjectPolicy(_root));

        Assert.Contains(".mauidevflow", error.Message, StringComparison.Ordinal);
    }
}
