using Microsoft.Maui.DevFlow.Driver;
using System.Globalization;

namespace Microsoft.Maui.Cli.DevFlow.Flows;

public sealed class FlowAssertResult
{
    public string Kind { get; set; } = "";
    /// <summary>true = passed, false = failed, null = not evaluated (report-only or skipped).</summary>
    public bool? Ok { get; set; }
    public bool Skipped { get; set; }
    public string? Name { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
}

public sealed class FlowStepResult
{
    public int Seq { get; set; }
    public string Action { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? FailureKind { get; set; }
    public int? MatchCount { get; set; }
    public string? SelectorQuality { get; set; }
    public List<FlowAssertResult> Asserts { get; set; } = new();
}

public sealed class FlowReplayReport
{
    public bool Ok { get; set; }
    public string Name { get; set; } = "scenario";
    public string? File { get; set; }
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int? DivergencePoint { get; set; }
    public bool StoppedEarly { get; set; }
    public bool EvidenceAvailable { get; set; }
    public string? EvidencePath { get; set; }
    public List<FlowStepResult> Results { get; set; } = new();
}

/// <summary>
/// Replays a recorded <see cref="MauiFlow"/> against a running app via <see cref="AgentClient"/>,
/// producing a per-step pass/fail report. Uses durable-selector resolution, drive-then-verify,
/// and polling of hard assertions (propEquals/exists) with re-resolution each
/// attempt to tolerate async navigation. A failed drive short-circuits that step's assertions.
/// </summary>
public sealed class FlowReplayer
{
    private readonly AgentClient _agent;
    private readonly int _pollTries;
    private readonly int _pollGapMs;
    private readonly FlowActionabilityEngine _actionability;
    private readonly bool _continueOnFailure;
    private readonly IFlowReplayEvidenceCapture? _evidenceCapture;

    public FlowReplayer(
        AgentClient agent,
        int pollTries = 4,
        int pollGapMs = 300,
        bool continueOnFailure = false,
        IFlowReplayEvidenceCapture? evidenceCapture = null)
    {
        _agent = agent;
        _pollTries = Math.Max(1, pollTries);
        _pollGapMs = Math.Max(0, pollGapMs);
        _actionability = new FlowActionabilityEngine(agent, _pollTries, _pollGapMs);
        _continueOnFailure = continueOnFailure;
        _evidenceCapture = evidenceCapture;
    }

    public async Task<FlowReplayReport> ReplayAsync(MauiFlow flow, string? file = null, CancellationToken ct = default)
    {
        var report = new FlowReplayReport { Name = flow.Name, File = file, Total = flow.Steps.Count };

        foreach (var step in flow.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var res = new FlowStepResult { Seq = step.Seq, Action = step.Action, Label = FlowMarkdown.Label(step) };

            var drive = await DriveAsync(step, ct);
            res.MatchCount = drive.MatchCount;
            res.SelectorQuality = drive.SelectorQuality;
            if (!drive.Ok)
            {
                // Causality: don't run assertions when the action itself didn't happen.
                res.Ok = false;
                res.Error = drive.Error;
                res.FailureKind = drive.Kind;
                foreach (var a in step.Asserts ?? Enumerable.Empty<FlowAssert>())
                    res.Asserts.Add(new FlowAssertResult { Kind = a.Kind, Skipped = true, Name = a.Name, Expected = a.Expected });
            }
            else
            {
                res.Ok = true;
                foreach (var a in step.Asserts ?? Enumerable.Empty<FlowAssert>())
                {
                    if (!a.Verify)
                    {
                        res.Asserts.Add(new FlowAssertResult { Kind = a.Kind, Skipped = true, Name = a.Name, Expected = a.Expected });
                        continue;
                    }
                    var ar = await VerifyAsync(a, ct);
                    res.Asserts.Add(ar);
                    if (ar.Ok == false)
                    {
                        res.Ok = false;
                        res.FailureKind ??= FlowFailureKinds.Assertion;
                        res.Error ??= $"{a.Kind} assertion failed.";
                    }
                }
            }

            if (res.Ok) report.Passed++;
            else report.Failed++;
            report.Results.Add(res);
            if (!res.Ok)
            {
                report.DivergencePoint ??= step.Seq;
                if (_evidenceCapture is not null)
                {
                    try { await _evidenceCapture.CaptureOnFailureAsync(flow, step, res, ct); }
                    catch { /* evidence must never mask the replay failure */ }
                }
                if (!_continueOnFailure)
                {
                    report.StoppedEarly = true;
                    break;
                }
            }
        }

        report.Ok = report.Failed == 0;
        return report;
    }

    private async Task<DriveResult> DriveAsync(FlowStep step, CancellationToken ct)
    {
        try
        {
            var args = step.Args;
            switch (step.Action)
            {
                case FlowActions.Tap:
                {
                    var target = await _actionability.WaitForActionableAsync(FlowValidator.EffectiveSelector(step), true, ct);
                    if (!target.Ok) return DriveResult.FromTarget("tap", target);
                    return await _agent.TapAsync(target.Element!.Id) ? DriveResult.Success(target) : DriveResult.Failure(FlowFailureKinds.Drive, "tap reported failure", target);
                }
                case FlowActions.Fill:
                {
                    var target = await _actionability.WaitForActionableAsync(FlowValidator.EffectiveSelector(step), false, ct);
                    if (!target.Ok) return DriveResult.FromTarget("fill", target);
                    return await _agent.FillAsync(target.Element!.Id, args?.Text ?? step.Value ?? "") ? DriveResult.Success(target) : DriveResult.Failure(FlowFailureKinds.Drive, "fill reported failure", target);
                }
                case FlowActions.SetProperty:
                {
                    var target = await _actionability.WaitForActionableAsync(FlowValidator.EffectiveSelector(step), false, ct);
                    if (!target.Ok) return DriveResult.FromTarget("setProperty", target);
                    if (IsUnsafeValueSource(args?.ValueSource))
                        return DriveResult.Failure(FlowFailureKinds.UnsafeValue, "setProperty value came from an unsafe binding/resource source and cannot be replayed.", target);
                    var name = string.IsNullOrEmpty(args?.Name) ? "Text" : args!.Name!;
                    return await _agent.SetPropertyAsync(target.Element!.Id, name, args?.Value ?? step.Value ?? "")
                        ? DriveResult.Success(target) : DriveResult.Failure(FlowFailureKinds.Drive, "setProperty reported failure", target);
                }
                case FlowActions.Scroll:
                {
                    string? id = null;
                    var sel = FlowValidator.EffectiveSelector(step);
                    if (sel is not null && !sel.IsEmpty)
                    {
                        var target = await _actionability.WaitForActionableAsync(sel, false, ct);
                        if (!target.Ok) return DriveResult.FromTarget("scroll", target);
                        id = target.Element!.Id;
                    }
                    var ok = await _agent.ScrollAsync(
                        elementId: id,
                        deltaX: args?.Dx ?? 0,
                        deltaY: args?.Dy ?? 0,
                        animated: args?.Animated ?? false,
                        itemIndex: args?.ItemIndex,
                        scrollToPosition: args?.Position);
                    return ok ? DriveResult.Success() : DriveResult.Failure(FlowFailureKinds.Drive, "scroll reported failure");
                }
                case FlowActions.Navigate:
                {
                    var route = args?.Route ?? step.Value;
                    if (string.IsNullOrEmpty(route)) return DriveResult.Failure(FlowFailureKinds.Drive, "navigate requires a route");
                    return await _agent.NavigateAsync(route) ? DriveResult.Success() : DriveResult.Failure(FlowFailureKinds.Drive, "navigate reported failure");
                }
                case FlowActions.Back:
                    return await _agent.BackAsync() ? DriveResult.Success() : DriveResult.Failure(FlowFailureKinds.Drive, "back reported failure");
                case FlowActions.Assert:
                    // Validation-only step: nothing to drive, so the step's assertions run next.
                    return DriveResult.Success();
                case FlowActions.SetTheme:
                {
                    var themeStr = args?.Theme ?? step.Value;
                    if (!TryParseTheme(themeStr, out var theme))
                        return DriveResult.Failure(FlowFailureKinds.Drive, $"setTheme requires light|dark|system (got '{themeStr ?? "(none)"}')");
                    var r = await _agent.SetThemeAsync(theme);
                    return (r?.Success ?? true) ? DriveResult.Success() : DriveResult.Failure(FlowFailureKinds.Drive, r?.Message ?? "setTheme reported failure");
                }
                default:
                    return DriveResult.Failure(FlowFailureKinds.Drive, $"unknown action: {step.Action}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DriveResult.Failure(FlowFailureKinds.Drive, $"drive failed: {ex.Message}");
        }
    }

    private async Task<FlowAssertResult> VerifyAsync(FlowAssert a, CancellationToken ct)
    {
        var r = new FlowAssertResult { Kind = a.Kind, Name = a.Name, Expected = a.Expected };
        for (var attempt = 0; attempt < _pollTries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (a.Kind == "propEquals")
                {
                    // Re-resolve every attempt — the element id can change across a navigation.
                    var id = await ResolveToIdAsync(a.Selector, ct);
                    if (id is not null)
                    {
                        var val = await _agent.GetPropertyAsync(id, string.IsNullOrEmpty(a.Name) ? "Text" : a.Name!);
                        r.Actual = val;
                        if (PropertyValuesEqual(val, a.Expected))
                        {
                            r.Ok = true;
                            return r;
                        }
                    }
                }
                else if (a.Kind == "exists")
                {
                    var id = await ResolveToIdAsync(a.Selector, ct);
                    if (id is not null)
                    {
                        r.Ok = true;
                        return r;
                    }
                }
                else if (a.Kind == "routeIs")
                {
                    var route = (await _agent.GetStatusAsync())?.Route;
                    r.Actual = route;
                    if (string.Equals(route, a.Expected, StringComparison.Ordinal))
                    {
                        r.Ok = true;
                        return r;
                    }
                }
                else
                {
                    // Unknown verify:true kind — don't fail the step on something we can't evaluate.
                    r.Skipped = true;
                    return r;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // fall through to retry
            }
            if (attempt < _pollTries - 1)
                await Task.Delay(_pollGapMs, ct);
        }
        r.Ok = false;
        return r;
    }

    internal static bool PropertyValuesEqual(string? actual, string? expected)
    {
        var left = (actual ?? string.Empty).Trim();
        var right = (expected ?? string.Empty).Trim();
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;

        if (bool.TryParse(left, out var leftBool) &&
            bool.TryParse(right, out var rightBool))
            return leftBool == rightBool;

        if (decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftNumber) &&
            decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightNumber))
            return leftNumber == rightNumber;

        if (!TryNormalizeReadColor(left, out var actualRgba) ||
            !TryNormalizeExpectedColor(right, out var expectedRgba, out var expectedArgbAsRgba))
            return false;

        return string.Equals(actualRgba, expectedRgba, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(actualRgba, expectedArgbAsRgba, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeReadColor(string value, out string rgba)
    {
        rgba = string.Empty;
        if (!value.StartsWith('#')) return false;
        var hex = value[1..];
        if (!hex.All(Uri.IsHexDigit)) return false;
        if (hex.Length == 6) rgba = hex + "FF";
        else if (hex.Length == 8) rgba = hex;
        else return false;
        return true;
    }

    private static bool TryNormalizeExpectedColor(string value, out string rgba, out string argbAsRgba)
    {
        rgba = string.Empty;
        argbAsRgba = string.Empty;
        if (!value.StartsWith('#')) return false;
        var hex = value[1..];
        if (!hex.All(Uri.IsHexDigit)) return false;
        if (hex.Length == 6)
        {
            rgba = hex + "FF";
            argbAsRgba = rgba;
            return true;
        }
        if (hex.Length != 8) return false;
        rgba = hex;
        argbAsRgba = hex[2..] + hex[..2];
        return true;
    }

    private async Task<string?> ResolveDriveTargetAsync(FlowSelector? selector, CancellationToken ct)
    {
        for (var attempt = 0; attempt < _pollTries; attempt++)
        {
            var id = await ResolveToIdAsync(selector, ct);
            if (id is not null)
                return id;
            if (attempt < _pollTries - 1)
                await Task.Delay(_pollGapMs, ct);
        }
        return null;
    }

    private async Task<string?> ResolveToIdAsync(FlowSelector? selector, CancellationToken ct)
    {
        var resolution = await _actionability.ResolveAsync(selector, ct);
        return resolution.Ok ? resolution.Element!.Id : null;
    }

    private static bool TryParseTheme(string? s, out DevFlowTheme theme)
    {
        theme = DevFlowTheme.System;
        if (string.IsNullOrWhiteSpace(s)) return false;
        switch (s.Trim().ToLowerInvariant())
        {
            case "light": theme = DevFlowTheme.Light; return true;
            case "dark": theme = DevFlowTheme.Dark; return true;
            case "system": theme = DevFlowTheme.System; return true;
            default: return false;
        }
    }

    private static bool IsUnsafeValueSource(string? source)
        => source is not null &&
           (source.Contains("binding", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("resource", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("trigger", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("unsafe", StringComparison.OrdinalIgnoreCase));

    private sealed record DriveResult(
        bool Ok,
        string? Kind = null,
        string? Error = null,
        int? MatchCount = null,
        string? SelectorQuality = null)
    {
        public static DriveResult Success(FlowTargetResolution? target = null)
            => new(true, MatchCount: target?.MatchCount, SelectorQuality: target?.Quality);

        public static DriveResult Failure(string kind, string error, FlowTargetResolution? target = null)
            => new(false, kind, error, target?.MatchCount, target?.Quality);

        public static DriveResult FromTarget(string action, FlowTargetResolution target)
            => Failure(
                target.Kind,
                $"{action} target could not be resolved: {target.Error}",
                target);
    }
}

/// <summary>Optional callback that callers can use to collect privacy-safe evidence after failure.</summary>
public interface IFlowReplayEvidenceCapture
{
    Task CaptureOnFailureAsync(MauiFlow flow, FlowStep failedStep, FlowStepResult result, CancellationToken cancellationToken);
}
