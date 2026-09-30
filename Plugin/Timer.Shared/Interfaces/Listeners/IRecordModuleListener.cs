using Source2Surf.Timer.Shared.Events;

namespace Source2Surf.Timer.Shared.Interfaces.Listeners;

public interface IRecordModuleListener
{
    void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
    }

    void OnMapRecordsLoaded()
    {
    }
}
