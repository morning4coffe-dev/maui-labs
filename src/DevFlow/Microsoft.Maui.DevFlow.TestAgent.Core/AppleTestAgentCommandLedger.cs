namespace Microsoft.Maui.DevFlow.TestAgent.Protocol;

/// <summary>State of a command retained by the bounded active-session ledger.</summary>
public enum AppleTestAgentCommandState
{
    Prepared,
    Dispatched,
    Completed,
    Cancelled,
    UnknownCompletion,
}

/// <summary>Result of preparing, completing, or cancelling a command without retaining its payload.</summary>
public sealed class AppleTestAgentLedgerResult
{
    public bool Accepted { get; init; }
    public bool ShouldDispatch { get; init; }
    public bool IsDuplicate { get; init; }
    public AppleTestAgentCommandReceipt? Receipt { get; init; }
    public AppleTestAgentError? Error { get; init; }
}

/// <summary>
/// Command-ID and sequence ledger shared by host transports and deterministic tests. It contains no
/// flow, selector, actionability, source, plan, or repair policy; it only fences operation commands.
/// </summary>
public sealed class AppleTestAgentCommandLedger
{
    private readonly object _gate = new();
    private readonly AppleTestAgentSession _session;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _commands = new(StringComparer.Ordinal);
    private long _lastSequence;

    public AppleTestAgentCommandLedger(AppleTestAgentSession session, TimeProvider? clock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? TimeProvider.System;
    }

    public AppleTestAgentLedgerResult Prepare(AppleTestAgentOperationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (!TryValidate(command, out var error))
                return Rejected(error!);

            if (_commands.TryGetValue(command.CommandId, out var existing))
            {
                if (!SameIdentity(existing.Command, command))
                    return Rejected(Error(AppleTestAgentErrorCodes.CommandConflict, "conflict", "The command ID was reused with different metadata."));

                return new AppleTestAgentLedgerResult
                {
                    Accepted = existing.State != AppleTestAgentCommandState.UnknownCompletion,
                    ShouldDispatch = false,
                    IsDuplicate = true,
                    Receipt = Clone(existing.Receipt),
                    Error = existing.State == AppleTestAgentCommandState.UnknownCompletion
                        ? Error(AppleTestAgentErrorCodes.UnknownCompletion, "state", "The command has unknown completion and cannot be retried.")
                        : null,
                };
            }

            if (command.Sequence != _lastSequence + 1)
            {
                return Rejected(Error(
                    AppleTestAgentErrorCodes.SequenceRejected,
                    "state",
                    "The command sequence must be contiguous."));
            }
            if (_commands.Count >= AppleTestAgentProtocolVersions.MaximumCommandsPerSession)
            {
                return Rejected(Error(
                    AppleTestAgentErrorCodes.InvalidRequest,
                    "state",
                    "The active session reached its bounded command limit."));
            }

            var receipt = CreateReceipt(command, "prepared", "pending");
            _commands.Add(command.CommandId, new Entry(command, receipt, AppleTestAgentCommandState.Prepared));
            _lastSequence = command.Sequence;
            return new AppleTestAgentLedgerResult
            {
                Accepted = true,
                ShouldDispatch = true,
                Receipt = Clone(receipt),
            };
        }
    }

    public AppleTestAgentLedgerResult MarkDispatched(string commandId)
        => Transition(commandId, AppleTestAgentCommandState.Dispatched, "dispatched", "pending");

    public AppleTestAgentLedgerResult Complete(AppleTestAgentOperationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            if (!_commands.TryGetValue(completion.Receipt.CommandId, out var entry))
                return Rejected(Error(AppleTestAgentErrorCodes.InvalidRequest, "state", "The command receipt is unknown."));

            if (!SameReceipt(entry.Receipt, completion.Receipt))
                return Rejected(Error(AppleTestAgentErrorCodes.CommandConflict, "conflict", "The completion receipt does not match the prepared command."));

            if (entry.State == AppleTestAgentCommandState.UnknownCompletion)
                return Rejected(Error(AppleTestAgentErrorCodes.UnknownCompletion, "state", "The command completion is already unknown."));

            if (entry.State == AppleTestAgentCommandState.Cancelled)
            {
                return new AppleTestAgentLedgerResult
                {
                    Accepted = true,
                    IsDuplicate = true,
                    Receipt = Clone(entry.Receipt),
                };
            }

            var cancelled = string.Equals(
                completion.Error?.Code,
                AppleTestAgentErrorCodes.Cancelled,
                StringComparison.Ordinal) ||
                string.Equals(completion.Receipt.AcknowledgementState, "cancelled", StringComparison.Ordinal);
            entry.State = cancelled
                ? AppleTestAgentCommandState.Cancelled
                : completion.CompletionCertainty == "unknown"
                    ? AppleTestAgentCommandState.UnknownCompletion
                    : AppleTestAgentCommandState.Completed;
            entry.Receipt.AcknowledgementState = entry.State switch
            {
                AppleTestAgentCommandState.Cancelled => "cancelled",
                AppleTestAgentCommandState.Completed => "completed",
                _ => "unknown-completion",
            };
            entry.Receipt.CompletionCertainty = completion.CompletionCertainty;
            entry.Receipt.At = _clock.GetUtcNow();
            return new AppleTestAgentLedgerResult
            {
                Accepted = entry.State is AppleTestAgentCommandState.Completed or AppleTestAgentCommandState.Cancelled,
                Receipt = Clone(entry.Receipt),
                Error = entry.State == AppleTestAgentCommandState.UnknownCompletion
                    ? Error(AppleTestAgentErrorCodes.UnknownCompletion, "state", "The operation did not provide a durable completion receipt.")
                    : null,
            };
        }
    }

    public AppleTestAgentLedgerResult Cancel(string commandId)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(commandId, out var entry))
                return Rejected(Error(AppleTestAgentErrorCodes.InvalidRequest, "state", "The command is unknown."));

            if (entry.State is AppleTestAgentCommandState.Completed or AppleTestAgentCommandState.Cancelled)
            {
                return new AppleTestAgentLedgerResult
                {
                    Accepted = true,
                    IsDuplicate = true,
                    Receipt = Clone(entry.Receipt),
                };
            }

            entry.State = AppleTestAgentCommandState.Cancelled;
            entry.Receipt.AcknowledgementState = "cancelled";
            entry.Receipt.CompletionCertainty = "certain";
            entry.Receipt.At = _clock.GetUtcNow();
            return new AppleTestAgentLedgerResult { Accepted = true, Receipt = Clone(entry.Receipt) };
        }
    }

    public AppleTestAgentLedgerResult MarkUnknownCompletion(string commandId)
        => Transition(commandId, AppleTestAgentCommandState.UnknownCompletion, "unknown-completion", "unknown");

    private AppleTestAgentLedgerResult Transition(
        string commandId,
        AppleTestAgentCommandState state,
        string acknowledgement,
        string certainty)
    {
        lock (_gate)
        {
            if (!_commands.TryGetValue(commandId, out var entry))
                return Rejected(Error(AppleTestAgentErrorCodes.InvalidRequest, "state", "The command is unknown."));

            if (entry.State is AppleTestAgentCommandState.Completed or AppleTestAgentCommandState.Cancelled or AppleTestAgentCommandState.UnknownCompletion)
            {
                return new AppleTestAgentLedgerResult
                {
                    Accepted = entry.State != AppleTestAgentCommandState.UnknownCompletion,
                    IsDuplicate = true,
                    Receipt = Clone(entry.Receipt),
                    Error = entry.State == AppleTestAgentCommandState.UnknownCompletion
                        ? Error(AppleTestAgentErrorCodes.UnknownCompletion, "state", "The command completion is unknown.")
                        : null,
                };
            }

            entry.State = state;
            entry.Receipt.AcknowledgementState = acknowledgement;
            entry.Receipt.CompletionCertainty = certainty;
            entry.Receipt.At = _clock.GetUtcNow();
            return new AppleTestAgentLedgerResult { Accepted = state != AppleTestAgentCommandState.UnknownCompletion, Receipt = Clone(entry.Receipt) };
        }
    }

    private bool TryValidate(AppleTestAgentOperationCommand command, out AppleTestAgentError? error)
    {
        error = null;
        if (command.Schema != AppleTestAgentProtocolVersions.Schema ||
            string.IsNullOrWhiteSpace(command.CommandId) ||
            command.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(command.ActionDigest) ||
            !AppleTestAgentOperations.All.Contains(command.Operation))
        {
            error = Error(AppleTestAgentErrorCodes.InvalidRequest, "validation", "The operation command is incomplete or unsupported.");
            return false;
        }

        if (!string.Equals(command.SessionId, _session.SessionId, StringComparison.Ordinal))
        {
            error = Error(AppleTestAgentErrorCodes.SessionMismatch, "target", "The command targets a different session.");
            return false;
        }

        if (!string.Equals(command.Target.TargetBundleId, _session.Target.TargetBundleId, StringComparison.Ordinal) ||
            !string.Equals(command.Target.Platform, _session.Target.Platform, StringComparison.Ordinal))
        {
            error = Error(AppleTestAgentErrorCodes.TargetMismatch, "target", "The command targets a different app identity.");
            return false;
        }

        if (command.AuthorityEpoch != _session.AuthorityEpoch)
        {
            error = Error(AppleTestAgentErrorCodes.StaleEpoch, "authorization", "The command authority epoch is stale.");
            return false;
        }

        if (!string.Equals(command.ApprovalDigest, _session.ApprovalDigest, StringComparison.Ordinal))
        {
            error = Error(AppleTestAgentErrorCodes.ApprovalMismatch, "authorization", "The command approval digest does not match.");
            return false;
        }

        var now = _clock.GetUtcNow();
        if (_session.ExpiresAt <= now || command.Deadline <= now || command.Deadline > _session.ExpiresAt)
        {
            error = Error(AppleTestAgentErrorCodes.DeadlineExpired, "deadline", "The command deadline has expired.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_session.Target.AppInstanceId) &&
            !string.Equals(command.Target.AppInstanceId, _session.Target.AppInstanceId, StringComparison.Ordinal))
        {
            error = Error(AppleTestAgentErrorCodes.TargetMismatch, "target", "The command targets a different app instance.");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_session.Target.AppBuildDigest) &&
            !string.Equals(command.Target.AppBuildDigest, _session.Target.AppBuildDigest, StringComparison.Ordinal))
        {
            error = Error(AppleTestAgentErrorCodes.TargetMismatch, "target", "The command targets a different app build.");
            return false;
        }

        return true;
    }

    private AppleTestAgentCommandReceipt CreateReceipt(
        AppleTestAgentOperationCommand command,
        string acknowledgement,
        string certainty)
        => new()
        {
            SessionId = command.SessionId,
            CommandId = command.CommandId,
            Sequence = command.Sequence,
            ActionDigest = command.ActionDigest,
            AuthorityEpoch = command.AuthorityEpoch,
            ApprovalDigest = command.ApprovalDigest,
            AcknowledgementState = acknowledgement,
            CompletionCertainty = certainty,
            At = _clock.GetUtcNow(),
        };

    private static bool SameIdentity(AppleTestAgentOperationCommand left, AppleTestAgentOperationCommand right)
        => left.Sequence == right.Sequence &&
            left.AuthorityEpoch == right.AuthorityEpoch &&
            string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
            string.Equals(left.ActionDigest, right.ActionDigest, StringComparison.Ordinal) &&
            string.Equals(left.ApprovalDigest, right.ApprovalDigest, StringComparison.Ordinal);

    private static bool SameReceipt(AppleTestAgentCommandReceipt left, AppleTestAgentCommandReceipt right)
        => left.Sequence == right.Sequence &&
            left.AuthorityEpoch == right.AuthorityEpoch &&
            string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
            string.Equals(left.CommandId, right.CommandId, StringComparison.Ordinal) &&
            string.Equals(left.ActionDigest, right.ActionDigest, StringComparison.Ordinal) &&
            string.Equals(left.ApprovalDigest, right.ApprovalDigest, StringComparison.Ordinal);

    private static AppleTestAgentCommandReceipt Clone(AppleTestAgentCommandReceipt receipt) => new()
    {
        SessionId = receipt.SessionId,
        CommandId = receipt.CommandId,
        Sequence = receipt.Sequence,
        ActionDigest = receipt.ActionDigest,
        AuthorityEpoch = receipt.AuthorityEpoch,
        ApprovalDigest = receipt.ApprovalDigest,
        AcknowledgementState = receipt.AcknowledgementState,
        CompletionCertainty = receipt.CompletionCertainty,
        At = receipt.At,
    };

    private static AppleTestAgentLedgerResult Rejected(AppleTestAgentError error)
        => new() { Accepted = false, Error = error };

    private static AppleTestAgentError Error(string code, string category, string message)
        => new() { Code = code, Category = category, Message = message, Retryable = false };

    private sealed class Entry(
        AppleTestAgentOperationCommand command,
        AppleTestAgentCommandReceipt receipt,
        AppleTestAgentCommandState state)
    {
        public AppleTestAgentOperationCommand Command { get; } = command;
        public AppleTestAgentCommandReceipt Receipt { get; } = receipt;
        public AppleTestAgentCommandState State { get; set; } = state;
    }
}
