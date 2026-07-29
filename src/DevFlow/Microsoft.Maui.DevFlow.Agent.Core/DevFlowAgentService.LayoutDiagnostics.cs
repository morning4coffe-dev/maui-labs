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
        var layoutRequest = new LayoutDiagnosticsRequest
        {
            ElementId = request.QueryParams.TryGetValue("elementId", out var elementId) && !string.IsNullOrWhiteSpace(elementId)
                ? elementId
                : null,
            Window = ParseWindowIndex(request),
            MaxElements = request.QueryParams.TryGetValue("maxElements", out var max) && int.TryParse(max, out var parsed)
                ? parsed
                : null,
        };
        return RunLayoutDiagnosticsAsync(layoutRequest);
    }

    private Task<HttpResponse> HandleLayoutDiagnosticsPost(HttpRequest request)
    {
        var layoutRequest = request.BodyAs<LayoutDiagnosticsRequest>() ?? new LayoutDiagnosticsRequest();
        layoutRequest.Window ??= ParseWindowIndex(request);
        return RunLayoutDiagnosticsAsync(layoutRequest);
    }

    private async Task<HttpResponse> RunLayoutDiagnosticsAsync(LayoutDiagnosticsRequest request)
    {
        if (_app == null)
            return HttpResponse.Error("Agent not bound to app");

        var maxElements = Math.Clamp(
            request.MaxElements ?? LayoutDiagnosticsFormat.DefaultMaxElements,
            1,
            LayoutDiagnosticsFormat.MaxElements);
        var rootElementId = string.IsNullOrWhiteSpace(request.ElementId) ? null : request.ElementId!.Trim();

        // Serialize scans so two concurrent callers cannot interleave walker state.
        await _layoutDiagnosticsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var report = await DispatchAsync(() => CaptureLayoutDiagnostics(rootElementId, request.Window, maxElements));
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
    private LayoutDiagnosticsReport? CaptureLayoutDiagnostics(string? rootElementId, int? window, int maxElements)
    {
        var app = _app;
        if (app == null)
            return null;

        _treeWalker.CaptureWalkElements = true;
        try
        {
            var roots = rootElementId is null
                ? _treeWalker.WalkTree(
                    app,
                    maxDepth: 0,
                    windowIndex: window,
                    maxElements: maxElements)
                : _treeWalker.WalkSubtree(
                    app,
                    rootElementId,
                    maxDepth: 0,
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
                DateTime.UtcNow);
        }
        finally
        {
            // Never retain strong references to live elements past the scan.
            _treeWalker.CaptureWalkElements = false;
            _treeWalker.ClearWalkElements();
        }
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
        version = 1,
        supported = true,
        schemaVersion = LayoutDiagnosticsFormat.SchemaVersion,
        ruleSetVersion = LayoutDiagnosticsFormat.RuleSetVersion,
        maxElements = LayoutDiagnosticsFormat.MaxElements,
        features = new[] { "on-demand", "scoped-root", "window-scope", "coverage", "source-location" },
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
