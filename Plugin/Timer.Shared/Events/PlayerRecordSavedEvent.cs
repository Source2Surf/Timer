using System;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Shared.Events;

public sealed record PlayerRecordSavedEvent(SteamID        SteamId,
                                            string         PlayerName,
                                            EAttemptResult RecordType,
                                            RunRecord      SavedRecord,
                                            RunRecord?     WrRecord,
                                            RunRecord?     PbRecord,
                                            int            AttemptId)
{
    public ulong MapId         => SavedRecord.MapId;
    public int   Style         => SavedRecord.Style;
    public int   Track         => SavedRecord.Track;
    public int   Stage         => SavedRecord.Stage;
    public float Time          => SavedRecord.Time;
    public bool  IsStageRecord => SavedRecord.Stage > 0;
}
