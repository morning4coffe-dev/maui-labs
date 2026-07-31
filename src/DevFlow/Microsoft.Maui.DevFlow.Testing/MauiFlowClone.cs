using System.Text.Json;

namespace Microsoft.Maui.DevFlow.Testing;

internal static class MauiFlowClone
{
    public static MauiFlow Clone(MauiFlow source) => new()
    {
        Schema = source.Schema,
        Name = source.Name,
        App = source.App,
        Platform = source.Platform,
        RecordedAt = source.RecordedAt,
        Preconditions = source.Preconditions,
        Steps = source.Steps.Select(CloneStep).ToList(),
        ExtensionData = CloneExtensions(source.ExtensionData),
    };

    private static FlowStep CloneStep(FlowStep step) => new()
    {
        Seq = step.Seq,
        Action = step.Action,
        Target = CloneSelector(step.Target),
        Value = step.Value,
        Args = step.Args is null ? null : new FlowStepArgs
        {
            Selector = CloneSelector(step.Args.Selector),
            Text = step.Args.Text,
            Name = step.Args.Name,
            Value = step.Args.Value,
            Route = step.Args.Route,
            Theme = step.Args.Theme,
            ValueSource = step.Args.ValueSource,
            SecretEnvironmentVariable = step.Args.SecretEnvironmentVariable,
            Element = step.Args.Element,
            Dx = step.Args.Dx,
            Dy = step.Args.Dy,
            ItemIndex = step.Args.ItemIndex,
            Position = step.Args.Position,
            Animated = step.Args.Animated,
            ExtensionData = CloneExtensions(step.Args.ExtensionData),
        },
        Page = step.Page,
        Navigated = step.Navigated,
        Fragile = step.Fragile,
        Screenshot = step.Screenshot,
        Asserts = step.Asserts?.Select(assertion => new FlowAssert
        {
            Kind = assertion.Kind,
            Selector = CloneSelector(assertion.Selector),
            Name = assertion.Name,
            Expected = assertion.Expected,
            Verify = assertion.Verify,
            Note = assertion.Note,
            ExtensionData = CloneExtensions(assertion.ExtensionData),
        }).ToList(),
        ExtensionData = CloneExtensions(step.ExtensionData),
    };

    private static FlowSelector? CloneSelector(FlowSelector? selector) => selector is null ? null : new FlowSelector
    {
        AutomationId = selector.AutomationId,
        Text = selector.Text,
        Id = selector.Id,
        Type = selector.Type,
        Index = selector.Index,
        SelectorKind = selector.SelectorKind,
        MatchCount = selector.MatchCount,
        Quality = selector.Quality,
        FragilityReasons = selector.FragilityReasons is null ? null : new List<string>(selector.FragilityReasons),
        TypeIndex = selector.TypeIndex is null ? null : new FlowTypeIndex
        {
            Type = selector.TypeIndex.Type,
            Index = selector.TypeIndex.Index,
            ExtensionData = CloneExtensions(selector.TypeIndex.ExtensionData),
        },
        ExtensionData = CloneExtensions(selector.ExtensionData),
    };

    private static Dictionary<string, JsonElement>? CloneExtensions(Dictionary<string, JsonElement>? extensions)
    {
        if (extensions is null)
            return null;

        var clone = new Dictionary<string, JsonElement>(extensions.Count, StringComparer.Ordinal);
        foreach (var (name, value) in extensions)
            clone[name] = value.Clone();

        return clone;
    }
}
