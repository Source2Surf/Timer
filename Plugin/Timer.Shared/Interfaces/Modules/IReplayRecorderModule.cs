using Sharp.Shared.Units;

namespace Source2Surf.Timer.Shared.Interfaces.Modules;

/// <summary>
/// Interface for the replay recorder module.
/// All interactions are driven through event listeners (IRecordModuleListener, ITimerModuleListener, etc.).
/// </summary>
public interface IReplayRecorderModule
{
    /// <summary>
    /// Gets the current AttemptId for a player slot.
    /// Used by RecordModule to pass AttemptId as a Correlation ID to RecordSaver,
    /// which flows back through PlayerRecordSavedEvent for PendingReplayStore matching.
    /// Returns 0 if the player has no active frame data.
    /// </summary>
    int GetAttemptId(PlayerSlot slot);
}
