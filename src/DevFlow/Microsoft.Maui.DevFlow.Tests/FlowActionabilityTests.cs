using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public class FlowActionabilityTests
{
    [Fact]
    public async Task ResolveAsync_ScopedStableItemSelector_ResolvesRepeatedControl()
    {
        var firstKey = "sha256:" + new string('1', 64);
        var secondKey = "sha256:" + new string('2', 64);
        var driver = new RepeatedItemDriver(
        [
            new ElementInfo
            {
                Id = "first",
                Type = "CheckBox",
                AutomationId = "TodoCheckBox",
                StableItemKey = firstKey,
                CollectionScope = "TodoList",
            },
            new ElementInfo
            {
                Id = "second",
                Type = "CheckBox",
                AutomationId = "TodoCheckBox",
                StableItemKey = secondKey,
                CollectionScope = "TodoList",
            },
        ]);
        var engine = new FlowActionabilityEngine(driver, tries: 1, gapMs: 0);

        var ambiguous = await engine.ResolveAsync(new FlowSelector { AutomationId = "TodoCheckBox" });
        Assert.False(ambiguous.Ok);
        Assert.Equal(FlowFailureKinds.Ambiguous, ambiguous.Kind);

        var resolved = await engine.ResolveAsync(new FlowSelector
        {
            AutomationId = "TodoCheckBox",
            StableItemKey = secondKey,
            CollectionScope = "TodoList",
        });

        Assert.True(resolved.Ok);
        Assert.Equal("second", resolved.Element?.Id);
        Assert.Equal("stable-item-key", resolved.Quality);
        Assert.Equal(1, resolved.MatchCount);
    }

    private sealed class RepeatedItemDriver(IReadOnlyList<ElementInfo> elements) : IMauiFlowDriver
    {
        public WorkflowCommandReceipt? LastWorkflowCommandReceipt => null;

        public Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
            => Task.FromResult(elements.Where(element =>
                (type is null || element.Type == type) &&
                (automationId is null || element.AutomationId == automationId) &&
                (text is null || element.Text == text)).ToList());

        public Task<ElementInfo?> GetElementAsync(string id)
            => Task.FromResult(elements.FirstOrDefault(element => element.Id == id));

        public Task<bool> TapAsync(string elementId) => Task.FromResult(true);
        public Task<bool> FillAsync(string elementId, string text) => Task.FromResult(true);
        public Task<bool> SetPropertyAsync(string elementId, string propertyName, string value) => Task.FromResult(true);
        public Task<bool> ScrollAsync(string? elementId = null, double deltaX = 0, double deltaY = 0, bool animated = true, int? itemIndex = null, string? scrollToPosition = null) => Task.FromResult(true);
        public Task<bool> NavigateAsync(string route) => Task.FromResult(true);
        public Task<bool> BackAsync() => Task.FromResult(true);
        public Task<ThemeResult> SetThemeAsync(DevFlowTheme theme) => Task.FromResult(new ThemeResult { Success = true });
        public Task<string?> GetPropertyAsync(string elementId, string propertyName) => Task.FromResult<string?>(null);
        public Task<AgentStatus?> GetStatusAsync() => Task.FromResult<AgentStatus?>(null);
    }
}
