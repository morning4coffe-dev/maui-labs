using Microsoft.Maui.DevFlow.Agent.Core.LayoutDiagnostics;

namespace Microsoft.Maui.DevFlow.Agent.Core;

/// <summary>
/// On-demand layout diagnostics: <c>GET|POST /api/v1/ui/diagnostics/layout</c>.
///
/// This is an explicit, one-shot, read-only scan. There is no watcher, no delta stream, and no
/// background sampling, so the observer effect is bounded to the single UI-thread tree walk the
/// caller asked for. The walk also populates the runtime element map, so the whole report comes
/// from one pass instead of a per-element <c>GetElementById</c> re-walk.
/// </summary>
public partial class DevFlowAgentService
{
    private readonly SemaphoreSlim _layoutDiagnosticsGate = new(1, 1);

    private Task<HttpResponse> HandleLayoutDiagnosticsGet(HttpRequest request)
    {
        var layoutRequest = new LayoutInspectionRequest
        {
            ElementId = request.QueryParams.TryGetValue("elementId", out var elementId) && !string.IsNullOrWhiteSpace(elementId)
                ? elementId
                : null,
            Window = ParseWindowIndex(request),
            MaxElements = request.QueryParams.TryGetValue("maxElements", out var max) && int.TryParse(max, out var parsed)
                ? parsed
                : null,
            Rules = [.. LayoutDiagnosticRules.Managed],
            MinimumSeverity = LayoutSeverity.Info,
        };
        return RunLayoutDiagnosticsAsync(layoutRequest);
    }

    private Task<HttpResponse> HandleLayoutDiagnosticsPost(HttpRequest request)
    {
        var layoutRequest = request.BodyAs<LayoutInspectionRequest>() ?? new LayoutInspectionRequest();
        layoutRequest.Scope ??= new LayoutInspectionScope();
        layoutRequest.Stability ??= new LayoutStabilityOptions();
        layoutRequest.Occlusion ??= new LayoutOcclusionOptions();
        layoutRequest.Privacy ??= new LayoutPrivacyOptions();
        layoutRequest.Suppressions ??= [];
        layoutRequest.Window ??= ParseWindowIndex(request);
        return RunLayoutDiagnosticsAsync(layoutRequest);
    }

    private Task<HttpResponse> HandleLayoutDiagnosticRules(HttpRequest request)
        => Task.FromResult(HttpResponse.Json(LayoutDiagnosticsAnalyzer.CreateRuleCatalog()));

    private async Task<HttpResponse> RunLayoutDiagnosticsAsync(LayoutInspectionRequest request)
    {
        if (_app == null)
            return HttpResponse.Error("Agent not bound to app");

        var validation = ValidateLayoutRequest(request);
        if (validation is not null)
        {
            return HttpResponse.Error(
                validation,
                statusCode: 400,
                reason: "layout-diagnostics-validation");
        }

        var maxElements = Math.Clamp(
            request.MaxElements ?? LayoutDiagnosticsFormat.DefaultMaxElements,
            1,
            LayoutDiagnosticsFormat.MaxElements);
        var requestedRoot = request.Scope.RootElementId ?? request.ElementId;
        var rootElementId = string.IsNullOrWhiteSpace(requestedRoot) ? null : requestedRoot.Trim();
        var window = request.Scope.Window ?? request.Window;

        // Serialize scans so two concurrent callers cannot interleave walker state.
        await _layoutDiagnosticsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var report = await DispatchAsync(() =>
                CaptureLayoutDiagnostics(request, rootElementId, window, maxElements));
            return report is null
                ? HttpResponse.NotFound($"Element '{rootElementId}' not found")
                : HttpResponse.Json(report);
        }
        finally
        {
            _layoutDiagnosticsGate.Release();
        }
    }

    /// <summary>Runs entirely on the UI thread: one walk, one projection, one analysis.</summary>
    private LayoutDiagnosticsReport? CaptureLayoutDiagnostics(
        LayoutInspectionRequest request,
        string? rootElementId,
        int? window,
        int maxElements)
    {
        var app = _app;
        if (app == null)
            return null;

        _treeWalker.CaptureWalkElements = true;
        try
        {
            var maxDepth = request.Scope.IncludeDescendants
                ? request.Scope.MaxDepth
                : 1;
            var roots = rootElementId is null
                ? _treeWalker.WalkTree(
                    app,
                    maxDepth,
                    windowIndex: window,
                    maxElements: maxElements)
                : _treeWalker.WalkSubtree(
                    app,
                    rootElementId,
                    maxDepth,
                    maxElements: maxElements,
                    windowIndex: window);
            var collected = LayoutSnapshotCollector.Collect(
                roots,
                _treeWalker.WalkElements,
                rootElementId,
                maxElements);

            if (!collected.RootFound)
                return null;

            var scope = new LayoutDiagnosticsScope
            {
                RootElementId = rootElementId,
                Window = window,
                MaxElements = maxElements,
                ElementsExamined = collected.Snapshots.Count,
                Truncated = collected.Truncated || _treeWalker.WalkWasTruncated,
                WindowBounds = ResolveWindowRect(window),
            };

            return LayoutDiagnosticsAnalyzer.Analyze(
                collected.Snapshots,
                scope,
                PlatformName,
                DateTime.UtcNow,
                request);
        }
        finally
        {
            // Never retain strong references to live elements past the scan.
            _treeWalker.CaptureWalkElements = false;
            _treeWalker.ClearWalkElements();
        }
    }

    private static string? ValidateLayoutRequest(LayoutInspectionRequest request)
    {
        if (!string.Equals(
                request.SchemaVersion,
                LayoutDiagnosticsFormat.SchemaVersion,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.SchemaVersion, "1.0", StringComparison.OrdinalIgnoreCase))
        {
            return $"schemaVersion must be '{LayoutDiagnosticsFormat.SchemaVersion}'.";
        }

        if (!new[] { "agent", "strict", "exhaustive", "ci" }
            .Contains(request.Profile, StringComparer.OrdinalIgnoreCase))
            return "profile must be agent, strict, exhaustive, or ci.";
        if (!LayoutSeverity.All.Contains(request.MinimumSeverity, StringComparer.OrdinalIgnoreCase))
            return "minimumSeverity must be info, minor, moderate, serious, or critical.";
        if (request.Rules is { Count: > 0 })
        {
            var unknown = request.Rules.FirstOrDefault(rule =>
                !LayoutDiagnosticRules.All.Contains(rule, StringComparer.OrdinalIgnoreCase));
            if (unknown is not null)
                return $"Unknown layout diagnostic rule '{unknown}'.";
        }
        if (request.Scope.MaxDepth < 0)
            return "scope.maxDepth must be zero or greater.";
        if (request.MaxElements is < 1 or > LayoutDiagnosticsFormat.MaxElements)
            return $"maxElements must be between 1 and {LayoutDiagnosticsFormat.MaxElements}.";
        if (!new[] { "wait", "immediate" }
            .Contains(request.Stability.Mode, StringComparer.OrdinalIgnoreCase))
            return "stability.mode must be wait or immediate.";
        if (request.Stability.StableFrames is < 1 or > 10)
            return "stability.stableFrames must be between 1 and 10.";
        if (request.Stability.QuietPeriodMs is < 0 or > 10_000)
            return "stability.quietPeriodMs must be between 0 and 10000.";
        if (request.Stability.TimeoutMs is < 1 or > 60_000)
            return "stability.timeoutMs must be between 1 and 60000.";
        if (!new[] { "none", "interactiveTargets", "all" }
            .Contains(request.Occlusion.Mode, StringComparer.OrdinalIgnoreCase))
            return "occlusion.mode must be none, interactiveTargets, or all.";
        if (request.Occlusion.MaxSamplesPerElement is < 1 or > 1000)
            return "occlusion.maxSamplesPerElement must be between 1 and 1000.";
        if (!double.IsFinite(request.Occlusion.CoverageError) ||
            request.Occlusion.CoverageError <= 0 ||
            request.Occlusion.CoverageError >= 1)
        {
            return "occlusion.coverageError must be greater than 0 and less than 1.";
        }
        if (!double.IsFinite(request.Occlusion.MinimumOverlapRatio) ||
            request.Occlusion.MinimumOverlapRatio < 0 ||
            request.Occlusion.MinimumOverlapRatio > 1)
        {
            return "occlusion.minimumOverlapRatio must be between 0 and 1.";
        }
        if (!new[] { "none", "length", "full" }
            .Contains(request.Privacy.Text, StringComparer.OrdinalIgnoreCase))
            return "privacy.text must be none, length, or full.";
        return null;
    }

    /// <summary>
    /// Window rectangle used by the window-relative rule. Returns null when the platform cannot
    /// report a usable size, which downgrades that rule to <c>incomplete</c> rather than a pass.
    /// </summary>
    private LayoutRect? ResolveWindowRect(int? window)
    {
        // A null window scope walks every window. One rectangle cannot truthfully represent
        // multiple windows with different sizes, so disable window-relative rules in that case.
        if (window is null && _app?.Windows.Count != 1)
            return null;

        var target = GetWindow(window);
        if (target is null)
            return null;

        var width = target.Width;
        var height = target.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            var (nativeWidth, nativeHeight) = GetNativeWindowSize(target);
            if (nativeWidth > 0) width = nativeWidth;
            if (nativeHeight > 0) height = nativeHeight;
        }

        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return null;

        return new LayoutRect { X = 0, Y = 0, Width = width, Height = height };
    }

    /// <summary>Capability descriptor advertised under <c>diagnostics.layout</c>.</summary>
    private static object BuildLayoutDiagnosticsCapability() => new
    {
        version = 2,
        supported = true,
        schemaVersion = LayoutDiagnosticsFormat.SchemaVersion,
        ruleSetVersion = LayoutDiagnosticsFormat.RuleSetVersion,
        maxElements = LayoutDiagnosticsFormat.MaxElements,
        profiles = new[] { "agent", "strict", "exhaustive", "ci" },
        features = new[]
        {
            "on-demand", "scoped-root", "window-scope", "coverage", "source-location",
            "rule-filter", "severity-filter", "evidence-control", "pass-accounting",
            "privacy-control", "suppressions", "rule-catalog"
        },
        rules = LayoutDiagnosticRules.All,
        neverCaptured = LayoutDiagnosticsFormat.NeverCaptured,
        limitations = new[]
        {
            "Managed MAUI layout state only — no platform clipping, occlusion, transform, or text-truncation data.",
            "One-shot scan: there is no watch mode, no delta stream, and no automatic re-scan.",
            "WebView (Blazor/CDP) content is not traversed.",
            "Unavailable geometry is reported as incomplete, never as a pass.",
        },
    };
}
