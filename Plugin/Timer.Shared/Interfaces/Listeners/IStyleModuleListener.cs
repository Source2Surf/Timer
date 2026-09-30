using System.Collections.Generic;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models.Style;

namespace Source2Surf.Timer.Shared.Interfaces.Listeners;

public interface IStyleModuleListener
{
    void OnStyleConfigLoaded(IReadOnlyList<StyleSetting> styles)
    {
    }

    void OnClientStyleChanged(PlayerSlot slot, int oldStyle, int newStyle)
    {
    }
}
