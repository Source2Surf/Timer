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

    // 区域没了 (编辑器删掉, 或触发器实体被删), 之后不会再为它发 EndTouch
    void OnZoneRemoved(IZoneInfo info)
    {
    }
}
