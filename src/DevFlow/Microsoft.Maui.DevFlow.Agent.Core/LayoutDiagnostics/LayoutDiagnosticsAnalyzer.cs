using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
        DateTime capturedUtc,
        LayoutInspectionRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(scope);
        request ??= new LayoutInspectionRequest
        {
            Rules = [.. LayoutDiagnosticRules.Managed],
            MinimumSeverity = LayoutSeverity.Info,
        };

        var byId = new Dictionary<string, LayoutElementSnapshot>(snapshots.Count, StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
            byId.TryAdd(snapshot.Id, snapshot);

        var enabledRules = ResolveEnabledRules(request);
        var findings = new List<LayoutFinding>();
        var coverage = new Dictionary<string, LayoutRuleCoverage>(StringComparer.Ordinal);
        foreach (var ruleId in enabledRules)
            coverage[ruleId] = new LayoutRuleCoverage { RuleId = ruleId };

        foreach (var snapshot in snapshots)
        {
            if (enabledRules.Contains(LayoutDiagnosticRules.VisibleZeroArea))
                EvaluateVisibleZeroArea(snapshot, coverage, findings);
            if (enabledRules.Contains(LayoutDiagnosticRules.ConstraintViolation))
                EvaluateConstraints(snapshot, coverage, findings);
            if (enabledRules.Contains(LayoutDiagnosticRules.OutsideWindow))
                EvaluateOutsideWindow(snapshot, scope.WindowBounds, coverage, findings);
            if (enabledRules.Contains(LayoutDiagnosticRules.DesiredSizeConstrained))
                EvaluateDesiredSize(snapshot, coverage, findings);
            if (enabledRules.Contains(LayoutDiagnosticRules.ChildOutsideParent))
                EvaluateChildOutsideParent(snapshot, byId, coverage, findings);
        }

        foreach (var ruleId in enabledRules)
            AppendIncompleteFinding(coverage[ruleId], findings);

        var passCounts = coverage.Values.ToDictionary(
            rule => rule.RuleId,
            rule => Math.Max(
                0,
                rule.Evaluated -
                findings.Count(finding =>
                    finding.RuleId == rule.RuleId &&
                    finding.Outcome is LayoutOutcomes.Violation or LayoutOutcomes.Observation)),
            StringComparer.Ordinal);
        var passCount = passCounts.Values.Sum();
        if (request.IncludePasses)
        {
            foreach (var (ruleId, count) in passCounts.Where(item => item.Value > 0))
            {
                findings.Add(new LayoutFinding
                {
                    Id = BuildId(ruleId, "scope", "pass"),
                    RuleId = ruleId,
                    Subtype = "aggregate",
                    Outcome = LayoutOutcomes.Pass,
                    Severity = LayoutSeverity.Info,
                    Confidence = coverage[ruleId].Confidence,
                    Actionability = LayoutActionability.Informational,
                    Message = $"{count} evaluated element(s) did not produce a finding for {ruleId}.",
                    Explanation =
                        "This pass count applies only to the elements the rule could evaluate; read coverage and incomplete findings before treating the scope as clean.",
                });
            }
        }
        EnrichAndFilterFindings(findings, request);
        findings.Sort(CompareFindings);

        var capturedAt = capturedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var normalizedPlatform = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform;
        var report = new LayoutDiagnosticsReport
        {
            CapturedUtc = capturedAt,
            Platform = normalizedPlatform,
            Scope = scope,
            Findings = findings,
            Snapshot = new LayoutSnapshotInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                CapturedAt = capturedAt,
                Platform = normalizedPlatform,
                TreeRevision = ComputeTreeRevision(snapshots),
                Stable = false,
                StabilityReason = "single-frame-managed-snapshot",
                NodeCount = snapshots.Count,
                Windows = scope.WindowBounds is null
                    ? []
                    :
                    [
                        new LayoutWindowInfo
                        {
                            Id = scope.Window is { } window ? $"window-{window}" : "window-0",
                            Bounds = scope.WindowBounds,
                        }
                    ],
            },
        };

        foreach (var ruleId in enabledRules)
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
            if (finding.Suppressed)
            {
                report.Summary.Suppressed++;
                continue;
            }

            switch (finding.Outcome)
            {
                case LayoutOutcomes.Violation: report.Summary.Violations++; break;
                case LayoutOutcomes.Incomplete: report.Summary.Incomplete++; break;
                case LayoutOutcomes.Pass: break;
                case LayoutOutcomes.NotApplicable: report.Summary.NotApplicable++; break;
                default: report.Summary.Observations++; break;
            }
        }

        report.Summary.Passes += passCount;
        report.Summary.NotApplicable += report.Coverage.Rules.Count(rule =>
            rule.Evaluated == 0 && rule.Skipped == 0);
        report.Snapshot.DiagnosticsRevision = ComputeDiagnosticsRevision(report);

        return report;
    }

    public static LayoutRuleCatalog CreateRuleCatalog()
    {
        var catalog = new LayoutRuleCatalog();
        foreach (var ruleId in LayoutDiagnosticRules.All)
        {
            var rule = new LayoutRuleCoverage
            {
                RuleId = ruleId,
                Support = LayoutDiagnosticRules.Managed.Contains(ruleId)
                    ? LayoutRuleSupport.Partial
                    : LayoutRuleSupport.Unavailable,
                Confidence = ConfidenceFor(ruleId),
            };
            AddRuleLimitations(rule);
            if (!LayoutDiagnosticRules.Managed.Contains(ruleId))
            {
                rule.Limitations.Add(
                    "This rule requires native or WebView evidence that is not available from the managed baseline collector.");
            }
            catalog.Rules.Add(rule);
        }
        return catalog;
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

    // ── rich contract projection ─────────────────────────────────────────────────────────────

    private static HashSet<string> ResolveEnabledRules(LayoutInspectionRequest request)
    {
        var requested = request.Rules is { Count: > 0 }
            ? request.Rules
            : LayoutDiagnosticRules.Managed;
        return LayoutDiagnosticRules.All
            .Where(rule => requested.Contains(rule, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void EnrichAndFilterFindings(
        List<LayoutFinding> findings,
        LayoutInspectionRequest request)
    {
        foreach (var finding in findings)
        {
            ApplyFindingMetadata(finding);
            finding.SuppressionKey = BuildSuppressionKey(finding);
            var suppression = finding.Outcome is LayoutOutcomes.Pass or LayoutOutcomes.NotApplicable
                ? null
                : request.Suppressions.FirstOrDefault(candidate =>
                    LayoutSuppressionMatches(candidate, finding));
            if (suppression is not null)
            {
                finding.Suppressed = true;
                finding.SuppressionReason = suppression.Reason;
            }

            if (finding.Evidence is { } evidence)
            {
                evidence.FullRegion ??= RegionFrom(
                    evidence.WindowBounds ?? evidence.Frame,
                    "managedBounds");
                evidence.VisibleRegion ??= evidence.FullRegion;
                if (evidence.Limitations.Count == 0 && finding.Limitations.Count > 0)
                    evidence.Limitations.AddRange(finding.Limitations);
            }
        }

        var minimumSeverity = SeverityRank(request.MinimumSeverity);
        findings.RemoveAll(finding =>
            finding.Outcome is LayoutOutcomes.Violation or LayoutOutcomes.Observation &&
            SeverityRank(finding.Severity) < minimumSeverity);

        if (!request.IncludeEvidence)
        {
            foreach (var finding in findings)
                finding.Evidence = null;
        }
    }

    private static void ApplyFindingMetadata(LayoutFinding finding)
    {
        if (finding.Outcome is LayoutOutcomes.Pass or LayoutOutcomes.NotApplicable)
        {
            finding.Severity = LayoutSeverity.Info;
            finding.Actionability = LayoutActionability.Informational;
            return;
        }

        switch (finding.RuleId)
        {
            case LayoutDiagnosticRules.VisibleZeroArea:
                finding.Subtype ??= "arranged-area";
                finding.Severity = finding.Element?.Interactive == true
                    ? LayoutSeverity.Serious
                    : LayoutSeverity.Moderate;
                finding.Actionability = LayoutActionability.Fix;
                finding.FixCategories = ["increase-host-space", "adjust-layout-constraints"];
                break;
            case LayoutDiagnosticRules.ConstraintViolation:
                finding.Subtype ??= finding.Evidence?.Constraint;
                finding.Severity = LayoutSeverity.Serious;
                finding.Actionability = LayoutActionability.Fix;
                finding.FixCategories = ["adjust-layout-constraints"];
                break;
            case LayoutDiagnosticRules.OutsideWindow:
                finding.Subtype ??= "window-edge";
                finding.Severity = LayoutSeverity.Moderate;
                finding.Actionability = LayoutActionability.Review;
                finding.FixCategories = ["adjust-layout-constraints", "enable-scroll"];
                break;
            case LayoutDiagnosticRules.DesiredSizeConstrained:
                finding.Subtype ??= "measure-arrange-pressure";
                finding.Severity = LayoutSeverity.Minor;
                finding.Actionability = LayoutActionability.Review;
                finding.FixCategories = ["increase-host-space", "adjust-layout-constraints"];
                break;
            case LayoutDiagnosticRules.ChildOutsideParent:
                finding.Subtype ??= "parent-bounds";
                finding.Severity = LayoutSeverity.Minor;
                finding.Actionability = LayoutActionability.Review;
                finding.FixCategories = ["review-overflow", "enable-scroll"];
                if (finding.Parent is not null && finding.RelatedElements.Count == 0)
                {
                    finding.RelatedElements.Add(new LayoutRelatedElement
                    {
                        Relation = "parent",
                        Element = finding.Parent,
                    });
                }
                break;
        }

        if (finding.Outcome == LayoutOutcomes.Incomplete)
        {
            finding.Severity = LayoutSeverity.Info;
            finding.Actionability = LayoutActionability.Informational;
        }
    }

    private static LayoutRegionInfo? RegionFrom(LayoutRect? rect, string precision)
    {
        if (rect is null)
            return null;
        var width = Math.Max(0, rect.Width);
        var height = Math.Max(0, rect.Height);
        return new LayoutRegionInfo
        {
            Bounds = rect,
            Area = width * height,
            Precision = precision,
            Points = width <= 0 || height <= 0
                ? []
                :
                [
                    new LayoutPointInfo { X = rect.X, Y = rect.Y },
                    new LayoutPointInfo { X = rect.Right, Y = rect.Y },
                    new LayoutPointInfo { X = rect.Right, Y = rect.Bottom },
                    new LayoutPointInfo { X = rect.X, Y = rect.Bottom },
                ],
        };
    }

    private static bool LayoutSuppressionMatches(
        LayoutSuppression suppression,
        LayoutFinding finding)
    {
        var element = finding.Element;
        if (suppression.RuleId is not null &&
            !suppression.RuleId.Equals(finding.RuleId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (suppression.Fingerprint is not null &&
            !suppression.Fingerprint.Equals(finding.SuppressionKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (suppression.ElementId is not null &&
            !suppression.ElementId.Equals(element?.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        if (suppression.AutomationId is not null &&
            !suppression.AutomationId.Equals(element?.AutomationId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (suppression.ElementType is not null &&
            !suppression.ElementType.Equals(element?.Type, StringComparison.OrdinalIgnoreCase))
            return false;
        if (suppression.SourceFile is not null &&
            !SourcePathMatches(suppression.SourceFile, element?.SourceFile))
            return false;
        if (suppression.SourceLineStart is { } start)
        {
            var line = element?.SourceLine;
            var end = suppression.SourceLineEnd ?? start;
            if (line is null || line < start || line > end)
                return false;
        }
        if (suppression.RelatedElementId is not null &&
            !finding.RelatedElements.Any(related =>
                suppression.RelatedElementId.Equals(
                    related.Element.Id,
                    StringComparison.OrdinalIgnoreCase)))
            return false;
        if (suppression.RelatedAutomationId is not null &&
            !finding.RelatedElements.Any(related =>
                suppression.RelatedAutomationId.Equals(
                    related.Element.AutomationId,
                    StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    private static bool SourcePathMatches(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
            return false;
        var normalizedExpected = expected.Replace('\\', '/').Trim();
        var normalizedActual = actual.Replace('\\', '/').Trim();
        return normalizedActual.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase) ||
            normalizedActual.EndsWith('/' + normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSuppressionKey(LayoutFinding finding)
    {
        var element = finding.Element;
        var related = string.Join(
            ",",
            finding.RelatedElements
                .OrderBy(item => item.Relation, StringComparer.Ordinal)
                .ThenBy(item => item.Element.Id, StringComparer.Ordinal)
                .Select(item => $"{item.Relation}:{item.Element.AutomationId ?? item.Element.Id}"));
        var identity = string.Join(
            "|",
            finding.RuleId,
            finding.Subtype,
            element?.SourceFile?.Replace('\\', '/'),
            element?.SourceLine,
            element?.AutomationId,
            element?.Type,
            related);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24]
            .ToLowerInvariant();
    }

    private static string ComputeTreeRevision(IReadOnlyList<LayoutElementSnapshot> snapshots)
    {
        var canonical = string.Join(
            "\n",
            snapshots.Select(snapshot => string.Join(
                "|",
                snapshot.Id,
                snapshot.ParentId,
                snapshot.Type,
                FormatRect(snapshot.WindowBounds ?? snapshot.Frame),
                snapshot.IsVisible,
                snapshot.IsRealized)));
        return HashRevision(canonical);
    }

    private static string ComputeDiagnosticsRevision(LayoutDiagnosticsReport report)
    {
        var canonical = string.Join(
            "\n",
            report.Findings.Select(finding => string.Join(
                "|",
                finding.Id,
                finding.Outcome,
                finding.Severity,
                finding.Confidence,
                finding.Suppressed,
                finding.SuppressionKey)));
        return HashRevision(canonical);
    }

    private static string FormatRect(LayoutRect? rect)
        => rect is null
            ? ""
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{rect.X:0.###},{rect.Y:0.###},{rect.Width:0.###},{rect.Height:0.###}");

    private static string HashRevision(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24]
            .ToLowerInvariant();

    private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        LayoutSeverity.Critical => 4,
        LayoutSeverity.Serious => 3,
        LayoutSeverity.Moderate => 2,
        LayoutSeverity.Minor => 1,
        _ => 0,
    };

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
            case LayoutDiagnosticRules.ElementClipped:
                rule.Limitations.Add("Requires native clip-chain and transformed-region evidence.");
                break;
            case LayoutDiagnosticRules.ContentOverflow:
                rule.Limitations.Add("Requires authoritative content and viewport regions from the native control or WebView.");
                break;
            case LayoutDiagnosticRules.TextNotFullyRendered:
                rule.Limitations.Add("Requires native text layout metrics; managed desired size alone cannot prove truncation.");
                break;
            case LayoutDiagnosticRules.InteractionOccluded:
                rule.Limitations.Add("Requires native hit-test sampling against the rendered surface.");
                break;
            case LayoutDiagnosticRules.VisualOccluded:
                rule.Limitations.Add("Requires native paint-order, opacity, and overlap evidence.");
                break;
            case LayoutDiagnosticRules.GeometricOverlap:
                rule.Limitations.Add("Requires transformed window regions for both elements.");
                break;
            case LayoutDiagnosticRules.AccessibilityVisibilityMismatch:
                rule.Limitations.Add("Requires platform accessibility or automation visibility.");
                break;
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
        LayoutDiagnosticRules.ElementClipped => LayoutConfidence.Medium,
        LayoutDiagnosticRules.ContentOverflow => LayoutConfidence.Medium,
        LayoutDiagnosticRules.TextNotFullyRendered => LayoutConfidence.Medium,
        LayoutDiagnosticRules.InteractionOccluded => LayoutConfidence.Medium,
        LayoutDiagnosticRules.VisualOccluded => LayoutConfidence.Medium,
        LayoutDiagnosticRules.GeometricOverlap => LayoutConfidence.Medium,
        LayoutDiagnosticRules.AccessibilityVisibilityMismatch => LayoutConfidence.Medium,
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
        ParentId = snapshot.ParentId,
        Type = snapshot.Type,
        AutomationId = snapshot.AutomationId,
        Role = snapshot.Role,
        Interactive = snapshot.Interactive,
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
