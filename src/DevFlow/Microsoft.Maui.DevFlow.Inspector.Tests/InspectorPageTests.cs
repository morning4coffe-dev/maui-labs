using System.Globalization;
using Microsoft.Playwright;
using Xunit;

namespace Microsoft.Maui.DevFlow.Inspector.Tests;

/// <summary>
/// Playwright integration tests for the DevFlow Web Inspector.
/// Requires the broker running with a connected MAUI app.
/// The inspector is available at http://localhost:19223/inspector/.
/// Set INSPECTOR_URL environment variable to override the default URL.
///
/// The default URL points to the broker's single-agent fallback route
/// (the broker uses the only connected agent when the id segment doesn't match).
///
/// These tests are categorized as "Integration" and excluded from normal CI runs
/// (which cannot spin up a broker + connected agent). Run them locally with:
///   dotnet test --filter "Category=Integration"
/// </summary>
[Collection("Inspector")]
[Trait("Category", "Integration")]
public class InspectorPageTests : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IBrowserContext _context = null!;
    private IPage _page = null!;

    private Uri BaseUri => new(Environment.GetEnvironmentVariable("INSPECTOR_URL") ?? "http://localhost:19223/inspector/default/");

    private string BaseUrl => BaseUri.ToString();

    private string ResolveUrl(string relativePath) => new Uri(BaseUri, relativePath).ToString();

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        _context = await _browser.NewContextAsync();
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    [LiveInspectorFact]
    public async Task ViewportUsesWindowDimensionsFromAgent()
    {
        await _page.GotoAsync(BaseUrl);
        var viewport = _page.Locator("#app-viewport");
        await Expect(viewport).ToBeVisibleAsync();

        var width = await viewport.GetAttributeAsync("data-width");
        var height = await viewport.GetAttributeAsync("data-height");

        var w = double.Parse(width!, CultureInfo.InvariantCulture);
        var h = double.Parse(height!, CultureInfo.InvariantCulture);
        Assert.True(w > 0, "Viewport width should be positive");
        Assert.True(h > 0, "Viewport height should be positive");

        // Correlate with the live /api/state values to prove the DOM is wired to the
        // agent-reported window dimensions rather than a hardcoded fallback. Using a
        // hardcoded "not iPhone size" assertion would false-positive on a real iPhone.
        var stateResponse = await _page.APIRequest.GetAsync(ResolveUrl("api/state"));
        Assert.True(stateResponse.Ok);
        var stateJson = System.Text.Json.JsonDocument.Parse(await stateResponse.TextAsync());
        var apiW = stateJson.RootElement.GetProperty("viewportWidth").GetDouble();
        var apiH = stateJson.RootElement.GetProperty("viewportHeight").GetDouble();

        // Allow 1px slack for fractional rounding between layout passes.
        Assert.True(Math.Abs(apiW - w) <= 1.0,
            $"DOM data-width {w} should match /api/state viewportWidth {apiW}");
        Assert.True(Math.Abs(apiH - h) <= 1.0,
            $"DOM data-height {h} should match /api/state viewportHeight {apiH}");
    }

    [LiveInspectorFact]
    public async Task ViewportHasFixedDimensions()
    {
        await _page.GotoAsync(BaseUrl);
        var viewport = _page.Locator("#app-viewport");

        // Viewport should have explicit width/height style
        var style = await viewport.GetAttributeAsync("style");
        Assert.NotNull(style);
        Assert.Contains("width:", style);
        Assert.Contains("height:", style);
    }

    [LiveInspectorFact]
    public async Task ScreenshotImageIsPresent()
    {
        await _page.GotoAsync(BaseUrl);
        var screenshot = _page.Locator("#screenshot");
        await Expect(screenshot).ToBeVisibleAsync();

        var src = await screenshot.GetAttributeAsync("src");
        Assert.Contains("screenshot.png", src);
    }

    [LiveInspectorFact]
    public async Task NoInspectorChromeRendered()
    {
        await _page.GotoAsync(BaseUrl);

        // No toolbar, no connection status — the host inspector tool provides its own chrome
        await Expect(_page.Locator("#devflow-toolbar")).ToHaveCountAsync(0);
        await Expect(_page.Locator("#btn-back")).ToHaveCountAsync(0);
        await Expect(_page.Locator("#connection-status")).ToHaveCountAsync(0);
    }

    [LiveInspectorFact]
    public async Task ElementsRenderedAsPositionedDivs()
    {
        await _page.GotoAsync(BaseUrl);
        var elements = _page.Locator(".devflow-element");
        var count = await elements.CountAsync();
        Assert.True(count > 0, "Should have at least one element div");

        // First element should have required data attributes
        var first = elements.First;
        var id = await first.GetAttributeAsync("data-id");
        var type = await first.GetAttributeAsync("data-type");
        Assert.NotNull(id);
        Assert.NotNull(type);
    }

    [LiveInspectorFact]
    public async Task ElementPositionsMatchAppCoordinates()
    {
        await _page.GotoAsync(BaseUrl);

        // Find an element with bounds
        var positioned = _page.Locator(".devflow-element[style*='left:']");
        var count = await positioned.CountAsync();
        Assert.True(count > 0, "Should have positioned elements");

        var style = await positioned.First.GetAttributeAsync("style");
        Assert.NotNull(style);
        Assert.Contains("position:absolute", style);
        Assert.Matches(@"left:\d", style);
        Assert.Matches(@"top:\d", style);
    }

    [LiveInspectorFact]
    public async Task ElementTreeIsFlatNotNested()
    {
        await _page.GotoAsync(BaseUrl);

        // All elements should be direct children of viewport (flat rendering)
        var directChildren = _page.Locator("#app-viewport > .devflow-element");
        var nested = _page.Locator(".devflow-element > .devflow-element");

        Assert.True(await directChildren.CountAsync() > 0, "Should have flat element divs");
        Assert.Equal(0, await nested.CountAsync());
    }

    [LiveInspectorFact]
    public async Task DataAttributesUseCamelCase()
    {
        // CSS attribute selectors like [data-isVisible] are case-insensitive in HTML,
        // so a Locator-based assertion would also match the lowercased serialization
        // that we are guarding against. Fetch the raw HTML and assert the byte-level
        // serialization preserves camelCase as advertised.
        var response = await _page.APIRequest.GetAsync(BaseUrl);
        Assert.True(response.Ok);
        var html = await response.TextAsync();

        // DevFlow properties use camelCase: isVisible, isEnabled, fullType
        Assert.Contains("data-isVisible", html);
        Assert.Contains("data-isEnabled", html);
        Assert.DoesNotContain("data-isvisible", html);
        Assert.DoesNotContain("data-isenabled", html);
    }

    [LiveInspectorFact]
    public async Task CssServedSeparately()
    {
        var response = await _page.APIRequest.GetAsync(ResolveUrl("devflow.css"));
        Assert.True(response.Ok);
        var text = await response.TextAsync();
        Assert.Contains("#app-viewport", text);
        Assert.Contains(".devflow-element", text);
        // The shared inspector is a full interactive tool (toolbar, tree, docked properties,
        // timeline), so it ships its own hover/selection chrome — hover styles are expected here.
    }

    [LiveInspectorFact]
    public async Task ClickSendsTapToAgent()
    {
        await _page.GotoAsync(BaseUrl);

        // Get the viewport bounding box
        var viewport = _page.Locator("#app-viewport");
        var box = await viewport.BoundingBoxAsync();
        Assert.NotNull(box);

        // Click in the middle of the viewport
        await viewport.ClickAsync(new() { Position = new() { X = (float)box.Width / 2, Y = (float)box.Height / 2 } });

        // Wait for AJAX refresh (devflow.js refreshes after tap via /api/state)
        await _page.WaitForTimeoutAsync(1000);

        // The screenshot src should identify the immutable frame captured after the action.
        var screenshotAfter = await _page.Locator("#screenshot").GetAttributeAsync("src");
        Assert.Contains("?frame=", screenshotAfter);
    }

    [LiveInspectorFact]
    public async Task ClickOnElementSendsTapAtCorrectCoordinates()
    {
        await _page.GotoAsync(BaseUrl);

        // Set up request interception to capture tap coordinates
        var tapRequests = new List<string>();
        await _page.RouteAsync("**/api/tap", async route =>
        {
            var body = route.Request.PostData;
            tapRequests.Add(body ?? "");
            await route.ContinueAsync();
        });

        // Find an element with positive width and height in style (not -1 or 0)
        var allPositioned = _page.Locator(".devflow-element[style*='width:']");
        var count = await allPositioned.CountAsync();
        ILocator? target = null;

        for (int i = 0; i < count; i++)
        {
            var style = await allPositioned.Nth(i).GetAttributeAsync("style") ?? "";
            // Parse width value — skip elements with -1 or 0 width
            var widthMatch = System.Text.RegularExpressions.Regex.Match(style, @"width:(\d+)px");
            var heightMatch = System.Text.RegularExpressions.Regex.Match(style, @"height:(\d+)px");
            if (widthMatch.Success && heightMatch.Success)
            {
                var w = int.Parse(widthMatch.Groups[1].Value);
                var h = int.Parse(heightMatch.Groups[1].Value);
                if (w > 10 && h > 10)
                {
                    target = allPositioned.Nth(i);
                    break;
                }
            }
        }

        if (target == null)
        {
            // No suitable element found — skip
            return;
        }

        // Click with force (the div is transparent overlay, not visually rendered)
        await target.ClickAsync(new() { Force = true, Timeout = 5000 });
        await _page.WaitForTimeoutAsync(300);

        // Verify a tap request was sent with valid coordinates
        Assert.NotEmpty(tapRequests);
        var json = System.Text.Json.JsonDocument.Parse(tapRequests[0]);
        var x = json.RootElement.GetProperty("x").GetDouble();
        var y = json.RootElement.GetProperty("y").GetDouble();
        Assert.True(x >= 0, $"Tap x should be non-negative, got {x}");
        Assert.True(y >= 0, $"Tap y should be non-negative, got {y}");
    }

    [LiveInspectorFact]
    public async Task ScreenshotEndpointReturnsPng()
    {
        var response = await _page.APIRequest.GetAsync(ResolveUrl("screenshot.png"));
        Assert.True(response.Ok);
        var body = await response.BodyAsync();

        // PNG magic bytes
        Assert.Equal(0x89, body[0]);
        Assert.Equal(0x50, body[1]); // P
        Assert.Equal(0x4E, body[2]); // N
        Assert.Equal(0x47, body[3]); // G
    }

    [LiveInspectorFact]
    public async Task StateEndpointReturnsJsonWithElements()
    {
        var response = await _page.APIRequest.GetAsync(ResolveUrl("api/state"));
        Assert.True(response.Ok);
        var text = await response.TextAsync();
        var json = System.Text.Json.JsonDocument.Parse(text);

        Assert.True(json.RootElement.TryGetProperty("screenshotUrl", out var url));
        Assert.Contains("screenshot.png", url.GetString());

        Assert.True(json.RootElement.TryGetProperty("elements", out var elements));
        Assert.Contains("devflow-element", elements.GetString());

        Assert.True(json.RootElement.TryGetProperty("viewportWidth", out var vw));
        Assert.True(vw.GetDouble() > 0);

        Assert.True(json.RootElement.TryGetProperty("viewportHeight", out var vh));
        Assert.True(vh.GetDouble() > 0);
    }

    [LiveInspectorFact]
    public async Task AjaxRefreshUpdatesElementsWithoutReload()
    {
        await _page.GotoAsync(BaseUrl);

        // Count initial elements
        var initialCount = await _page.Locator(".devflow-element").CountAsync();
        Assert.True(initialCount > 0);

        // Trigger a refresh via JS (simulating what the polling does)
        await _page.EvaluateAsync(@"async () => {
            const basePath = location.pathname.replace(/\/$/, '');
            const resp = await fetch(basePath + '/api/state');
            return resp.ok;
        }");

        // Elements should still exist (page was not reloaded)
        var afterCount = await _page.Locator(".devflow-element").CountAsync();
        Assert.True(afterCount > 0);
    }

    [LiveInspectorFact]
    public async Task AdaptiveLayoutTracksHostViewport()
    {
        await _page.SetViewportSizeAsync(1200, 820);
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();

        Assert.Equal("wide", await _page.Locator("body").GetAttributeAsync("data-host-layout"));
        await Expect(_page.Locator("#df-more")).ToBeHiddenAsync();
        await Expect(_page.Locator("#df-toolbar-secondary")).ToBeVisibleAsync();
        await _page.Locator(".df-tree-node").Last.ClickAsync();
        await Expect(_page.Locator("#df-props-pane")).ToBeVisibleAsync();
        await _page.Keyboard.PressAsync("Escape");
        await Expect(_page.Locator("#df-props-pane")).ToBeHiddenAsync();

        await _page.SetViewportSizeAsync(900, 820);
        await _page.WaitForTimeoutAsync(100);
        Assert.Equal("compact", await _page.Locator("body").GetAttributeAsync("data-host-layout"));
        await Expect(_page.Locator("#df-more")).ToBeVisibleAsync();
        await _page.Locator(".df-tree-node").Last.ClickAsync();
        await Expect(_page.Locator("#df-props-pane")).ToBeVisibleAsync();
        await Expect(_page.Locator("#df-tree-pane")).ToBeVisibleAsync();
        Assert.False(await _page.Locator("body").EvaluateAsync<bool>("body => body.classList.contains('df-tree-hidden')"));
        var compactTree = await _page.Locator("#df-tree-pane").BoundingBoxAsync();
        var compactScrim = await _page.Locator("#df-pane-scrim").BoundingBoxAsync();
        Assert.NotNull(compactTree);
        Assert.NotNull(compactScrim);
        Assert.True(compactScrim.X >= compactTree.X + compactTree.Width - 1,
            $"Expected compact properties scrim to preserve the tree, tree right={compactTree.X + compactTree.Width}, scrim left={compactScrim.X}");
        await _page.Keyboard.PressAsync("Escape");

        await _page.SetViewportSizeAsync(700, 450);
        await _page.WaitForTimeoutAsync(100);
        Assert.Equal("short", await _page.Locator("body").GetAttributeAsync("data-host-layout"));
        Assert.True(await _page.Locator("body").EvaluateAsync<bool>("body => body.classList.contains('df-tree-hidden')"));
    }

    [LiveInspectorFact]
    public async Task NarrowLayoutCoordinatesTreeAndPropertiesDrawers()
    {
        await _page.SetViewportSizeAsync(420, 820);
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();

        var toolbar = await _page.Locator("#df-toolbar").BoundingBoxAsync();
        Assert.NotNull(toolbar);
        Assert.True(toolbar.Height <= 40, $"Expected a single-row toolbar, got {toolbar.Height}px");
        Assert.True(await _page.Locator("body").EvaluateAsync<bool>("body => body.classList.contains('df-tree-hidden')"));

        await _page.Locator("#df-toggle-tree").ClickAsync();
        await Expect(_page.Locator("#df-tree-pane")).ToBeVisibleAsync();
        await Expect(_page.Locator("#df-pane-scrim")).ToBeVisibleAsync();

        await _page.Locator(".df-tree-node").Last.ClickAsync();
        await Expect(_page.Locator("#df-props-pane")).ToBeVisibleAsync();
        Assert.True(await _page.Locator("body").EvaluateAsync<bool>("body => body.classList.contains('df-tree-hidden')"));

        var props = await _page.Locator("#df-props-pane").BoundingBoxAsync();
        Assert.NotNull(props);
        Assert.True(Math.Abs(props.Width - 420) <= 1, $"Expected a full-width narrow drawer, got {props.Width}px");
    }

    [LiveInspectorFact]
    public async Task ConstrainedDataSheetPreservesScreenshotBudget()
    {
        await _page.SetViewportSizeAsync(420, 500);
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();

        var bodyBefore = await _page.Locator("#df-body").BoundingBoxAsync();
        Assert.NotNull(bodyBefore);

        await _page.Locator("#df-more").ClickAsync();
        await _page.Locator("#df-toggle-dock").ClickAsync();
        await Expect(_page.Locator("#df-dock")).ToBeVisibleAsync();
        await Expect(_page.Locator("#df-pane-scrim")).ToBeVisibleAsync();

        var bodyAfter = await _page.Locator("#df-body").BoundingBoxAsync();
        Assert.NotNull(bodyAfter);
        Assert.True(Math.Abs(bodyBefore.Height - bodyAfter.Height) <= 1,
            $"Opening the constrained data sheet changed body height {bodyBefore.Height} -> {bodyAfter.Height}");

        await _page.Locator("#df-dock-collapse").ClickAsync();
        Assert.True(await _page.Locator("body").EvaluateAsync<bool>("body => body.classList.contains('df-dock-collapsed')"));
    }

    [LiveInspectorFact]
    public async Task DataContentStartsDirectlyBelowTabs()
    {
        await _page.SetViewportSizeAsync(420, 500);
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();

        await _page.Locator("#df-more").ClickAsync();
        await _page.Locator("#df-toggle-dock").ClickAsync();
        await Expect(_page.Locator("#df-dock-body > *").First).ToBeVisibleAsync();

        var tabs = await _page.Locator("#df-dock-tabs").BoundingBoxAsync();
        var content = await _page.Locator("#df-dock-body > *").First.BoundingBoxAsync();
        Assert.NotNull(tabs);
        Assert.NotNull(content);
        Assert.True(Math.Abs(content.Y - (tabs.Y + tabs.Height)) <= 1,
            $"Expected Data content to start at the tab boundary, gap was {content.Y - (tabs.Y + tabs.Height)}px");
    }

    [LiveInspectorFact]
    public async Task DataControlsUseThemedSvgIcons()
    {
        await _page.RouteAsync("**/api/sensors", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"ok":true,"sensors":[]}""",
        }));
        await _page.RouteAsync("**/api/files/roots", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """
                {"ok":true,"roots":{"roots":[{"id":"appData","displayName":"App data","kind":"appData","isReadOnly":false,"isPersistent":true,"isUserVisible":false}]}}
                """,
        }));
        await _page.RouteAsync("**/api/files/list", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """
                {"ok":true,"files":{"root":"appData","path":"","entries":[{"name":"logs","type":"directory"},{"name":"settings.json","type":"file","size":42}]}}
                """,
        }));

        await OpenDataDockAsync();

        await _page.Locator("[data-tab='sensors']").ClickAsync();
        await Expect(_page.Locator("#df-dock-body button use[href='#i-location']")).ToHaveCountAsync(1);
        Assert.DoesNotContain("📍", await _page.Locator("#df-dock-body").InnerTextAsync());

        await _page.Locator("[data-tab='files']").ClickAsync();
        await Expect(_page.Locator("#df-dock-body use[href='#i-folder']")).ToHaveCountAsync(1);
        await Expect(_page.Locator("#df-dock-body use[href='#i-file']")).ToHaveCountAsync(1);
        var filesText = await _page.Locator("#df-dock-body").InnerTextAsync();
        Assert.DoesNotContain("📁", filesText);
        Assert.DoesNotContain("📄", filesText);
    }

    [LiveInspectorFact]
    public async Task ToolbarToggleStateIsExposedToAssistiveTechnology()
    {
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();

        await Expect(_page.Locator("#df-mode-interact")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(_page.Locator("#df-mode-inspect")).ToHaveAttributeAsync("aria-pressed", "false");
        await _page.Locator("#df-mode-inspect").ClickAsync();
        await Expect(_page.Locator("#df-mode-interact")).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(_page.Locator("#df-mode-inspect")).ToHaveAttributeAsync("aria-pressed", "true");

        await Expect(_page.Locator("#df-toggle-bounds")).ToHaveAttributeAsync("aria-pressed", "false");
        await _page.Locator("#df-toggle-bounds").ClickAsync();
        await Expect(_page.Locator("#df-toggle-bounds")).ToHaveAttributeAsync("aria-pressed", "true");

        await Expect(_page.Locator("#df-toggle-record")).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(_page.Locator("#df-toggle-dock")).ToHaveAttributeAsync("aria-pressed", "false");
        await _page.Locator("#df-toggle-dock").ClickAsync();
        await Expect(_page.Locator("#df-toggle-dock")).ToHaveAttributeAsync("aria-pressed", "true");
        await _page.Locator("#df-dock-close").ClickAsync();
        await Expect(_page.Locator("#df-toggle-dock")).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [LiveInspectorFact]
    public async Task DataTabSelectionSurvivesRefreshAndCollapse()
    {
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();
        await _page.Locator("#df-toggle-dock").ClickAsync();

        var network = _page.Locator("[data-tab='network']");
        await network.ClickAsync();
        await Expect(network).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(_page.Locator("[data-tab='logs']")).ToHaveAttributeAsync("aria-selected", "false");

        await _page.Locator("#df-dock-refresh").ClickAsync();
        await Expect(network).ToHaveAttributeAsync("aria-selected", "true");
        await _page.Locator("#df-dock-collapse").ClickAsync();
        await Expect(network).ToHaveAttributeAsync("aria-selected", "true");
        await _page.Locator("#df-dock-collapse").ClickAsync();
        await Expect(network).ToHaveAttributeAsync("aria-selected", "true");
    }

    [LiveInspectorFact]
    public async Task OpacityEditorUsesBoundedFractionalKeyboardStep()
    {
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".df-tree-node").First).ToBeAttachedAsync();
        await _page.Locator(".df-tree-node").First.ClickAsync();

        var opacity = _page
            .Locator(".df-prop-row")
            .Filter(new() { HasText = "Opacity" })
            .Locator("input[type='number']");
        await Expect(opacity).ToHaveAttributeAsync("min", "0");
        await Expect(opacity).ToHaveAttributeAsync("max", "1");
        await Expect(opacity).ToHaveAttributeAsync("step", "0.05");
    }

    [LiveInspectorFact]
    public async Task FilesTabExplainsEmptyAppDataRoot()
    {
        await _page.RouteAsync("**/api/files/roots", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """
                {"ok":true,"roots":{"roots":[{"id":"appData","displayName":"App data","kind":"appData","isReadOnly":false,"isPersistent":true,"isUserVisible":false}]}}
                """,
        }));
        await _page.RouteAsync("**/api/files/list", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"ok":true,"files":{"root":"appData","path":"","entries":[]}}""",
        }));

        await OpenDataDockAsync();
        await _page.Locator("[data-tab='files']").ClickAsync();

        Assert.Equal("App data", await _page.Locator("#df-files-root option").First.InnerTextAsync());
        await Expect(_page.Locator("#df-files-root-info")).ToContainTextAsync("Private app storage · persistent");
        await Expect(_page.Locator(".df-files-mode")).ToHaveTextAsync("Browse only");
        await Expect(_page.Locator(".df-files-empty")).ToContainTextAsync("App data is empty.");
        await Expect(_page.Locator(".df-files-empty")).ToContainTextAsync("In-memory data and Preferences are not files.");
    }

    [LiveInspectorFact]
    public async Task NetworkTabRefreshesAutomaticallyAndKeepsDetailOpen()
    {
        var networkCalls = 0;
        await _page.RouteAsync("**/api/network", async route =>
        {
            var call = Interlocked.Increment(ref networkCalls);
            var marker = call == 1 ? "first" : "second";
            if (call == 1) await Task.Delay(3000);
            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = $$"""
                    {"ok":true,"requests":[{"id":"request-1","method":"GET","url":"https://example.test/{{marker}}","statusCode":200,"durationMs":12}]}
                    """,
            });
        });
        await _page.RouteAsync("**/api/network/detail", async route =>
        {
            await Task.Delay(500);
            await route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = """
                    {"ok":true,"request":{"id":"request-1","method":"GET","url":"https://example.test/second","statusCode":200,"statusText":"OK","durationMs":12}}
                    """,
            });
        });

        await OpenDataDockAsync();
        await _page.Locator("[data-tab='network']").ClickAsync();

        await Expect(_page.Locator("#df-dock-body")).ToContainTextAsync(
            "https://example.test/second",
            new() { Timeout = 6000 });
        await _page.WaitForTimeoutAsync(1500);
        await Expect(_page.Locator("#df-dock-body")).Not.ToContainTextAsync("https://example.test/first");
        Assert.True(networkCalls >= 2, $"Expected an automatic network refresh, got {networkCalls} request(s).");
        await Expect(_page.Locator("#df-dock-meta")).ToContainTextAsync("live");

        await Expect(_page.Locator("#df-attach-data")).ToBeEnabledAsync();
        await _page.Locator("#df-dock-body tr.df-row-click").First.ClickAsync();
        await Expect(_page.Locator("#df-attach-data")).ToBeDisabledAsync();
        await Expect(_page.Locator("#df-dock-body")).ToContainTextAsync("Back to requests");
        await Expect(_page.Locator("#df-attach-data")).ToBeEnabledAsync();
        await _page.WaitForTimeoutAsync(2500);
        await Expect(_page.Locator("#df-dock-body")).ToContainTextAsync("Back to requests");
        await Expect(_page.Locator("#df-dock-meta")).ToContainTextAsync("live paused");
    }

    [LiveInspectorFact]
    public async Task DataContextCopyIsBoundedAndRedacted()
    {
        await CaptureClipboardWritesAsync();
        var deepEncodedUrl = EncodeUrlLayers(
            "https://deep-user:deep-password@example.test/path?token=deep-token",
            7);
        var malformedEncodedUrl = EncodeUrlLayers(
            "https://malformed-user:malformed-password@example.test/path?token=malformed-token",
            2) + "%ZZ";
        var encodedWrapperUrl = Uri.EscapeDataString(
            "\"https://wrapper-user:wrapper-password@example.test/path?sig=wrapper-secret\"");
        var deeplyEncodedKey = EncodeQueryKeyLayers("sig", 7);
        var preferences = new Dictionary<string, string>
        {
            ["theme"] = "dark",
            ["apiToken"] = "super-secret-value",
            ["endpoint"] = "https://example.test/items?access_token=url-secret-value",
            ["embeddedUrl"] = "Request failed for https://embedded-user:embedded-password@example.test/path?access_token=embedded-token#embedded-fragment",
            ["indentedUrl"] = "  https://indented-user:indented-password@example.test/path?sig=indented-signature#indented-fragment",
            ["bracketedUrl"] = "Failure [https://bracket-user:bracket-password@example.test]",
            ["nestedUrl"] = "https://proxy.example/fetch?url=https://nested-user:nested-password@internal.example/path?X-Goog-Signature=nested-signature",
            ["outerNestedUrl"] = "https://proxy.example/redirect?to=https://inner.example/a&code=outer-nested-code",
            ["encodedNestedUrl"] = "https://proxy.example/fetch?url=https%3A%2F%2Fencoded-user%3Aencoded-password%40internal.example%2Fpath%3Fkey%3Dencoded-key",
            ["doubleEncodedNestedUrl"] = "https://proxy.example/fetch?url=https%253A%252F%252Fdouble-user%253Adouble-password%2540internal.example%252Fpath%253Fkey%253Ddouble-key",
            ["encodedPathUrl"] = "https://proxy.example/fetch/https%253A%252F%252Fpath-user%253Apath-password%2540internal.example%252Fpath%253Fkey%253Dpath-key",
            ["wholeEncodedUrl"] = Uri.EscapeDataString("https://whole-user:whole-password@example.test/path?token=whole-token"),
            ["deepEncodedUrl"] = deepEncodedUrl,
            ["malformedEncodedUrl"] = "https://proxy.example/fetch?url=" + malformedEncodedUrl,
            ["backslashUrl"] = """https:\\backslash-user:backslash-password@example.test\path?sig=backslash-secret""",
            ["mixedSlashUrl"] = """https:/\mixed-user:mixed-password@example.test/path?sig=mixed-secret""",
            ["zeroSlashUrl"] = "https:storage.example/path?X-Goog-Signature=zero-slash-secret",
            ["mixedEncodedZeroSlashUrl"] = "https:%75ser:mixed-encoded-password@example.test/path?sig=mixed-encoded-zero-secret#mixed-encoded-fragment",
            ["mixedEncodedSlashesUrl"] = "https:%2F%2Fmixed-slash-user%3Amixed-slash-password%40example.test%2Fpath%3Fsig%3Dmixed-slash-encoded-secret%23mixed-slash-fragment",
            ["tabbedKeyUrl"] = "https://storage.example/path?X-Goog-\tSignature=tabbed-key-secret",
            ["gluedUrl"] = "dashboard.https://glued-user:glued-password@example.test/path?sig=glued-secret",
            ["encodedWrapperUrl"] = encodedWrapperUrl,
            ["quotedQueryUrl"] = "https://example.test/path?note=\"useful\"&sig=\"quoted-secret\"",
            ["emptyQuotedQueryUrl"] = "https://example.test/path?sig=\"\"",
            ["wrappedPunctuationUrl"] = "Prefix (https://example.test/path?code=wrapped-punctuation-secret).Next",
            ["parenthesizedPathUrl"] = "See (https://example.test/a(b)c?sig=parenthesized-path-secret#parenthesized-fragment).",
            ["bracketedIpv6Url"] = "[https://[2001:db8::1]/path?sig=ipv6-secret#ipv6-fragment]",
            ["encodedStructureUrl"] = "https://proxy.example/fetch?url="
                + Uri.EscapeDataString("https://safe.test/p?a=1&sig=encoded-structure-secret")
                + "&mode=raw",
            ["queryKeyUrl"] = "https://proxy.example/?https://query-key-user:query-key-password@inner.example/path=value",
            ["providerUrl"] = "https://storage.example/object?key=bare-key&subscription-key=subscription-secret&subscriptionKey=camel-subscription-secret&X-Goog-Signature=provider-signature&XGoogSignature=camel-provider-signature&X-Goog-Credential=provider-credential&hmac=hmac-secret&hdnts=hdnts-secret&hdnea=hdnea-secret&monkey=banana",
            ["encodedKeyUrl"] = "https://example.test/path?%2573%2569%2567=encoded-query-key-secret",
            ["deeplyEncodedKeyUrl"] = "https://example.test/path?" + deeplyEncodedKey + "=deeply-encoded-key-secret",
            ["sessionUrl"] = "https://example.test/path;jsessionid=path-session-secret?JSESSIONID=query-session-secret",
            ["authUrl"] = "https://login.example/callback?ticket=cas-ticket-secret&oobCode=firebase-code-secret&SAMLart=saml-artifact-secret&SAMLResponse=saml-response-secret",
            ["punctuatedUrl"] = "(https://example.test/callback?code=punctuation-secret).",
            ["safeUrl"] = "https://example.test/search?q=hello%20world&tag=~value&tag=two).",
            ["credentialedUrl"] = "https://url-user:url-password@example.test/private#fragment-secret",
            ["note"] = "authorization=header-secret-value",
            ["wireDump"] = "Authorization: Basic basic-secret-value\nCookie: session=cookie-secret-value; theme=dark",
            ["connection"] = "Connection String=\"Server=localhost;Password=connection-secret-value\"",
            ["nearLimitUrl"] = new string('a', 1950) + " https://example.test/?token=near-limit-secret",
        };
        await _page.RouteAsync("**/api/preferences", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = System.Text.Json.JsonSerializer.Serialize(new { ok = true, preferences }),
        }));

        await OpenDataDockAsync();
        await _page.Locator("[data-tab='preferences']").ClickAsync();
        await Expect(_page.Locator("#df-attach-data")).ToBeEnabledAsync();
        await _page.Locator("#df-attach-data").ClickAsync();

        var copied = await _page.EvaluateAsync<string>("() => window.__copiedDevFlowData || ''");
        Assert.Contains("MAUI DevFlow Data snapshot:", copied);
        Assert.Contains("\"theme\": \"dark\"", copied);
        Assert.Contains("redacted", copied, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"agent\":", copied);
        Assert.Contains("\"port\":", copied);
        Assert.Contains("\"followUpTools\":", copied);
        Assert.Contains("maui_preferences_get", copied);
        Assert.DoesNotContain("\"appName\": \"DevFlow Inspector\"", copied);
        Assert.DoesNotContain("super-secret-value", copied);
        Assert.DoesNotContain("url-secret-value", copied);
        Assert.DoesNotContain("header-secret-value", copied);
        Assert.DoesNotContain("url-user", copied);
        Assert.DoesNotContain("url-password", copied);
        Assert.DoesNotContain("fragment-secret", copied);
        Assert.DoesNotContain("basic-secret-value", copied);
        Assert.DoesNotContain("cookie-secret-value", copied);
        Assert.DoesNotContain("connection-secret-value", copied);
        Assert.DoesNotContain("embedded-user", copied);
        Assert.DoesNotContain("embedded-password", copied);
        Assert.DoesNotContain("embedded-token", copied);
        Assert.DoesNotContain("embedded-fragment", copied);
        Assert.DoesNotContain("indented-user", copied);
        Assert.DoesNotContain("indented-password", copied);
        Assert.DoesNotContain("indented-signature", copied);
        Assert.DoesNotContain("indented-fragment", copied);
        Assert.DoesNotContain("bracket-user", copied);
        Assert.DoesNotContain("bracket-password", copied);
        Assert.DoesNotContain("nested-user", copied);
        Assert.DoesNotContain("nested-password", copied);
        Assert.DoesNotContain("nested-signature", copied);
        Assert.DoesNotContain("encoded-user", copied);
        Assert.DoesNotContain("encoded-password", copied);
        Assert.DoesNotContain("encoded-key", copied);
        Assert.DoesNotContain("double-user", copied);
        Assert.DoesNotContain("double-password", copied);
        Assert.DoesNotContain("double-key", copied);
        Assert.DoesNotContain("path-user", copied);
        Assert.DoesNotContain("path-password", copied);
        Assert.DoesNotContain("path-key", copied);
        Assert.DoesNotContain("outer-nested-code", copied);
        Assert.DoesNotContain("whole-user", copied);
        Assert.DoesNotContain("whole-password", copied);
        Assert.DoesNotContain("whole-token", copied);
        Assert.DoesNotContain("deep-user", copied);
        Assert.DoesNotContain("deep-password", copied);
        Assert.DoesNotContain("deep-token", copied);
        Assert.DoesNotContain("malformed-user", copied);
        Assert.DoesNotContain("malformed-password", copied);
        Assert.DoesNotContain("malformed-token", copied);
        Assert.DoesNotContain("backslash-user", copied);
        Assert.DoesNotContain("backslash-password", copied);
        Assert.DoesNotContain("backslash-secret", copied);
        Assert.DoesNotContain("mixed-user", copied);
        Assert.DoesNotContain("mixed-password", copied);
        Assert.DoesNotContain("mixed-secret", copied);
        Assert.DoesNotContain("zero-slash-secret", copied);
        Assert.DoesNotContain("mixed-encoded-password", copied);
        Assert.DoesNotContain("mixed-encoded-zero-secret", copied);
        Assert.DoesNotContain("mixed-encoded-fragment", copied);
        Assert.DoesNotContain("mixed-slash-user", copied);
        Assert.DoesNotContain("mixed-slash-password", copied);
        Assert.DoesNotContain("mixed-slash-encoded-secret", copied);
        Assert.DoesNotContain("mixed-slash-fragment", copied);
        Assert.DoesNotContain("tabbed-key-secret", copied);
        Assert.DoesNotContain("glued-user", copied);
        Assert.DoesNotContain("glued-password", copied);
        Assert.DoesNotContain("glued-secret", copied);
        Assert.DoesNotContain("wrapper-user", copied);
        Assert.DoesNotContain("wrapper-password", copied);
        Assert.DoesNotContain("wrapper-secret", copied);
        Assert.DoesNotContain("quoted-secret", copied);
        Assert.DoesNotContain("wrapped-punctuation-secret", copied);
        Assert.DoesNotContain("parenthesized-path-secret", copied);
        Assert.DoesNotContain("parenthesized-fragment", copied);
        Assert.DoesNotContain("ipv6-secret", copied);
        Assert.DoesNotContain("ipv6-fragment", copied);
        Assert.DoesNotContain("encoded-structure-secret", copied);
        Assert.DoesNotContain("query-key-user", copied);
        Assert.DoesNotContain("query-key-password", copied);
        Assert.DoesNotContain("bare-key", copied);
        Assert.DoesNotContain("subscription-secret", copied);
        Assert.DoesNotContain("camel-subscription-secret", copied);
        Assert.DoesNotContain("provider-signature", copied);
        Assert.DoesNotContain("camel-provider-signature", copied);
        Assert.DoesNotContain("provider-credential", copied);
        Assert.DoesNotContain("hmac-secret", copied);
        Assert.DoesNotContain("hdnts-secret", copied);
        Assert.DoesNotContain("hdnea-secret", copied);
        Assert.DoesNotContain("encoded-query-key-secret", copied);
        Assert.DoesNotContain("deeply-encoded-key-secret", copied);
        Assert.DoesNotContain("path-session-secret", copied);
        Assert.DoesNotContain("query-session-secret", copied);
        Assert.DoesNotContain("cas-ticket-secret", copied);
        Assert.DoesNotContain("firebase-code-secret", copied);
        Assert.DoesNotContain("saml-artifact-secret", copied);
        Assert.DoesNotContain("saml-response-secret", copied);
        Assert.DoesNotContain("punctuation-secret", copied);
        Assert.DoesNotContain("near-limit-secret", copied);
        Assert.Contains("monkey=banana", copied);
        Assert.Contains("(https://example.test/callback?code=<redacted>).", copied);
        Assert.Contains("Prefix (https://example.test/path?code=<redacted>).Next", copied);
        Assert.Contains("https://example.test/path?sig=\\\"<redacted>\\\"", copied);
        Assert.Contains("url=https%3A%2F%2Fsafe.test%2Fp%3Fa%3D1%26sig%3D%3Credacted%3E&mode=raw", copied);
        Assert.Contains("https://example.test/search?q=hello%20world&tag=~value&tag=two).", copied);
        Assert.True(copied.Length < 20000, $"Expected bounded Data context, got {copied.Length} characters.");
    }

    [LiveInspectorFact]
    public async Task DataContextLogsCaptureNewestEntries()
    {
        await CaptureClipboardWritesAsync();
        var logs = Enumerable.Range(0, 200)
            .Select(index => new { l = "Info", m = $"log-{index:D3}" })
            .ToArray();
        await _page.RouteAsync("**/api/logs", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = System.Text.Json.JsonSerializer.Serialize(new { ok = true, logs }),
        }));

        await OpenDataDockAsync();
        await Expect(_page.Locator("#df-attach-data")).ToBeEnabledAsync();
        await _page.Locator("#df-attach-data").ClickAsync();

        var copied = await _page.EvaluateAsync<string>("() => window.__copiedDevFlowData || ''");
        Assert.Contains("newest 100 of 200", copied);
        Assert.Contains("log-000", copied);
        Assert.Contains("log-099", copied);
        Assert.DoesNotContain("log-100", copied);
        Assert.DoesNotContain("log-199", copied);
    }

    [LiveInspectorFact]
    public async Task HostedDataAttachmentWaitsForHostAcknowledgement()
    {
        await _page.RouteAsync("**/api/device", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "application/json",
            Body = """{"ok":true,"device":{"device-info":{"platform":"WinUI"}}}""",
        }));
        await _page.SetViewportSizeAsync(1100, 800);
        var trustedEmbedUrl = TrustedEmbedUrl();
        await _page.SetContentAsync($$"""
            <iframe id="hosted" style="width:1000px;height:700px;border:0" src="{{trustedEmbedUrl}}#devflowBridge=test-bridge"></iframe>
            <script>
              window.__attachRequests = 0;
              window.addEventListener('message', function (e) {
                const d = e.data;
                if (!d || d.bridgeId !== 'test-bridge') return;
                if (d.type === 'devflow:ready') {
                  e.source.postMessage({
                    type: 'devflow:host',
                    v: 1,
                    bridgeId: 'test-bridge',
                    hostKind: 'test-host',
                    capabilities: ['attachData']
                  }, '*');
                } else if (d.type === 'devflow:attachData') {
                  window.__attachRequests++;
                  setTimeout(function () {
                    e.source.postMessage({
                      type: 'devflow:hostResult',
                      v: 1,
                      bridgeId: 'test-bridge',
                      requestId: d.requestId,
                      ok: false,
                      error: 'Host rejected snapshot.'
                    }, '*');
                  }, 800);
                }
              });
            </script>
            """);

        var frame = _page.FrameLocator("#hosted");
        await Expect(frame.Locator(".devflow-element").First).ToBeAttachedAsync();
        await Expect(frame.Locator("body")).ToHaveAttributeAsync("data-host-kind", "test-host");
        await frame.Locator("#df-more").ClickAsync();
        await frame.Locator("#df-toggle-dock").ClickAsync();
        await frame.Locator("[data-tab='device']").ClickAsync();
        await Expect(frame.Locator("#df-attach-data")).ToBeEnabledAsync();
        await frame.Locator("#df-attach-data").ClickAsync();
        await Expect(frame.Locator("#df-status")).ToContainTextAsync("Adding Device snapshot to Copilot");
        await Expect(frame.Locator("#df-status")).ToHaveTextAsync("Host rejected snapshot.", new() { Timeout = 3000 });
        Assert.Equal(1, await _page.EvaluateAsync<int>("() => window.__attachRequests"));
    }

    private string TrustedEmbedUrl()
    {
        var brokerStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mauidevflow",
            "broker.json");
        Assert.True(File.Exists(brokerStatePath), $"Broker state not found at {brokerStatePath}");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(brokerStatePath));
        Assert.True(document.RootElement.TryGetProperty("embedToken", out var tokenElement));
        var token = tokenElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(token), "Broker embed token is missing.");
        var separator = BaseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return BaseUrl + separator + "embed=" + Uri.EscapeDataString(token);
    }

    private Task CaptureClipboardWritesAsync() =>
        _page.AddInitScriptAsync("""
            window.__copiedDevFlowData = "";
            Object.defineProperty(navigator, "clipboard", {
              configurable: true,
              value: { writeText: async text => { window.__copiedDevFlowData = text; } }
            });
            """);

    private static string EncodeUrlLayers(string value, int layers)
    {
        for (var layer = 0; layer < layers; layer++)
            value = Uri.EscapeDataString(value);
        return value;
    }

    private static string EncodeQueryKeyLayers(string value, int layers)
    {
        value = string.Concat(value.Select(character => $"%{(int)character:X2}"));
        for (var layer = 1; layer < layers; layer++)
            value = Uri.EscapeDataString(value);
        return value;
    }

    private async Task OpenDataDockAsync()
    {
        await _page.SetViewportSizeAsync(700, 700);
        await _page.GotoAsync(BaseUrl);
        await Expect(_page.Locator(".devflow-element").First).ToBeAttachedAsync();
        await _page.Locator("#df-more").ClickAsync();
        await _page.Locator("#df-toggle-dock").ClickAsync();
        await Expect(_page.Locator("#df-dock")).ToBeVisibleAsync();
    }

    private ILocatorAssertions Expect(ILocator locator) =>
        Assertions.Expect(locator);
}
