using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Maui.Controls;
using Microsoft.Maui.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core.LayoutDiagnostics;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.Dispatching;
using CoreLayoutFormat = Microsoft.Maui.DevFlow.Agent.Core.LayoutDiagnostics.LayoutDiagnosticsFormat;
using CoreLayoutRules = Microsoft.Maui.DevFlow.Agent.Core.LayoutDiagnostics.LayoutDiagnosticRules;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// End-to-end coverage for the layout diagnostics endpoint, the typed driver API, and the
/// walker's runtime element map.
/// </summary>
public class LayoutDiagnosticsAgentTests
{
    [Fact]
    public async Task Endpoint_ReturnsAVersionedReportWithCoverageAndLimitations()
    {
        var label = new Label { AutomationId = "Title", Text = "secret label text" };
        using var harness = await LayoutHarness.CreateAsync(label);

        var report = await harness.Client.GetLayoutDiagnosticsAsync();

        Assert.NotNull(report);
        Assert.Equal(CoreLayoutFormat.SchemaVersion, report!.SchemaVersion);
        Assert.Equal(CoreLayoutFormat.RuleSetVersion, report.RuleSetVersion);
        Assert.True(report.Scope.ElementsExamined > 0);
        Assert.Equal(CoreLayoutRules.Managed.Count, report.Coverage.Rules.Count);
        Assert.NotEmpty(report.Coverage.Limitations);
        Assert.Contains("Element Text/Value content", report.Coverage.NeverCaptured);
    }

    [Fact]
    public async Task Endpoint_NeverReturnsElementTextOrValues()
    {
        var label = new Label { AutomationId = "Title", Text = "secret label text" };
        var entry = new Entry { AutomationId = "Email", Text = "alice@example.com" };
        using var harness = await LayoutHarness.CreateAsync(label, entry);

        var raw = await harness.GetRawAsync("/api/v1/ui/diagnostics/layout");

        Assert.DoesNotContain("secret label text", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Endpoint_ScopesToTheRequestedElementSubtree()
    {
        var label = new Label { AutomationId = "Title" };
        var entry = new Entry { AutomationId = "Email" };
        using var harness = await LayoutHarness.CreateAsync(label, entry);

        var full = await harness.Client.GetLayoutDiagnosticsAsync();
        var scoped = await harness.Client.GetLayoutDiagnosticsAsync(elementId: "Title");

        Assert.NotNull(full);
        Assert.NotNull(scoped);
        Assert.Equal("Title", scoped!.Scope.RootElementId);
        Assert.Equal(1, scoped.Scope.ElementsExamined);
        Assert.True(full!.Scope.ElementsExamined > scoped.Scope.ElementsExamined);
    }

    [Fact]
    public async Task Endpoint_ResolvesScopedRootBeforeApplyingElementBudget()
    {
        using var harness = await LayoutHarness.CreateAsync(
            new Label { AutomationId = "First" },
            new Label { AutomationId = "Second" },
            new Label { AutomationId = "Target" });

        var scoped = await harness.Client.GetLayoutDiagnosticsAsync(
            elementId: "Target",
            maxElements: 1);

        Assert.NotNull(scoped);
        Assert.Equal("Target", scoped!.Scope.RootElementId);
        Assert.Equal(1, scoped.Scope.ElementsExamined);
        Assert.False(scoped.Scope.Truncated);
    }

    [Fact]
    public async Task Endpoint_ReturnsNullForAnUnknownElement()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        var report = await harness.Client.GetLayoutDiagnosticsAsync(elementId: "does-not-exist");

        Assert.Null(report);
    }

    [Fact]
    public async Task Endpoint_ClampsAndReportsTheElementBudget()
    {
        using var harness = await LayoutHarness.CreateAsync(
            new Label { AutomationId = "A" },
            new Label { AutomationId = "B" },
            new Label { AutomationId = "C" });

        var capped = await harness.Client.GetLayoutDiagnosticsAsync(maxElements: 1);
        var oversized = await harness.Client.GetLayoutDiagnosticsAsync(maxElements: 999_999);

        Assert.NotNull(capped);
        Assert.Equal(1, capped!.Scope.MaxElements);
        Assert.Equal(1, capped.Scope.ElementsExamined);
        Assert.True(capped.Scope.Truncated);
        Assert.Contains(capped.Coverage.Limitations, limitation =>
            limitation.Contains("element budget", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(oversized);
        Assert.Equal(CoreLayoutFormat.MaxElements, oversized!.Scope.MaxElements);
        Assert.False(oversized.Scope.Truncated);
    }

    [Fact]
    public async Task Endpoint_AcceptsAPostBodyForTheSameScan()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        var raw = await harness.PostRawAsync(
            "/api/v1/ui/diagnostics/layout",
            """{"elementId":"Title","maxElements":10}""");

        using var document = JsonDocument.Parse(raw);
        var scope = document.RootElement.GetProperty("scope");
        Assert.Equal("Title", scope.GetProperty("rootElementId").GetString());
        Assert.Equal(10, scope.GetProperty("maxElements").GetInt32());
    }

    [Fact]
    public async Task Endpoint_AcceptsTheRichVersionedRequest()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        var report = await harness.Client.AnalyzeLayoutAsync(
            new Microsoft.Maui.DevFlow.Driver.LayoutInspectionRequest
            {
                Scope = new Microsoft.Maui.DevFlow.Driver.LayoutInspectionScope
                {
                    RootElementId = "Title",
                },
                Rules = [Microsoft.Maui.DevFlow.Driver.LayoutDiagnosticRules.VisibleZeroArea],
                IncludePasses = true,
            });

        Assert.NotNull(report);
        Assert.Equal(CoreLayoutFormat.SchemaVersion, report!.SchemaVersion);
        Assert.Equal("Title", report.Scope.RootElementId);
        Assert.Single(report.Coverage.Rules);
        Assert.NotEmpty(report.Snapshot.Id);
        Assert.NotEmpty(report.Snapshot.TreeRevision);
    }

    [Fact]
    public async Task RuleCatalog_AdvertisesTheFullContract()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        var catalog = await harness.Client.GetLayoutDiagnosticRulesAsync();

        Assert.NotNull(catalog);
        Assert.Equal(CoreLayoutRules.All.Count, catalog!.Rules.Count);
        Assert.Contains(catalog.Profiles, profile => profile == "agent");
    }

    [Fact]
    public async Task RichRequest_ReturnsTypedValidationErrors()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        var error = await Assert.ThrowsAsync<LayoutDiagnosticsException>(() =>
            harness.Client.AnalyzeLayoutAsync(
                new Microsoft.Maui.DevFlow.Driver.LayoutInspectionRequest
                {
                    Rules = ["layout.not-a-rule"],
                }));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("layout-diagnostics-validation", error.ErrorType);
        Assert.Contains("Unknown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_AdvertiseLayoutDiagnosticsWithItsLimitations()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        var capabilities = await harness.Client.GetCapabilitiesAsync();
        var layout = capabilities.GetProperty("capabilities").GetProperty("diagnostics.layout");

        Assert.True(layout.GetProperty("supported").GetBoolean());
        Assert.Equal(2, layout.GetProperty("version").GetInt32());
        Assert.Equal(CoreLayoutFormat.SchemaVersion, layout.GetProperty("schemaVersion").GetString());
        Assert.Equal(CoreLayoutFormat.MaxElements, layout.GetProperty("maxElements").GetInt32());
        Assert.NotEmpty(layout.GetProperty("rules").EnumerateArray());
        Assert.NotEmpty(layout.GetProperty("limitations").EnumerateArray());
        Assert.NotEmpty(layout.GetProperty("neverCaptured").EnumerateArray());
    }

    [Fact]
    public async Task Scan_DoesNotRetainRuntimeElementReferencesAfterwards()
    {
        using var harness = await LayoutHarness.CreateAsync(new Label { AutomationId = "Title" });

        await harness.Client.GetLayoutDiagnosticsAsync();

        Assert.Empty(harness.Walker.WalkElements);
        Assert.False(harness.Walker.CaptureWalkElements);
    }

    [Fact]
    public void Walker_OnlyRecordsRuntimeElementsWhenCaptureIsEnabled()
    {
        var walker = new VisualTreeWalker();
        var app = new TestApplication([new Label { AutomationId = "Title" }]);

        walker.WalkTree(app);
        Assert.Empty(walker.WalkElements);

        walker.CaptureWalkElements = true;
        walker.WalkTree(app);
        Assert.NotEmpty(walker.WalkElements);

        // A second walk rebuilds the map rather than accumulating across walks.
        var count = walker.WalkElements.Count;
        walker.WalkTree(app);
        Assert.Equal(count, walker.WalkElements.Count);

        walker.ClearWalkElements();
        Assert.Empty(walker.WalkElements);
    }

    [Fact]
    public void SnapshotCollector_UsesEffectiveAncestorVisibility()
    {
        Microsoft.Maui.DevFlow.Agent.Core.ElementInfo[] roots =
        {
            new Microsoft.Maui.DevFlow.Agent.Core.ElementInfo
            {
                Id = "parent",
                Type = "Grid",
                IsVisible = false,
                Opacity = 1,
                Children =
                [
                    new Microsoft.Maui.DevFlow.Agent.Core.ElementInfo
                    {
                        Id = "child",
                        ParentId = "parent",
                        Type = "Label",
                        IsVisible = true,
                        Opacity = 1,
                    }
                ]
            }
        };

        var result = LayoutSnapshotCollector.Collect(
            roots,
            new Dictionary<string, object>(),
            rootElementId: null,
            maxElements: 10);
        var scoped = LayoutSnapshotCollector.Collect(
            roots,
            new Dictionary<string, object>(),
            rootElementId: "child",
            maxElements: 10);

        Assert.False(Assert.Single(result.Snapshots, item => item.Id == "child").IsVisible);
        Assert.False(Assert.Single(scoped.Snapshots).IsVisible);
    }

    [Fact]
    public void Window_IsAVisibleStructuralRootForEffectiveVisibility()
    {
        var label = new Label { AutomationId = "Title", IsVisible = true };
        var window = new Window(new ContentPage { Content = label });
        var walker = new VisualTreeWalker { CaptureWalkElements = true };
        var roots = walker.WalkTree(new TestApplication([window]));

        var root = Assert.Single(roots);
        Assert.Equal("Window", root.Type);
        Assert.True(root.IsVisible);

        var collected = LayoutSnapshotCollector.Collect(
            roots,
            walker.WalkElements,
            rootElementId: null,
            maxElements: 20);
        Assert.True(Assert.Single(
            collected.Snapshots,
            snapshot => snapshot.Id == "Title").IsVisible);
    }

    [Fact]
    public void ShellItems_PreserveTheirStructuralVisibility()
    {
        var shellContent = new ShellContent
        {
            AutomationId = "NativeRoute",
            IsVisible = true,
            IsEnabled = true,
        };
        var roots = new VisualTreeWalker().WalkTree(new TestApplication([shellContent]));

        var info = Assert.Single(roots);
        Assert.Equal("ShellContent", info.Type);
        Assert.True(info.IsVisible);
        Assert.True(info.IsEnabled);
    }

    [Fact]
    public void Walker_ElementBudgetStopsTraversalAndRuntimeReferenceCapture()
    {
        var walker = new VisualTreeWalker { CaptureWalkElements = true };
        var app = new TestApplication(
            Enumerable.Range(0, 20).Select(index =>
                new Label { AutomationId = $"Label{index}" }));

        var tree = walker.WalkTree(app, maxDepth: 0, windowIndex: null, maxElements: 2);

        Assert.Equal(2, tree.Count);
        Assert.Equal(2, walker.WalkElements.Count);
        Assert.True(walker.WalkWasTruncated);
    }

    [Fact]
    public void Walker_ElementBudgetIncludesSyntheticToolbarItems()
    {
        var page = new ContentPage();
        for (var index = 0; index < 10; index++)
            page.ToolbarItems.Add(new ToolbarItem { Text = $"Item {index}" });
        var walker = new VisualTreeWalker();
        var app = new TestApplication([page]);

        var tree = walker.WalkTree(app, maxDepth: 0, windowIndex: null, maxElements: 2);
        var flattened = VisualTreeWalker.FlattenElementInfos(tree).ToList();

        Assert.Equal(2, flattened.Count);
        Assert.True(walker.WalkWasTruncated);
    }

    [Fact]
    public void Walker_ObjectReferenceCachePreservesIdAcrossLookupAndSubtreeWalk()
    {
        var walker = new VisualTreeWalker();
        var element = new Label { AutomationId = "StableId" };
        var generateId = typeof(VisualTreeWalker).GetMethod(
            "GenerateId",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var elementCache = (Dictionary<Guid, string>)typeof(VisualTreeWalker)
            .GetField("_elementIdToExternalId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(walker)!;

        var first = (string)generateId.Invoke(walker, [element])!;
        elementCache.Clear();
        var second = (string)generateId.Invoke(walker, [element])!;

        Assert.Equal(first, second);
        Assert.Equal("StableId", second);
    }

    [Fact]
    public void Walker_ObjectReferenceCacheDoesNotRetainRemovedTree()
    {
        var walker = new VisualTreeWalker();
        var reference = WalkTemporaryTree(walker);

        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }

        Assert.False(reference.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WalkTemporaryTree(VisualTreeWalker walker)
    {
        var label = new Label { AutomationId = "Temporary" };
        typeof(VisualTreeWalker)
            .GetMethod("GenerateId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(walker, [label]);
        return new WeakReference(label);
    }

    private sealed class LayoutHarness : IDisposable
    {
        private readonly DevFlowAgentService _service;
        private readonly HttpClient _http;

        private LayoutHarness(DevFlowAgentService service, AgentClient client, VisualTreeWalker walker, int port)
        {
            _service = service;
            Client = client;
            Walker = walker;
            _http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        }

        public AgentClient Client { get; }

        public VisualTreeWalker Walker { get; }

        public Task<string> GetRawAsync(string path) => _http.GetStringAsync(path);

        public async Task<string> PostRawAsync(string path, string json)
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content);
            return await response.Content.ReadAsStringAsync();
        }

        public static async Task<LayoutHarness> CreateAsync(params View[] views)
        {
            var service = new TestAgentService(new AgentOptions { Port = GetFreePort() });
            var client = new AgentClient("localhost", service.Port);
            service.StartServerOnly(new ImmediateDispatcher());
            service.BindApp(new TestApplication(views));

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (await client.GetStatusAsync() is not null)
                    return new LayoutHarness(service, client, service.Walker, service.Port);
                await Task.Delay(50);
            }

            client.Dispose();
            service.Dispose();
            throw new InvalidOperationException("DevFlow layout test agent did not start.");
        }

        public void Dispose()
        {
            _http.Dispose();
            Client.Dispose();
            _service.Dispose();
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    /// <summary>Exposes the walker instance so a test can assert the per-scan map is released.</summary>
    private sealed class TestAgentService : DevFlowAgentService
    {
        public TestAgentService(AgentOptions options) : base(options) { }

        public VisualTreeWalker Walker { get; private set; } = null!;

        protected override VisualTreeWalker CreateTreeWalker()
        {
            Walker = new VisualTreeWalker();
            return Walker;
        }
    }

    private sealed class TestApplication : Application, IVisualTreeElement
    {
        private readonly IReadOnlyList<IVisualTreeElement> _children;

        public TestApplication(IEnumerable<IVisualTreeElement> views)
            => _children = views.ToArray();

        IReadOnlyList<IVisualTreeElement> IVisualTreeElement.GetVisualChildren() => _children;

        IVisualTreeElement? IVisualTreeElement.GetVisualParent() => null;
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public bool IsDispatchRequired => false;
        public bool Dispatch(Action action) { action(); return true; }
        public bool DispatchDelayed(TimeSpan delay, Action action) { action(); return true; }
        public IDispatcherTimer CreateTimer() => new ImmediateDispatcherTimer();
    }

    private sealed class ImmediateDispatcherTimer : IDispatcherTimer
    {
        public bool IsRepeating { get; set; }
        public TimeSpan Interval { get; set; }
        public bool IsRunning { get; private set; }
        public event EventHandler? Tick { add { } remove { } }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }
}
