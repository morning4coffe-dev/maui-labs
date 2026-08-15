namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>
/// Facts supplied to the deterministic failure classifier. The classifier does not perform I/O,
/// retry actions, or generate repair proposals.
/// </summary>
public sealed class MauiFlowFailureFacts
{
    public string? TerminalOutcome { get; set; }
    public string? FailureClass { get; set; }
    public string? LegacyFailureKind { get; set; }
    public bool? FlowInvalid { get; set; }
    public bool? SchemaUnsupported { get; set; }
    public bool? CapabilityMissing { get; set; }
    public bool? ResetFailed { get; set; }
    public bool? ActionRejected { get; set; }
    public bool? AgentDisconnected { get; set; }
    public bool? TransportFailure { get; set; }
    public bool? CompletionCertain { get; set; }
    public bool? BeforeDispatch { get; set; }
    public bool? CheckpointVerified { get; set; }
    public bool? CheckpointMatches { get; set; }
    public bool? RouteMatches { get; set; }
}

/// <summary>The stable, typed result produced from <see cref="MauiFlowFailureFacts"/>.</summary>
/// <summary>
/// Why <see cref="MauiFlowFailureClassifier.Classify"/> returned the class it returned.
/// Only <see cref="Inferred"/> means the classifier derived the class from observable structure.
/// The other three mean the facts already named the class, so the answer was copied and is not
/// evidence that classification works.
/// </summary>
public static class MauiFlowClassificationBases
{
    /// <summary>The facts carried an explicit, known <c>FailureClass</c>.</summary>
    public const string Stamped = "stamped";

    /// <summary>The terminal outcome mapped directly onto a class.</summary>
    public const string Outcome = "outcome";

    /// <summary>A fact flag (transport, agent-disconnected, flow-invalid, ...) named the class.</summary>
    public const string FactFlag = "fact-flag";

    /// <summary>The class was derived from replay structure rather than read off an input.</summary>
    public const string Inferred = "inferred";
}

public sealed class MauiFlowFailureClassification
{
    public string FailureClass { get; init; } = MauiFlowFailureClasses.Infrastructure;
    public string Code { get; init; } = MauiFlowFailureClasses.Infrastructure;
    public string Category { get; init; } = "infrastructure";
    public string Phase { get; init; } = "execution";
    public bool Retryable { get; init; }
    public bool RepairEligible { get; init; }

    /// <summary>
    /// One of <see cref="MauiFlowClassificationBases"/>. Accuracy measurement must count only
    /// <see cref="MauiFlowClassificationBases.Inferred"/> results, or it measures its own inputs.
    /// </summary>
    public string Basis { get; init; } = MauiFlowClassificationBases.Inferred;
}

/// <summary>
/// Maps observed terminal state and legacy replay facts to exactly one stable failure class.
/// This is intentionally a pure mapping so host and device execution produce identical results.
/// </summary>
public static class MauiFlowFailureClassifier
{
    public static MauiFlowFailureClassification Classify(MauiFlowFailureFacts? facts)
    {
        facts ??= new MauiFlowFailureFacts();

        var terminal = ClassFromOutcome(facts.TerminalOutcome);
        if (terminal is not null)
            return Describe(terminal, facts, MauiFlowClassificationBases.Outcome);

        if (facts.CompletionCertain == false)
            return Describe(MauiFlowFailureClasses.UnknownCompletion, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.AgentDisconnected == true)
            return Describe(MauiFlowFailureClasses.AgentDisconnected, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.TransportFailure == true)
            return Describe(MauiFlowFailureClasses.Transport, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.FlowInvalid == true)
            return Describe(MauiFlowFailureClasses.FlowInvalid, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.SchemaUnsupported == true)
            return Describe(MauiFlowFailureClasses.SchemaUnsupported, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.CapabilityMissing == true)
            return Describe(MauiFlowFailureClasses.CapabilityMissing, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.ResetFailed == true)
            return Describe(MauiFlowFailureClasses.ResetFailed, facts, MauiFlowClassificationBases.FactFlag);
        if (facts.ActionRejected == true)
            return Describe(MauiFlowFailureClasses.ActionRejected, facts, MauiFlowClassificationBases.FactFlag);

        if (IsKnownClass(facts.FailureClass))
            return Describe(NormalizeClass(facts.FailureClass!), facts, MauiFlowClassificationBases.Stamped);

        var mapped = FromLegacyFailureKind(facts.LegacyFailureKind);
        if (mapped == MauiFlowFailureClasses.LocatorNotFound)
        {
            // A zero-match selector is never locator drift if the recorded run preconditions or
            // route state disagree. Absence of checkpoint evidence is diagnostic-only, not repair
            // eligible, but preserves the legacy locator result for compatibility.
            if (facts.RouteMatches == false)
                return Describe(MauiFlowFailureClasses.RouteStateDrift, facts, MauiFlowClassificationBases.Inferred);
            if (facts.CheckpointMatches == false)
                return Describe(MauiFlowFailureClasses.PreconditionUnsatisfied, facts, MauiFlowClassificationBases.Inferred);
        }

        return Describe(mapped ?? MauiFlowFailureClasses.Infrastructure, facts, MauiFlowClassificationBases.Inferred);
    }

    /// <summary>Maps a legacy <see cref="FlowFailureKinds"/> value without changing its wire value.</summary>
    public static string? FromLegacyFailureKind(string? legacyKind) => legacyKind switch
    {
        FlowFailureKinds.Validation => MauiFlowFailureClasses.FlowInvalid,
        FlowFailureKinds.Assertion => MauiFlowFailureClasses.AssertionFailed,
        FlowFailureKinds.Ambiguous => MauiFlowFailureClasses.LocatorAmbiguous,
        FlowFailureKinds.NotFound => MauiFlowFailureClasses.LocatorNotFound,
        FlowFailureKinds.NotVisible => MauiFlowFailureClasses.NotVisible,
        FlowFailureKinds.Disabled => MauiFlowFailureClasses.Disabled,
        FlowFailureKinds.Unstable => MauiFlowFailureClasses.UnstableBounds,
        FlowFailureKinds.SecretRequired => MauiFlowFailureClasses.SecretUnavailable,
        FlowFailureKinds.UnsafeValue => MauiFlowFailureClasses.UnsafeValue,
        FlowFailureKinds.UnknownCompletion => MauiFlowFailureClasses.UnknownCompletion,
        FlowFailureKinds.WorkflowCommandConflict => MauiFlowFailureClasses.WorkflowCommandConflict,
        FlowFailureKinds.Drive => MauiFlowFailureClasses.DriveFailed,
        null or "" => null,
        _ => MauiFlowFailureClasses.DriveFailed,
    };

    /// <summary>Creates the report model from a classification without exposing arbitrary exception data.</summary>
    public static MauiFlowFailure ToFailure(
        MauiFlowFailureClassification classification,
        string? failureId,
        string? legacyKind,
        string? stepId,
        DateTimeOffset at,
        string? message = null) => new()
    {
        FailureId = failureId,
        Class = classification.FailureClass,
        Code = classification.Code,
        Category = classification.Category,
        Phase = classification.Phase,
        Retryable = classification.Retryable,
        RepairEligible = classification.RepairEligible,
        LegacyKind = legacyKind,
        StepId = stepId,
        At = at,
        Message = MauiFlowReportRedactor.SafeMessage(message),
    };

    private static MauiFlowFailureClassification Describe(string failureClass, MauiFlowFailureFacts facts, string basis)
    {
        var (category, phase, retryable) = failureClass switch
        {
            MauiFlowFailureClasses.FlowInvalid or MauiFlowFailureClasses.SchemaUnsupported
                => ("flow", "validation", false),
            MauiFlowFailureClasses.CapabilityMissing
                => ("capability", "preflight", false),
            MauiFlowFailureClasses.LeaseConflict or MauiFlowFailureClasses.LeaseLost
                => ("lease", "lifecycle", false),
            MauiFlowFailureClasses.Cancelled or MauiFlowFailureClasses.Timeout
                => ("lifecycle", "execution", false),
            MauiFlowFailureClasses.ResetFailed
                => ("lifecycle", "reset", false),
            MauiFlowFailureClasses.PreconditionUnsatisfied or MauiFlowFailureClasses.RouteStateDrift
                => ("state", "precondition", false),
            MauiFlowFailureClasses.LocatorNotFound or MauiFlowFailureClasses.LocatorAmbiguous
                => ("selector", "resolution", false),
            MauiFlowFailureClasses.NotVisible or MauiFlowFailureClasses.Disabled or MauiFlowFailureClasses.UnstableBounds
                => ("actionability", "actionability", false),
            MauiFlowFailureClasses.ActionRejected or MauiFlowFailureClasses.DriveFailed
                => ("action", "dispatch", false),
            MauiFlowFailureClasses.UnknownCompletion or MauiFlowFailureClasses.WorkflowCommandConflict
                => ("command", "dispatch", false),
            MauiFlowFailureClasses.SecretUnavailable or MauiFlowFailureClasses.UnsafeValue
                => ("input", "preflight", false),
            MauiFlowFailureClasses.AssertionFailed
                => ("assertion", "verification", false),
            MauiFlowFailureClasses.Transport or MauiFlowFailureClasses.AgentDisconnected
                => ("transport", "execution", true),
            _ => ("infrastructure", "execution", false),
        };

        var repairEligible = failureClass == MauiFlowFailureClasses.LocatorNotFound &&
            facts.BeforeDispatch == true &&
            facts.CheckpointVerified == true &&
            facts.CheckpointMatches == true &&
            facts.RouteMatches != false;

        return new MauiFlowFailureClassification
        {
            FailureClass = failureClass,
            Code = failureClass,
            Category = category,
            Phase = phase,
            Retryable = retryable,
            RepairEligible = repairEligible,
            Basis = basis,
        };
    }

    private static string? ClassFromOutcome(string? outcome) => outcome switch
    {
        MauiFlowRunOutcomes.Cancelled => MauiFlowFailureClasses.Cancelled,
        MauiFlowRunOutcomes.TimedOut => MauiFlowFailureClasses.Timeout,
        MauiFlowRunOutcomes.LeaseLost => MauiFlowFailureClasses.LeaseLost,
        MauiFlowRunOutcomes.UnknownCompletion => MauiFlowFailureClasses.UnknownCompletion,
        MauiFlowRunOutcomes.Orphaned => MauiFlowFailureClasses.AgentDisconnected,
        MauiFlowRunOutcomes.InfrastructureError => MauiFlowFailureClasses.Infrastructure,
        _ => null,
    };

    private static bool IsKnownClass(string? value)
        => value is not null && KnownClasses.Contains(value);

    /// <summary>Returns whether the supplied value is one of the closed stable failure classes.</summary>
    public static bool IsKnownFailureClass(string? value) => IsKnownClass(value);

    private static string NormalizeClass(string value)
        => value.Trim().ToLowerInvariant();

    private static readonly HashSet<string> KnownClasses = new(StringComparer.Ordinal)
    {
        MauiFlowFailureClasses.FlowInvalid,
        MauiFlowFailureClasses.SchemaUnsupported,
        MauiFlowFailureClasses.CapabilityMissing,
        MauiFlowFailureClasses.LeaseConflict,
        MauiFlowFailureClasses.LeaseLost,
        MauiFlowFailureClasses.Cancelled,
        MauiFlowFailureClasses.Timeout,
        MauiFlowFailureClasses.ResetFailed,
        MauiFlowFailureClasses.PreconditionUnsatisfied,
        MauiFlowFailureClasses.RouteStateDrift,
        MauiFlowFailureClasses.LocatorNotFound,
        MauiFlowFailureClasses.LocatorAmbiguous,
        MauiFlowFailureClasses.NotVisible,
        MauiFlowFailureClasses.Disabled,
        MauiFlowFailureClasses.UnstableBounds,
        MauiFlowFailureClasses.ActionRejected,
        MauiFlowFailureClasses.DriveFailed,
        MauiFlowFailureClasses.UnknownCompletion,
        MauiFlowFailureClasses.WorkflowCommandConflict,
        MauiFlowFailureClasses.SecretUnavailable,
        MauiFlowFailureClasses.UnsafeValue,
        MauiFlowFailureClasses.AssertionFailed,
        MauiFlowFailureClasses.Transport,
        MauiFlowFailureClasses.AgentDisconnected,
        MauiFlowFailureClasses.Infrastructure,
    };
}
