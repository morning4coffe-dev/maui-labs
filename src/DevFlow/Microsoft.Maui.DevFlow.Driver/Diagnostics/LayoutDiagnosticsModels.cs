using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Driver;

/// <summary>
/// Client-side view of the agent's on-demand layout diagnostics report
/// (<c>GET|POST /api/v1/ui/diagnostics/layout</c>).
///
/// The report is deliberately narrow and truthful: it describes managed MAUI layout state only.
/// It never asserts clipping, occlusion, text truncation, or an accessibility mismatch, and an
/// absent finding is never a guarantee of correctness — see <see cref="LayoutCoverage"/>.
/// </summary>
public class LayoutDiagnosticsReport
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "";

    [JsonPropertyName("ruleSetVersion")]
    public string RuleSetVersion { get; set; } = "";

    [JsonPropertyName("capturedUtc")]
    public string CapturedUtc { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "unknown";

    [JsonPropertyName("scope")]
    public LayoutScope Scope { get; set; } = new();

    [JsonPropertyName("coverage")]
    public LayoutCoverage Coverage { get; set; } = new();

    [JsonPropertyName("summary")]
    public LayoutSummary Summary { get; set; } = new();

    [JsonPropertyName("findings")]
    public List<LayoutFinding> Findings { get; set; } = [];
}

public class LayoutScope
{
    [JsonPropertyName("rootElementId")]
    public string? RootElementId { get; set; }

    [JsonPropertyName("window")]
    public int? Window { get; set; }

    [JsonPropertyName("maxElements")]
    public int MaxElements { get; set; }

    [JsonPropertyName("elementsExamined")]
    public int ElementsExamined { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    [JsonPropertyName("windowBounds")]
    public LayoutRect? WindowBounds { get; set; }
}

public class LayoutCoverage
{
    /// <summary><c>full</c>, <c>partial</c>, or <c>unavailable</c>.</summary>
    [JsonPropertyName("overall")]
    public string Overall { get; set; } = "unavailable";

    [JsonPropertyName("rules")]
    public List<LayoutRuleCoverage> Rules { get; set; } = [];

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = [];

    [JsonPropertyName("neverCaptured")]
    public List<string> NeverCaptured { get; set; } = [];
}

public class LayoutRuleCoverage
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("support")]
    public string Support { get; set; } = "unavailable";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "medium";

    [JsonPropertyName("evaluated")]
    public int Evaluated { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = [];
}

public class LayoutSummary
{
    [JsonPropertyName("violations")]
    public int Violations { get; set; }

    [JsonPropertyName("observations")]
    public int Observations { get; set; }

    [JsonPropertyName("incomplete")]
    public int Incomplete { get; set; }
}

public class LayoutFinding
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";

    /// <summary><c>violation</c>, <c>observation</c>, or <c>incomplete</c>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "observation";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "medium";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";

    [JsonPropertyName("element")]
    public LayoutElementReference? Element { get; set; }

    [JsonPropertyName("parent")]
    public LayoutElementReference? Parent { get; set; }

    [JsonPropertyName("evidence")]
    public LayoutFindingEvidence? Evidence { get; set; }

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = [];
}

public class LayoutElementReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("automationId")]
    public string? AutomationId { get; set; }

    [JsonPropertyName("sourceFile")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("sourceLine")]
    public int? SourceLine { get; set; }

    [JsonPropertyName("sourceColumn")]
    public int? SourceColumn { get; set; }
}

public class LayoutFindingEvidence
{
    [JsonPropertyName("frame")]
    public LayoutRect? Frame { get; set; }

    [JsonPropertyName("windowBounds")]
    public LayoutRect? WindowBounds { get; set; }

    [JsonPropertyName("parentWindowBounds")]
    public LayoutRect? ParentWindowBounds { get; set; }

    [JsonPropertyName("desiredSize")]
    public LayoutSize? DesiredSize { get; set; }

    [JsonPropertyName("explicitWidth")]
    public double? ExplicitWidth { get; set; }

    [JsonPropertyName("explicitHeight")]
    public double? ExplicitHeight { get; set; }

    [JsonPropertyName("constraint")]
    public string? Constraint { get; set; }

    [JsonPropertyName("constraintValue")]
    public double? ConstraintValue { get; set; }

    [JsonPropertyName("actualValue")]
    public double? ActualValue { get; set; }

    [JsonPropertyName("overflowWidth")]
    public double? OverflowWidth { get; set; }

    [JsonPropertyName("overflowHeight")]
    public double? OverflowHeight { get; set; }

    [JsonPropertyName("affectedElements")]
    public int? AffectedElements { get; set; }
}

public class LayoutRect
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public class LayoutSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}
