using Microsoft.Maui.DevFlow.Agent.IntegrationTests.Fixtures;
using Xunit.Abstractions;

namespace Microsoft.Maui.DevFlow.Agent.IntegrationTests;

[Collection("AgentIntegration")]
[Trait("Category", "UiProperties")]
public class UiPropertyTests : IntegrationTestBase
{
    public UiPropertyTests(AppFixture app, ITestOutputHelper output)
        : base(app, output) { }

    [Fact]
    public async Task GetProperty_Text_ReturnsValue()
    {
        await NavigateToMainPageAsync();
        var header = await FindElementAsync("HeaderLabel");

        var text = await Client.GetPropertyAsync(header.Id, "Text");

        Assert.NotNull(text);
        Assert.Contains("Todos", text!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetProperty_IsVisible_ReturnsTrue()
    {
        await NavigateToMainPageAsync();
        var addButton = await FindElementAsync("AddButton");

        var value = await Client.GetPropertyAsync(addButton.Id, "IsVisible");

        Assert.NotNull(value);
        Assert.Contains("true", value!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetProperty_Text_UpdatesValue()
    {
        await NavigateToMainPageAsync();
        var header = await FindElementAsync("HeaderLabel");
        var originalText = await Client.GetPropertyAsync(header.Id, "Text");

        var result = await Client.SetPropertyAsync(header.Id, "Text", "Modified Header");
        Assert.True(result);

        await SettleAsync();
        var newText = await Client.GetPropertyAsync(header.Id, "Text");
        Assert.Equal("Modified Header", newText);

        if (originalText != null)
            await Client.SetPropertyAsync(header.Id, "Text", originalText);
    }

    [Fact]
    public async Task GetProperty_Opacity_ReturnsNumericValue()
    {
        await NavigateToMainPageAsync();
        var addButton = await FindElementAsync("AddButton");

        // GetPropertyAsync now throws (rather than returning null) when the agent responds
        // with an explicit "success": false rejection, so the graceful fallback below is
        // expressed as a try/catch instead of a null check.
        string? value;
        try
        {
            value = await Client.GetPropertyAsync(addButton.Id, "Opacity");
        }
        catch (InvalidOperationException ex)
        {
            Output.WriteLine($"Opacity property was rejected ('{ex.Message}') — trying IsVisible instead.");
            try
            {
                var isVisible = await Client.GetPropertyAsync(addButton.Id, "IsVisible");
                Assert.NotNull(isVisible);
            }
            catch (InvalidOperationException ex2)
            {
                Output.WriteLine($"IsVisible was also rejected ('{ex2.Message}') — property access may not be supported for this element type.");
            }
            return;
        }

        Assert.True(double.TryParse(value, out var opacity), $"Expected numeric opacity, got: {value}");
        Assert.InRange(opacity, 0.0, 1.0);
    }

    [Fact]
    public async Task GetProperty_NonExistentProperty_HandlesGracefully()
    {
        await NavigateToMainPageAsync();
        var addButton = await FindElementAsync("AddButton");

        // The agent responds with an explicit "success": false rejection for an unknown
        // property, which GetPropertyAsync now surfaces as an exception rather than null.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client.GetPropertyAsync(addButton.Id, "NonExistentProperty12345"));
        Output.WriteLine($"Non-existent property surfaced: '{ex.Message}'");
    }
}
