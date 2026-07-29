using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.Cli.DevFlow.Flows;

/// <summary>
/// Shared, capability-honest actionability checks for workflow drivers. It checks only what every
/// agent reports (resolution, visibility, enabled state and bounds); it deliberately makes no
/// cross-platform claim about occlusion.
/// </summary>
public sealed class FlowActionabilityEngine
{
    private readonly AgentClient _agent;
    private readonly int _tries;
    private readonly int _gapMs;

    public FlowActionabilityEngine(AgentClient agent, int tries = 4, int gapMs = 300)
    {
        _agent = agent;
        _tries = Math.Max(1, tries);
        _gapMs = Math.Max(0, gapMs);
    }

    public async Task<FlowTargetResolution> WaitForActionableAsync(
        FlowSelector? selector,
        bool requireStableBounds,
        CancellationToken cancellationToken = default)
    {
        FlowTargetResolution? last = null;
        for (var attempt = 0; attempt < _tries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = await ResolveAsync(selector, cancellationToken);
            last = resolution;
            if (!resolution.Ok)
            {
                if (resolution.Kind == FlowFailureKinds.Ambiguous)
                    return resolution;
            }
            else if (!resolution.Element!.IsVisible)
            {
                last = resolution.WithFailure(FlowFailureKinds.NotVisible, "The resolved element is not visible.");
            }
            else if (!resolution.Element.IsEnabled)
            {
                last = resolution.WithFailure(FlowFailureKinds.Disabled, "The resolved element is disabled.");
            }
            else if (requireStableBounds)
            {
                var bounds = resolution.Element.Bounds;
                if (bounds is null || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    last = resolution.WithFailure(FlowFailureKinds.Unstable, "The tap target has empty or non-positive bounds.");
                }
                else
                {
                    await Task.Delay(Math.Max(40, Math.Min(_gapMs, 150)), cancellationToken);
                    var second = await _agent.GetElementAsync(resolution.Element.Id);
                    if (second is not null && SameBounds(bounds, second.Bounds))
                        return resolution;
                    last = resolution.WithFailure(
                        FlowFailureKinds.Unstable,
                        "The tap target bounds changed while waiting for a stable layout.");
                }
            }
            else
            {
                return resolution;
            }

            if (attempt < _tries - 1)
                await Task.Delay(_gapMs, cancellationToken);
        }
        return last ?? FlowTargetResolution.Failure(FlowFailureKinds.NotFound, "No target selector was supplied.");
    }

    public async Task<FlowTargetResolution> ResolveAsync(FlowSelector? selector, CancellationToken cancellationToken = default)
    {
        if (selector is null || selector.IsEmpty)
            return FlowTargetResolution.Failure(FlowFailureKinds.NotFound, "No target selector was supplied.");
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ElementInfo> matches;
        if (!string.IsNullOrEmpty(selector.AutomationId))
        {
            matches = await _agent.QueryAsync(automationId: selector.AutomationId);
            return Exact(matches, "AutomationId");
        }
        if (!string.IsNullOrEmpty(selector.Text))
        {
            matches = (await _agent.QueryAsync(text: selector.Text))
                .Where(element => string.Equals(element.Text, selector.Text, StringComparison.Ordinal))
                .ToArray();
            return Exact(matches, "text");
        }
        var typeIndex = selector.TypeIndex ??
            (selector.SelectorKind == "typeIndex" && !string.IsNullOrEmpty(selector.Type) && selector.Index is not null
                ? new FlowTypeIndex { Type = selector.Type, Index = selector.Index.Value }
                : null);
        if (typeIndex is not null && !string.IsNullOrEmpty(typeIndex.Type))
        {
            matches = await _agent.QueryAsync(type: typeIndex.Type);
            if (typeIndex.Index < 0 || typeIndex.Index >= matches.Count)
                return FlowTargetResolution.Failure(FlowFailureKinds.NotFound, $"Type+index selector found {matches.Count} candidate(s), not index {typeIndex.Index}.", matches);
            return FlowTargetResolution.Success(matches[typeIndex.Index], matches.Count, "fragile");
        }
        if (!string.IsNullOrEmpty(selector.Id))
        {
            var element = await _agent.GetElementAsync(selector.Id);
            return element is null
                ? FlowTargetResolution.Failure(FlowFailureKinds.NotFound, "Raw element id was not found.")
                : FlowTargetResolution.Success(element, 1, "fragile");
        }
        return FlowTargetResolution.Failure(FlowFailureKinds.NotFound, "No usable target selector was supplied.");
    }

    private static FlowTargetResolution Exact(IReadOnlyList<ElementInfo> matches, string selectorKind)
    {
        if (matches.Count == 1)
            return FlowTargetResolution.Success(matches[0], 1, selectorKind);
        if (matches.Count == 0)
            return FlowTargetResolution.Failure(FlowFailureKinds.NotFound, $"{selectorKind} selector found no elements.", matches);
        return FlowTargetResolution.Failure(
            FlowFailureKinds.Ambiguous,
            $"{selectorKind} selector is ambiguous: {matches.Count} elements matched ({CandidateSummary(matches)}).",
            matches);
    }

    private static string CandidateSummary(IReadOnlyList<ElementInfo> matches)
        => string.Join(", ", matches.Take(3).Select(static element =>
            string.IsNullOrWhiteSpace(element.AutomationId)
                ? $"{element.Type}#{element.Id}"
                : $"{element.Type}[{element.AutomationId}]"));

    private static bool SameBounds(BoundsInfo first, BoundsInfo? second)
        => second is not null &&
           first.X == second.X && first.Y == second.Y &&
           first.Width == second.Width && first.Height == second.Height;
}

public static class FlowFailureKinds
{
    public const string NotFound = "not-found";
    public const string Ambiguous = "ambiguous";
    public const string NotVisible = "not-visible";
    public const string Disabled = "disabled";
    public const string Unstable = "unstable";
    public const string UnsafeValue = "unsafe-value";
    public const string Drive = "drive";
    public const string Assertion = "assertion";
}

public sealed class FlowTargetResolution
{
    public bool Ok { get; init; }
    public string Kind { get; init; } = FlowFailureKinds.NotFound;
    public string? Error { get; init; }
    public ElementInfo? Element { get; init; }
    public int MatchCount { get; init; }
    public string? Quality { get; init; }
    public IReadOnlyList<ElementInfo> Candidates { get; init; } = [];

    public static FlowTargetResolution Success(ElementInfo element, int count, string quality)
        => new() { Ok = true, Element = element, MatchCount = count, Quality = quality, Candidates = [element] };

    public static FlowTargetResolution Failure(string kind, string error, IReadOnlyList<ElementInfo>? candidates = null)
        => new() { Kind = kind, Error = error, MatchCount = candidates?.Count ?? 0, Candidates = candidates ?? [] };

    public FlowTargetResolution WithFailure(string kind, string error)
        => new()
        {
            Kind = kind,
            Error = error,
            Element = Element,
            MatchCount = MatchCount,
            Quality = Quality,
            Candidates = Candidates
        };
}
