using Sharp.Shared.GameEntities;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Shared.Interfaces.Listeners;

public interface IZoneModuleListener
{
    void OnZoneStartTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
    }

    void OnZoneEndTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
    }

    void OnZoneTrigger(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
    }
}
