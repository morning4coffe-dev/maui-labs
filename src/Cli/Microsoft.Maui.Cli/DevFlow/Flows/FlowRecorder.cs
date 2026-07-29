namespace Microsoft.Maui.Cli.DevFlow.Flows;

/// <summary>
/// Accumulates a workflow recording in memory as a <see cref="MauiFlow"/>, one host-reported step
/// at a time. Pure (no agent dependency) and thread-safe, so interleaved MCP record calls are safe.
/// Recordings serialized via <see cref="FlowMarkdown"/> replay unchanged under
/// <see cref="FlowReplayer"/>.
/// </summary>
public sealed class FlowRecorder
{
    /// <summary>Hard cap on recorded steps, to bound memory for a runaway recording.</summary>
    public const int MaxSteps = 5000;

    private readonly object _gate = new();
    private readonly MauiFlow _flow;
    private readonly TimeProvider _clock;
    private int _seq;
    private bool _closed;

    public FlowRecorder(
        string name,
        string? app,
        string? platform,
        string? preconditions,
        TimeProvider? clock = null,
        DateTimeOffset? createdAtUtc = null,
        DateTimeOffset? lastTouchedUtc = null,
        MauiFlow? restoredFlow = null)
    {
        _clock = clock ?? TimeProvider.System;
        _flow = restoredFlow is null ? new MauiFlow
        {
            Name = string.IsNullOrWhiteSpace(name) ? "scenario" : name.Trim(),
            App = app,
            Platform = platform,
            Preconditions = preconditions,
        } : Clone(restoredFlow);
        _seq = _flow.Steps.Count == 0 ? 0 : _flow.Steps.Max(static step => step.Seq);
        CreatedAtUtc = createdAtUtc ?? _clock.GetUtcNow();
        LastTouchedUtc = lastTouchedUtc ?? CreatedAtUtc;
    }

    public string Name => _flow.Name;

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>When this recording was last started/appended/inspected; drives idle eviction.</summary>
    public DateTimeOffset LastTouchedUtc { get; private set; }

    /// <summary>Marks the recording as recently used (called when it is looked up).</summary>
    public void Touch()
    {
        lock (_gate) LastTouchedUtc = _clock.GetUtcNow();
    }

    public int StepCount
    {
        get { lock (_gate) return _flow.Steps.Count; }
    }

    /// <summary>
    /// Appends a host-reported step and returns its assigned <c>seq</c>, or <c>-1</c> if the step
    /// cap is reached. A step whose <paramref name="target"/> carries no AutomationId is flagged
    /// <c>fragile</c> (only an AutomationId is a durable selector).
    /// </summary>
    public int AppendStep(string action, FlowSelector? target, string? value, FlowStepArgs? args, string? page, bool navigated, List<FlowAssert>? asserts)
    {
        lock (_gate)
        {
            if (_closed || _flow.Steps.Count >= MaxSteps)
                return -1;

            LastTouchedUtc = _clock.GetUtcNow();
            var seq = ++_seq;
            _flow.Steps.Add(new FlowStep
            {
                Seq = seq,
                Action = action,
                Target = target,
                Value = value,
                Args = args,
                Page = page,
                Navigated = navigated,
                Fragile = FlowRecordTools.IsFragileSelector(target),
                Asserts = asserts is { Count: > 0 } ? asserts : null,
            });
            return seq;
        }
    }

    internal bool TryRollbackLastStep(int sequence)
    {
        lock (_gate)
        {
            if (_flow.Steps.Count == 0 || _flow.Steps[^1].Seq != sequence)
                return false;
            _flow.Steps.RemoveAt(_flow.Steps.Count - 1);
            _seq = _flow.Steps.Count == 0 ? 0 : _flow.Steps[^1].Seq;
            return true;
        }
    }

    /// <summary>Returns a snapshot copy of the flow so far, without stamping or closing it (for
    /// validation and status). Safe against a concurrent <see cref="AppendStep"/>.</summary>
    public MauiFlow Snapshot()
    {
        lock (_gate) return CopyLocked();
    }

    /// <summary>
    /// Validates and closes the recording under the same lock so a concurrent observation cannot
    /// append an unvalidated step between validation and final serialization.
    /// </summary>
    internal (MauiFlow? Flow, FlowValidation Validation, bool Empty) ValidateAndFinish()
    {
        lock (_gate)
        {
            var snapshot = CopyLocked();
            var validation = FlowValidator.Validate(snapshot);
            var empty = snapshot.Steps.Count == 0;
            if (empty || !validation.Ok)
                return (null, validation, empty);

            _closed = true;
            _flow.RecordedAt ??= _clock.GetUtcNow().ToString("o");
            return (CopyLocked(), validation, false);
        }
    }

    /// <summary>
    /// Closes the recording (further appends fail), stamps <c>recordedAt</c>, and returns a snapshot
    /// copy ready to serialize. Idempotent: a second call still returns the final snapshot.
    /// </summary>
    public MauiFlow Finish()
    {
        lock (_gate)
        {
            _closed = true;
            _flow.RecordedAt ??= _clock.GetUtcNow().ToString("o");
            return CopyLocked();
        }
    }

    private MauiFlow CopyLocked() => Clone(_flow);

    private static MauiFlow Clone(MauiFlow source) => new()
    {
        Schema = source.Schema,
        Name = source.Name,
        App = source.App,
        Platform = source.Platform,
        RecordedAt = source.RecordedAt,
        Preconditions = source.Preconditions,
        Steps = source.Steps.Select(CloneStep).ToList(),
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
            Animated = step.Args.Animated
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
            Note = assertion.Note
        }).ToList()
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
            Index = selector.TypeIndex.Index
        }
    };
}
