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

    /// <summary>
    /// How the failing assertion's own selector resolved: <c>resolved</c>,
    /// <see cref="FlowFailureKinds.NotFound"/>, or <see cref="FlowFailureKinds.Ambiguous"/>.
    /// Only set for assertion kinds that must resolve a selector to reach a verdict, so a
    /// <c>notExists</c> assertion that failed because the element was present is never read as
    /// selector drift.
    /// </summary>
    public string? AssertionTargetResolution { get; set; }
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

        // An assertion that failed because its own selector no longer resolves read no value from
        // the app, so the app was never observed to misbehave. This is checked before the stamped
        // class is honoured because the stamp is the very thing being corrected: the runner and
        // older reports both stamp `assertionFailed` from the legacy kind alone.
        if (AssertionTargetDrift(facts) is { } assertionDrift)
            return Describe(RefineLocatorDrift(assertionDrift, facts), facts, MauiFlowClassificationBases.Inferred);

        if (IsKnownClass(facts.FailureClass))
            return Describe(NormalizeClass(facts.FailureClass!), facts, MauiFlowClassificationBases.Stamped);

        var mapped = FromLegacyFailureKind(facts.LegacyFailureKind);
        if (mapped == MauiFlowFailureClasses.LocatorNotFound)
        {
            var refined = RefineLocatorDrift(mapped, facts);
            if (!string.Equals(refined, mapped, StringComparison.Ordinal))
                return Describe(refined, facts, MauiFlowClassificationBases.Inferred);
        }

        return Describe(mapped ?? MauiFlowFailureClasses.Infrastructure, facts, MauiFlowClassificationBases.Inferred);
    }

    /// <summary>
    /// A zero-match selector is never locator drift if the recorded run preconditions or route
    /// state disagree. Absence of checkpoint evidence is diagnostic-only, not repair eligible, but
    /// preserves the legacy locator result for compatibility.
    /// </summary>
    private static string RefineLocatorDrift(string failureClass, MauiFlowFailureFacts facts)
    {
        if (!string.Equals(failureClass, MauiFlowFailureClasses.LocatorNotFound, StringComparison.Ordinal))
            return failureClass;
        if (facts.RouteMatches == false)
            return MauiFlowFailureClasses.RouteStateDrift;
        if (facts.CheckpointMatches == false)
            return MauiFlowFailureClasses.PreconditionUnsatisfied;
        return failureClass;
    }

    /// <summary>
    /// Projects a stable failure class onto the four-value ownership axis in
    /// <see cref="MauiFlowTriageDispositions"/>. Unlike <see cref="Classify"/>, an unrecognized
    /// class projects to <c>inconclusive</c> rather than to <c>infrastructure</c>: a class this
    /// build does not know is not evidence that the environment failed. Returns null for a passed
    /// run or a missing class, because there is no failure to attribute.
    /// </summary>
    public static string? Project(string? failureClass) => failureClass?.Trim().ToLowerInvariant() switch
    {
        null or "" or "passed" => null,

        // The app resolved the target and then behaved differently than the recording.
        MauiFlowFailureClasses.AssertionFailed or
        MauiFlowFailureClasses.NotVisible or
        MauiFlowFailureClasses.Disabled or
        MauiFlowFailureClasses.ActionRejected
            => MauiFlowTriageDispositions.AppRegression,

        // The committed flow no longer describes the app it was recorded against.
        MauiFlowFailureClasses.LocatorNotFound or
        MauiFlowFailureClasses.LocatorAmbiguous or
        MauiFlowFailureClasses.RouteStateDrift or
        MauiFlowFailureClasses.PreconditionUnsatisfied or
        MauiFlowFailureClasses.FlowInvalid or
        MauiFlowFailureClasses.WorkflowCommandConflict or
        MauiFlowFailureClasses.UnsafeValue
            => MauiFlowTriageDispositions.TestDrift,

        // The failure is outside both the app and the flow.
        MauiFlowFailureClasses.CapabilityMissing or
        MauiFlowFailureClasses.SchemaUnsupported or
        MauiFlowFailureClasses.LeaseConflict or
        MauiFlowFailureClasses.LeaseLost or
        MauiFlowFailureClasses.ResetFailed or
        MauiFlowFailureClasses.DriveFailed or
        MauiFlowFailureClasses.SecretUnavailable or
        MauiFlowFailureClasses.Transport or
        MauiFlowFailureClasses.Infrastructure
            => MauiFlowTriageDispositions.Infrastructure,

        // Nothing recorded separates an app fault from a harness or environment fault. There is no
        // app exit code, no crash log, and no device log in this path, so agent-disconnected in
        // particular cannot be read as a crash.
        MauiFlowFailureClasses.Cancelled or
        MauiFlowFailureClasses.Timeout or
        MauiFlowFailureClasses.UnstableBounds or
        MauiFlowFailureClasses.UnknownCompletion or
        MauiFlowFailureClasses.AgentDisconnected
            => MauiFlowTriageDispositions.Inconclusive,

        _ => MauiFlowTriageDispositions.Inconclusive,
    };

    /// <summary>
    /// Maps an assertion failure onto locator drift when the assertion's own selector did not
    /// resolve. Returns null when the failure was not an assertion, when the selector resolved
    /// (the value genuinely differed, which is an app regression), or when no resolution was
    /// recorded — an absent signal must never be invented into drift.
    /// </summary>
    private static string? AssertionTargetDrift(MauiFlowFailureFacts facts)
    {
        if (!AssertionSelectorDrifted(facts))
            return null;
        var declared = IsKnownClass(facts.FailureClass)
            ? NormalizeClass(facts.FailureClass!)
            : FromLegacyFailureKind(facts.LegacyFailureKind);
        if (!string.Equals(declared, MauiFlowFailureClasses.AssertionFailed, StringComparison.Ordinal))
            return null;
        return NormalizeAssertionTargetResolution(facts) == FlowFailureKinds.Ambiguous
            ? MauiFlowFailureClasses.LocatorAmbiguous
            : MauiFlowFailureClasses.LocatorNotFound;
    }

    /// <summary>
    /// True when the failing assertion's own selector stopped matching a single element. Selector
    /// repair rewrites a step's <em>action</em> selector, so it must stay switched off here: the
    /// selector that drifted belongs to the assertion and repairing the action would replace a
    /// selector that is still resolving correctly. Read from the fact rather than from the
    /// classification branch, so re-classifying an already-corrected report reaches the same
    /// answer.
    /// </summary>
    private static bool AssertionSelectorDrifted(MauiFlowFailureFacts facts)
        => NormalizeAssertionTargetResolution(facts) is FlowFailureKinds.NotFound or FlowFailureKinds.Ambiguous;

    private static string? NormalizeAssertionTargetResolution(MauiFlowFailureFacts facts)
    {
        var resolution = facts.AssertionTargetResolution?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(resolution) ? null : resolution;
    }

    /// <summary>
    /// Reads how the first failed assertion's own selector resolved from a recorded step attempt.
    /// A report classified after the fact must reach the same answer as the run that produced it,
    /// so both read the signal from here.
    /// </summary>
    public static string? AssertionTargetResolutionOf(MauiFlowStepAttempt? step)
        => (step?.Assertions ?? [])
            .FirstOrDefault(static assertion =>
                assertion is not null && assertion.Passed == false && assertion.Skipped != true)
            ?.TargetResolution?.Status;

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
            !AssertionSelectorDrifted(facts) &&
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
