using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Agent.Core.LayoutDiagnostics;

/// <summary>
/// Wire constants for the on-demand layout diagnostics report.
///
/// The subsystem is deliberately narrow: it is a single, explicit, one-shot pass over the
/// <em>managed</em> MAUI layout state. It never watches for changes, never drives the platform
/// accessibility/automation stack, never traverses a WebView, and never reads element text.
/// Anything it cannot observe from managed layout state is reported as
/// <see cref="LayoutOutcomes.Incomplete"/> — never as a pass.
/// </summary>
public static class LayoutDiagnosticsFormat
{
    /// <summary>Payload shape version. Bump when a field is removed or changes meaning.</summary>
    public const string SchemaVersion = "1.0";

    /// <summary>Rule semantics version. Bump when a rule's outcome or threshold changes.</summary>
    public const string RuleSetVersion = "1.0";

    /// <summary>Hard cap on elements examined in one report, regardless of the request.</summary>
    public const int MaxElements = 5_000;

    /// <summary>Default element budget when the caller does not ask for one.</summary>
    public const int DefaultMaxElements = 2_000;

    /// <summary>
    /// Geometry tolerance in device-independent units. Layout arithmetic accumulates rounding on
    /// every platform, so a difference under this is treated as "equal" by every rule.
    /// </summary>
    public const double Tolerance = 0.5;

    /// <summary>
    /// Relative slack applied on top of <see cref="Tolerance"/> before a desired-size overflow is
    /// considered material, so a 1% measure/arrange rounding difference is not reported.
    /// </summary>
    public const double RelativeTolerance = 0.02;

    /// <summary>Data classes this subsystem never reads, surfaced verbatim in every report.</summary>
    public static readonly IReadOnlyList<string> NeverCaptured =
    [
        "Element Text/Value content",
        "Native and framework property dictionaries",
        "BindingContext / view-model object graphs",
        "Platform accessibility or automation tree data",
        "WebView (Blazor/CDP) DOM geometry",
    ];
}

/// <summary>Stable rule identifiers. Also the ordering key for deterministic report output.</summary>
public static class LayoutDiagnosticRules
{
    /// <summary>A visible, realized element was allocated no drawable area.</summary>
    public const string VisibleZeroArea = "layout.visible-zero-area";

    /// <summary>A finite actual dimension falls outside a declared minimum/maximum request.</summary>
    public const string ConstraintViolation = "layout.constraint-violation";

    /// <summary>A visible element with positive area lies entirely outside the known window.</summary>
    public const string OutsideWindow = "layout.outside-window";

    /// <summary>The element's measured desired size materially exceeds its arranged size.</summary>
    public const string DesiredSizeConstrained = "layout.desired-size-constrained";

    /// <summary>A child's arranged rectangle is not contained by its parent's rectangle.</summary>
    public const string ChildOutsideParent = "layout.child-outside-parent";

    /// <summary>Every rule in report order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        VisibleZeroArea,
        ConstraintViolation,
        OutsideWindow,
        DesiredSizeConstrained,
        ChildOutsideParent,
    ];

    internal static int OrderOf(string ruleId)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], ruleId, StringComparison.Ordinal))
                return i;
        }
        return All.Count;
    }
}

/// <summary>What a finding asserts. Absence of a finding is never an assertion of correctness.</summary>
public static class LayoutOutcomes
{
    /// <summary>An observable defect: the reported geometry cannot be correct as authored.</summary>
    public const string Violation = "violation";

    /// <summary>Something worth a human look that may be entirely intentional.</summary>
    public const string Observation = "observation";

    /// <summary>The rule could not be evaluated because the required geometry was unavailable.</summary>
    public const string Incomplete = "incomplete";
}

/// <summary>How much the report trusts a finding or a rule's coverage.</summary>
public static class LayoutConfidence
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
}

/// <summary>How completely a rule could be evaluated over the captured scope.</summary>
public static class LayoutRuleSupport
{
    /// <summary>Every element in scope had the geometry the rule needs.</summary>
    public const string Full = "full";

    /// <summary>Some elements lacked the geometry the rule needs; those are reported incomplete.</summary>
    public const string Partial = "partial";

    /// <summary>No element in scope had the geometry the rule needs.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>The request accepted by <c>/api/v1/ui/diagnostics/layout</c> (all fields optional).</summary>
public sealed class LayoutDiagnosticsRequest
{
    /// <summary>Restrict the scan to this element and its descendants.</summary>
    public string? ElementId { get; set; }

    /// <summary>0-based window index. Defaults to every window.</summary>
    public int? Window { get; set; }

    /// <summary>Element budget, clamped to <see cref="LayoutDiagnosticsFormat.MaxElements"/>.</summary>
    public int? MaxElements { get; set; }
}

/// <summary>A rectangle in the coordinate space named by its owner.</summary>
public sealed class LayoutRect
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;

    [JsonIgnore]
    public bool HasPositiveArea => Width > 0 && Height > 0;
}

public sealed class LayoutSize
{
    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public sealed class LayoutThickness
{
    [JsonPropertyName("left")]
    public double Left { get; set; }

    [JsonPropertyName("top")]
    public double Top { get; set; }

    [JsonPropertyName("right")]
    public double Right { get; set; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; set; }
}

/// <summary>
/// One element's managed layout state, captured during a single UI-thread tree walk.
/// Text and values are deliberately absent — this type carries geometry and identity only.
/// </summary>
public sealed class LayoutElementSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("parentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("automationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutomationId { get; set; }

    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; set; }

    [JsonPropertyName("sourceLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceLine { get; set; }

    [JsonPropertyName("sourceColumn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceColumn { get; set; }

    /// <summary>Arranged rectangle in the parent's coordinate space (MAUI <c>Frame</c>).</summary>
    [JsonPropertyName("frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRect? Frame { get; set; }

    /// <summary>Arranged rectangle in window coordinates, when the platform can resolve it.</summary>
    [JsonPropertyName("windowBounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRect? WindowBounds { get; set; }

    /// <summary>Measured size from the last measure pass, when the platform reports one.</summary>
    [JsonPropertyName("desiredSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutSize? DesiredSize { get; set; }

    [JsonPropertyName("explicitWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExplicitWidth { get; set; }

    [JsonPropertyName("explicitHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExplicitHeight { get; set; }

    [JsonPropertyName("minimumWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinimumWidth { get; set; }

    [JsonPropertyName("minimumHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinimumHeight { get; set; }

    [JsonPropertyName("maximumWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumWidth { get; set; }

    [JsonPropertyName("maximumHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumHeight { get; set; }

    [JsonPropertyName("margin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutThickness? Margin { get; set; }

    [JsonPropertyName("padding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutThickness? Padding { get; set; }

    [JsonPropertyName("zIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ZIndex { get; set; }

    /// <summary>Effective tree visibility, including hidden or transparent ancestors.</summary>
    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1;

    /// <summary>
    /// True when the element has a platform handler, so its arranged geometry reflects a completed
    /// layout pass. Unrealized elements are excluded from every geometry rule.
    /// </summary>
    [JsonPropertyName("isRealized")]
    public bool IsRealized { get; set; }

    /// <summary>True when managed layout state could be read at all (a runtime object was found).</summary>
    [JsonPropertyName("hasLayoutState")]
    public bool HasLayoutState { get; set; }
}

/// <summary>What the scan actually looked at.</summary>
public sealed class LayoutDiagnosticsScope
{
    [JsonPropertyName("rootElementId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootElementId { get; set; }

    [JsonPropertyName("window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Window { get; set; }

    [JsonPropertyName("maxElements")]
    public int MaxElements { get; set; }

    [JsonPropertyName("elementsExamined")]
    public int ElementsExamined { get; set; }

    /// <summary>True when the element budget stopped the walk before the tree was exhausted.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }

    /// <summary>Window rectangle used by window-relative rules, when it is known.</summary>
    [JsonPropertyName("windowBounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRect? WindowBounds { get; set; }
}

public sealed class LayoutRuleCoverage
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("support")]
    public string Support { get; set; } = LayoutRuleSupport.Unavailable;

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = LayoutConfidence.Medium;

    [JsonPropertyName("evaluated")]
    public int Evaluated { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = [];
}

public sealed class LayoutDiagnosticsCoverage
{
    /// <summary><c>full</c>, <c>partial</c>, or <c>unavailable</c> across every rule.</summary>
    [JsonPropertyName("overall")]
    public string Overall { get; set; } = LayoutRuleSupport.Unavailable;

    [JsonPropertyName("rules")]
    public List<LayoutRuleCoverage> Rules { get; set; } = [];

    /// <summary>Everything this report structurally cannot tell you.</summary>
    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = [];

    [JsonPropertyName("neverCaptured")]
    public List<string> NeverCaptured { get; set; } = [];
}

public sealed class LayoutDiagnosticsSummary
{
    [JsonPropertyName("violations")]
    public int Violations { get; set; }

    [JsonPropertyName("observations")]
    public int Observations { get; set; }

    [JsonPropertyName("incomplete")]
    public int Incomplete { get; set; }
}

/// <summary>A reference to the element a finding is about (identity and source only).</summary>
public sealed class LayoutElementReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("automationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutomationId { get; set; }

    [JsonPropertyName("sourceFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFile { get; set; }

    [JsonPropertyName("sourceLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceLine { get; set; }

    [JsonPropertyName("sourceColumn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceColumn { get; set; }
}

/// <summary>The measurements a finding is derived from, so a reader can check the arithmetic.</summary>
public sealed class LayoutFindingEvidence
{
    [JsonPropertyName("frame")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRect? Frame { get; set; }

    [JsonPropertyName("windowBounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRect? WindowBounds { get; set; }

    [JsonPropertyName("parentWindowBounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutRect? ParentWindowBounds { get; set; }

    [JsonPropertyName("desiredSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutSize? DesiredSize { get; set; }

    [JsonPropertyName("explicitWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExplicitWidth { get; set; }

    [JsonPropertyName("explicitHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExplicitHeight { get; set; }

    [JsonPropertyName("constraint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Constraint { get; set; }

    [JsonPropertyName("constraintValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ConstraintValue { get; set; }

    [JsonPropertyName("actualValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ActualValue { get; set; }

    [JsonPropertyName("overflowWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OverflowWidth { get; set; }

    [JsonPropertyName("overflowHeight")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OverflowHeight { get; set; }

    [JsonPropertyName("affectedElements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AffectedElements { get; set; }
}

public sealed class LayoutFinding
{
    /// <summary>Stable within one report; derived from rule + element + axis.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = LayoutOutcomes.Observation;

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = LayoutConfidence.Medium;

    /// <summary>The observed fact, stated without inference.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Why it may or may not be a bug, including the benign explanations.</summary>
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("element")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutElementReference? Element { get; set; }

    [JsonPropertyName("parent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutElementReference? Parent { get; set; }

    [JsonPropertyName("evidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayoutFindingEvidence? Evidence { get; set; }

    [JsonPropertyName("limitations")]
    public List<string> Limitations { get; set; } = [];
}

/// <summary>The complete, self-describing layout diagnostics report.</summary>
public sealed class LayoutDiagnosticsReport
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = LayoutDiagnosticsFormat.SchemaVersion;

    [JsonPropertyName("ruleSetVersion")]
    public string RuleSetVersion { get; set; } = LayoutDiagnosticsFormat.RuleSetVersion;

    [JsonPropertyName("capturedUtc")]
    public string CapturedUtc { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "unknown";

    [JsonPropertyName("scope")]
    public LayoutDiagnosticsScope Scope { get; set; } = new();

    [JsonPropertyName("coverage")]
    public LayoutDiagnosticsCoverage Coverage { get; set; } = new();

    [JsonPropertyName("summary")]
    public LayoutDiagnosticsSummary Summary { get; set; } = new();

    [JsonPropertyName("findings")]
    public List<LayoutFinding> Findings { get; set; } = [];
}
