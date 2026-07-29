using System.Globalization;

namespace Microsoft.Maui.DevFlow.Agent.Core.LayoutDiagnostics;

/// <summary>
/// Turns a captured set of <see cref="LayoutElementSnapshot"/> into a
/// <see cref="LayoutDiagnosticsReport"/>.
///
/// The analyzer is pure and deterministic: same snapshots in, byte-identical findings out
/// (aside from the capture timestamp). It performs no I/O, touches no MAUI type, and therefore
/// runs unchanged inside unit tests.
///
/// Every rule is written to be defensible from managed layout state alone. The analyzer never
/// claims clipping, occlusion, text truncation, or an accessibility mismatch, because none of
/// those can be proven without authoritative platform data that this subsystem does not read.
/// </summary>
public static class LayoutDiagnosticsAnalyzer
{
    private const string OverflowIsNotClippingLimitation =
        "Overflow is not clipping: this report cannot prove whether the parent clips, scrolls, or lets content draw outside its bounds.";

    private const string NoPlatformGeometryLimitation =
        "Findings are derived from managed MAUI layout state only. Platform-side clipping, occlusion, transforms, and text truncation are not observed.";

    private const string NoRenderingLimitation =
        "No rendering, screenshot, or hit-test evidence is used, so a finding cannot confirm what a user actually sees.";

    private const string SnapshotIsSingleFrameLimitation =
        "The report is a single snapshot taken on the UI thread. An element mid-animation or mid-layout can produce a transient finding.";

    /// <summary>Analyzes a captured scope. <paramref name="snapshots"/> must be in tree pre-order.</summary>
    public static LayoutDiagnosticsReport Analyze(
        IReadOnlyList<LayoutElementSnapshot> snapshots,
        LayoutDiagnosticsScope scope,
        string platform,
        DateTime capturedUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(scope);

        var byId = new Dictionary<string, LayoutElementSnapshot>(snapshots.Count, StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
            byId.TryAdd(snapshot.Id, snapshot);

        var findings = new List<LayoutFinding>();
        var coverage = new Dictionary<string, LayoutRuleCoverage>(StringComparer.Ordinal);
        foreach (var ruleId in LayoutDiagnosticRules.All)
            coverage[ruleId] = new LayoutRuleCoverage { RuleId = ruleId };

        foreach (var snapshot in snapshots)
        {
            EvaluateVisibleZeroArea(snapshot, coverage, findings);
            EvaluateConstraints(snapshot, coverage, findings);
            EvaluateOutsideWindow(snapshot, scope.WindowBounds, coverage, findings);
            EvaluateDesiredSize(snapshot, coverage, findings);
            EvaluateChildOutsideParent(snapshot, byId, coverage, findings);
        }

        foreach (var ruleId in LayoutDiagnosticRules.All)
            AppendIncompleteFinding(coverage[ruleId], findings);

        findings.Sort(CompareFindings);

        var report = new LayoutDiagnosticsReport
        {
            CapturedUtc = capturedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Platform = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform,
            Scope = scope,
            Findings = findings,
        };

        foreach (var ruleId in LayoutDiagnosticRules.All)
        {
            var rule = coverage[ruleId];
            rule.Support = rule.Evaluated == 0
                ? LayoutRuleSupport.Unavailable
                : rule.Skipped == 0 ? LayoutRuleSupport.Full : LayoutRuleSupport.Partial;
            rule.Confidence = ConfidenceFor(ruleId);
            AddRuleLimitations(rule);
            report.Coverage.Rules.Add(rule);
        }

        report.Coverage.Overall = ResolveOverallSupport(report.Coverage.Rules);
        report.Coverage.Limitations.Add(NoPlatformGeometryLimitation);
        report.Coverage.Limitations.Add(NoRenderingLimitation);
        report.Coverage.Limitations.Add(SnapshotIsSingleFrameLimitation);
        if (scope.WindowBounds is null)
        {
            report.Coverage.Limitations.Add(
                "Window bounds were unavailable, so window-relative rules could not be evaluated.");
        }
        if (scope.Truncated)
        {
            report.Coverage.Limitations.Add(
                $"The element budget of {scope.MaxElements} stopped the walk, so part of the tree was not examined.");
        }
        report.Coverage.NeverCaptured = [.. LayoutDiagnosticsFormat.NeverCaptured];

        foreach (var finding in findings)
        {
            switch (finding.Outcome)
            {
                case LayoutOutcomes.Violation: report.Summary.Violations++; break;
                case LayoutOutcomes.Incomplete: report.Summary.Incomplete++; break;
                default: report.Summary.Observations++; break;
            }
        }

        return report;
    }

    // ── rules ────────────────────────────────────────────────────────────────────────────────

    private static void EvaluateVisibleZeroArea(
        LayoutElementSnapshot snapshot,
        Dictionary<string, LayoutRuleCoverage> coverage,
        List<LayoutFinding> findings)
    {
        var rule = coverage[LayoutDiagnosticRules.VisibleZeroArea];
        if (!snapshot.IsVisible)
            return;
        if (!snapshot.IsRealized)
        {
            rule.Skipped++;
            return;
        }

        var rect = snapshot.Frame;
        if (rect is null)
        {
            rule.Skipped++;
            return;
        }

        rule.Evaluated++;
        if (rect.Width > 0 && rect.Height > 0)
            return;

        var axis = rect.Width <= 0 && rect.Height <= 0
            ? "width and height"
            : rect.Width <= 0 ? "width" : "height";

        findings.Add(new LayoutFinding
        {
            Id = BuildId(LayoutDiagnosticRules.VisibleZeroArea, snapshot.Id, "area"),
            RuleId = LayoutDiagnosticRules.VisibleZeroArea,
            Outcome = LayoutOutcomes.Violation,
            Confidence = LayoutConfidence.High,
            Message =
                $"{snapshot.Type} is visible and realized but was arranged with a non-positive {axis} " +
                $"({Format(rect.Width)}×{Format(rect.Height)}), so it cannot draw anything.",
            Explanation =
                "A realized element whose arranged rectangle has no area occupies no space on screen. " +
                "This is usually an unsatisfied layout constraint (a zero-size parent, a star row/column " +
                "that collapsed, or a missing size request), but it also matches an element that is " +
                "deliberately collapsed while remaining IsVisible.",
            Element = Reference(snapshot),
            Evidence = new LayoutFindingEvidence
            {
                Frame = rect,
                WindowBounds = snapshot.WindowBounds,
                DesiredSize = snapshot.DesiredSize,
                ExplicitWidth = snapshot.ExplicitWidth,
                ExplicitHeight = snapshot.ExplicitHeight,
            },
            Limitations =
            [
                "An element that is intentionally collapsed to zero size while still IsVisible matches this rule.",
            ],
        });
    }

    private static void EvaluateConstraints(
        LayoutElementSnapshot snapshot,
        Dictionary<string, LayoutRuleCoverage> coverage,
        List<LayoutFinding> findings)
    {
        var rule = coverage[LayoutDiagnosticRules.ConstraintViolation];
        var declared =
            snapshot.MinimumWidth is not null || snapshot.MinimumHeight is not null ||
            snapshot.MaximumWidth is not null || snapshot.MaximumHeight is not null;
        if (!declared)
            return;
        if (!snapshot.IsRealized)
        {
            rule.Skipped++;
            return;
        }

        var rect = snapshot.Frame;
        if (rect is null || !IsUsable(rect.Width) || !IsUsable(rect.Height))
        {
            rule.Skipped++;
            return;
        }

        rule.Evaluated++;

        AddConstraintFinding(snapshot, findings, "minimumWidth", snapshot.MinimumWidth, rect.Width, isMinimum: true, "width");
        AddConstraintFinding(snapshot, findings, "minimumHeight", snapshot.MinimumHeight, rect.Height, isMinimum: true, "height");
        AddConstraintFinding(snapshot, findings, "maximumWidth", snapshot.MaximumWidth, rect.Width, isMinimum: false, "width");
        AddConstraintFinding(snapshot, findings, "maximumHeight", snapshot.MaximumHeight, rect.Height, isMinimum: false, "height");
    }

    private static void AddConstraintFinding(
        LayoutElementSnapshot snapshot,
        List<LayoutFinding> findings,
        string constraint,
        double? constraintValue,
        double actual,
        bool isMinimum,
        string axis)
    {
        if (constraintValue is not { } limit || !IsUsable(limit))
            return;

        var violated = isMinimum
            ? actual < limit - LayoutDiagnosticsFormat.Tolerance
            : actual > limit + LayoutDiagnosticsFormat.Tolerance;
        if (!violated)
            return;

        var relation = isMinimum ? "below its declared minimum" : "above its declared maximum";
        findings.Add(new LayoutFinding
        {
            Id = BuildId(LayoutDiagnosticRules.ConstraintViolation, snapshot.Id, constraint),
            RuleId = LayoutDiagnosticRules.ConstraintViolation,
            Outcome = LayoutOutcomes.Violation,
            Confidence = LayoutConfidence.High,
            Message =
                $"{snapshot.Type} was arranged with {axis} {Format(actual)}, which is {relation} " +
                $"{constraint} of {Format(limit)}.",
            Explanation =
                "The arranged size contradicts a constraint declared on the element itself. A parent " +
                "layout that hard-constrains its child overrides the request, so the declared minimum " +
                "or maximum cannot be honoured as authored.",
            Element = Reference(snapshot),
            Evidence = new LayoutFindingEvidence
            {
                Frame = snapshot.Frame,
                Constraint = constraint,
                ConstraintValue = limit,
                ActualValue = actual,
                DesiredSize = snapshot.DesiredSize,
            },
            Limitations = [],
        });
    }

    private static void EvaluateOutsideWindow(
        LayoutElementSnapshot snapshot,
        LayoutRect? windowBounds,
        Dictionary<string, LayoutRuleCoverage> coverage,
        List<LayoutFinding> findings)
    {
        var rule = coverage[LayoutDiagnosticRules.OutsideWindow];
        if (!snapshot.IsVisible)
            return;
        if (!snapshot.IsRealized)
        {
            rule.Skipped++;
            return;
        }

        var rect = snapshot.WindowBounds;
        if (windowBounds is null || !windowBounds.HasPositiveArea || rect is null)
        {
            rule.Skipped++;
            return;
        }

        if (!rect.HasPositiveArea)
            return;

        rule.Evaluated++;

        var outside =
            rect.Right <= windowBounds.X + LayoutDiagnosticsFormat.Tolerance ||
            rect.X >= windowBounds.Right - LayoutDiagnosticsFormat.Tolerance ||
            rect.Bottom <= windowBounds.Y + LayoutDiagnosticsFormat.Tolerance ||
            rect.Y >= windowBounds.Bottom - LayoutDiagnosticsFormat.Tolerance;
        if (!outside)
            return;

        findings.Add(new LayoutFinding
        {
            Id = BuildId(LayoutDiagnosticRules.OutsideWindow, snapshot.Id, "window"),
            RuleId = LayoutDiagnosticRules.OutsideWindow,
            Outcome = LayoutOutcomes.Observation,
            Confidence = LayoutConfidence.Medium,
            Message =
                $"{snapshot.Type} is visible with a positive area but its window rectangle " +
                $"({Format(rect.X)}, {Format(rect.Y)}, {Format(rect.Width)}×{Format(rect.Height)}) " +
                $"lies entirely outside the window ({Format(windowBounds.Width)}×{Format(windowBounds.Height)}).",
            Explanation =
                "The element was arranged completely off the window surface, so no part of it can be " +
                "on screen in this window. Content parked off-screen on purpose (an off-canvas drawer, " +
                "a virtualized row recycled outside the viewport) produces the same geometry.",
            Element = Reference(snapshot),
            Evidence = new LayoutFindingEvidence
            {
                WindowBounds = rect,
                Frame = snapshot.Frame,
            },
            Limitations =
            [
                "Scrolled-away and off-canvas content is arranged outside the window by design and matches this rule.",
            ],
        });
    }

    private static void EvaluateDesiredSize(
        LayoutElementSnapshot snapshot,
        Dictionary<string, LayoutRuleCoverage> coverage,
        List<LayoutFinding> findings)
    {
        var rule = coverage[LayoutDiagnosticRules.DesiredSizeConstrained];
        if (!snapshot.IsVisible)
            return;
        if (!snapshot.IsRealized)
        {
            rule.Skipped++;
            return;
        }

        var rect = snapshot.Frame;
        var desired = snapshot.DesiredSize;
        if (rect is null || desired is null || !IsUsable(desired.Width) || !IsUsable(desired.Height))
        {
            rule.Skipped++;
            return;
        }

        rule.Evaluated++;

        var overflowWidth = desired.Width - rect.Width;
        var overflowHeight = desired.Height - rect.Height;
        var widthMaterial = IsMaterialOverflow(overflowWidth, desired.Width);
        var heightMaterial = IsMaterialOverflow(overflowHeight, desired.Height);
        if (!widthMaterial && !heightMaterial)
            return;

        var axes = widthMaterial && heightMaterial ? "width and height"
            : widthMaterial ? "width" : "height";

        findings.Add(new LayoutFinding
        {
            Id = BuildId(LayoutDiagnosticRules.DesiredSizeConstrained, snapshot.Id, "desired"),
            RuleId = LayoutDiagnosticRules.DesiredSizeConstrained,
            Outcome = LayoutOutcomes.Observation,
            Confidence = LayoutConfidence.Medium,
            Message =
                $"{snapshot.Type} measured {Format(desired.Width)}×{Format(desired.Height)} but was " +
                $"arranged {Format(rect.Width)}×{Format(rect.Height)}, so its {axes} is smaller than it asked for.",
            Explanation =
                "This is normal whenever a parent intentionally constrains a child — a fixed grid row, " +
                "a star column under pressure, or a scroll viewport all produce it. It is only worth " +
                "investigating when the element is expected to show all of its content at this size.",
            Element = Reference(snapshot),
            Evidence = new LayoutFindingEvidence
            {
                Frame = rect,
                DesiredSize = desired,
                ExplicitWidth = snapshot.ExplicitWidth,
                ExplicitHeight = snapshot.ExplicitHeight,
                OverflowWidth = widthMaterial ? Round(overflowWidth) : null,
                OverflowHeight = heightMaterial ? Round(overflowHeight) : null,
            },
            Limitations =
            [
                "A constrained measure does not imply that content is visually cut off; that requires platform rendering data this report does not read.",
            ],
        });
    }

    private static void EvaluateChildOutsideParent(
        LayoutElementSnapshot snapshot,
        Dictionary<string, LayoutElementSnapshot> byId,
        Dictionary<string, LayoutRuleCoverage> coverage,
        List<LayoutFinding> findings)
    {
        var rule = coverage[LayoutDiagnosticRules.ChildOutsideParent];
        if (!snapshot.IsVisible || snapshot.ParentId is null)
            return;
        if (!snapshot.IsRealized)
        {
            rule.Skipped++;
            return;
        }

        if (!byId.TryGetValue(snapshot.ParentId, out var parent))
        {
            rule.Skipped++;
            return;
        }
        if (!parent.IsRealized)
        {
            rule.Skipped++;
            return;
        }

        var child = snapshot.WindowBounds;
        var container = parent.WindowBounds;
        if (child is null || container is null || !child.HasPositiveArea || !container.HasPositiveArea)
        {
            rule.Skipped++;
            return;
        }

        rule.Evaluated++;

        var overflowLeft = container.X - child.X;
        var overflowTop = container.Y - child.Y;
        var overflowRight = child.Right - container.Right;
        var overflowBottom = child.Bottom - container.Bottom;
        var horizontal = Math.Max(Math.Max(overflowLeft, overflowRight), 0);
        var vertical = Math.Max(Math.Max(overflowTop, overflowBottom), 0);
        if (horizontal <= LayoutDiagnosticsFormat.Tolerance && vertical <= LayoutDiagnosticsFormat.Tolerance)
            return;

        findings.Add(new LayoutFinding
        {
            Id = BuildId(LayoutDiagnosticRules.ChildOutsideParent, snapshot.Id, "parent"),
            RuleId = LayoutDiagnosticRules.ChildOutsideParent,
            Outcome = LayoutOutcomes.Observation,
            Confidence = LayoutConfidence.Low,
            Message =
                $"{snapshot.Type} extends past its {parent.Type} parent by " +
                $"{Format(horizontal)} horizontally and {Format(vertical)} vertically.",
            Explanation =
                "Parents routinely allow children to draw outside their own rectangle — shadows, " +
                "overlays, absolute positioning, and scroll content all do it. Treat this as a pointer " +
                "to look at the layout, not as a defect.",
            Element = Reference(snapshot),
            Parent = Reference(parent),
            Evidence = new LayoutFindingEvidence
            {
                WindowBounds = child,
                ParentWindowBounds = container,
                OverflowWidth = horizontal > LayoutDiagnosticsFormat.Tolerance ? Round(horizontal) : null,
                OverflowHeight = vertical > LayoutDiagnosticsFormat.Tolerance ? Round(vertical) : null,
            },
            Limitations = [OverflowIsNotClippingLimitation],
        });
    }

    // ── coverage / incomplete reporting ──────────────────────────────────────────────────────

    private static void AppendIncompleteFinding(LayoutRuleCoverage rule, List<LayoutFinding> findings)
    {
        if (rule.Skipped <= 0)
            return;

        findings.Add(new LayoutFinding
        {
            Id = BuildId(rule.RuleId, "scope", "incomplete"),
            RuleId = rule.RuleId,
            Outcome = LayoutOutcomes.Incomplete,
            Confidence = LayoutConfidence.High,
            Message =
                $"{rule.RuleId} could not be evaluated for {rule.Skipped} element(s) because the " +
                "geometry it needs was unavailable.",
            Explanation =
                "Those elements are neither passing nor failing this rule. Managed layout state did not " +
                "expose the measurements the rule requires on this platform or at this point in the " +
                "layout cycle.",
            Limitations =
            [
                "An unevaluated element is reported as incomplete and must never be read as a pass.",
            ],
            Evidence = new LayoutFindingEvidence { AffectedElements = rule.Skipped },
        });
    }

    private static void AddRuleLimitations(LayoutRuleCoverage rule)
    {
        switch (rule.RuleId)
        {
            case LayoutDiagnosticRules.VisibleZeroArea:
                rule.Limitations.Add("Only realized elements are evaluated; an element without a handler is skipped, not passed.");
                break;
            case LayoutDiagnosticRules.ConstraintViolation:
                rule.Limitations.Add("Only minimum/maximum requests declared on the element itself are checked; parent-imposed constraints are not visible here.");
                break;
            case LayoutDiagnosticRules.OutsideWindow:
                rule.Limitations.Add("Requires platform-resolved window bounds for both the window and the element.");
                rule.Limitations.Add("Cannot distinguish an off-screen bug from deliberately off-canvas or recycled content.");
                rule.Limitations.Add("Reported as a medium-confidence observation only, never as a violation.");
                break;
            case LayoutDiagnosticRules.DesiredSizeConstrained:
                rule.Limitations.Add("Reported as an observation only: a parent constraining a child is normal MAUI layout behaviour.");
                break;
            case LayoutDiagnosticRules.ChildOutsideParent:
                rule.Limitations.Add(OverflowIsNotClippingLimitation);
                rule.Limitations.Add("Reported as a low-confidence observation only, never as a violation.");
                break;
        }
    }

    private static string ConfidenceFor(string ruleId) => ruleId switch
    {
        LayoutDiagnosticRules.VisibleZeroArea => LayoutConfidence.High,
        LayoutDiagnosticRules.ConstraintViolation => LayoutConfidence.High,
        LayoutDiagnosticRules.OutsideWindow => LayoutConfidence.Medium,
        LayoutDiagnosticRules.DesiredSizeConstrained => LayoutConfidence.Medium,
        _ => LayoutConfidence.Low,
    };

    private static string ResolveOverallSupport(IReadOnlyList<LayoutRuleCoverage> rules)
    {
        if (rules.Count == 0 || rules.All(rule => rule.Support == LayoutRuleSupport.Unavailable))
            return LayoutRuleSupport.Unavailable;
        return rules.All(rule => rule.Support == LayoutRuleSupport.Full)
            ? LayoutRuleSupport.Full
            : LayoutRuleSupport.Partial;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static int CompareFindings(LayoutFinding left, LayoutFinding right)
    {
        var byOutcome = OutcomeOrder(left.Outcome).CompareTo(OutcomeOrder(right.Outcome));
        if (byOutcome != 0) return byOutcome;

        var byRule = LayoutDiagnosticRules.OrderOf(left.RuleId).CompareTo(LayoutDiagnosticRules.OrderOf(right.RuleId));
        if (byRule != 0) return byRule;

        return string.CompareOrdinal(left.Id, right.Id);
    }

    private static int OutcomeOrder(string outcome) => outcome switch
    {
        LayoutOutcomes.Violation => 0,
        LayoutOutcomes.Observation => 1,
        _ => 2,
    };

    private static LayoutElementReference Reference(LayoutElementSnapshot snapshot) => new()
    {
        Id = snapshot.Id,
        Type = snapshot.Type,
        AutomationId = snapshot.AutomationId,
        SourceFile = snapshot.SourceFile,
        SourceLine = snapshot.SourceLine,
        SourceColumn = snapshot.SourceColumn,
    };

    private static bool IsMaterialOverflow(double overflow, double desired)
    {
        if (overflow <= LayoutDiagnosticsFormat.Tolerance)
            return false;
        var relative = Math.Abs(desired) * LayoutDiagnosticsFormat.RelativeTolerance;
        return overflow > relative;
    }

    private static bool IsUsable(double value) => double.IsFinite(value) && value >= 0;

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Format(double value)
        => Round(value).ToString("0.##", CultureInfo.InvariantCulture);

    private static string BuildId(string ruleId, string elementId, string discriminator)
        => $"{ruleId}:{elementId}:{discriminator}";
}
