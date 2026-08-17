using HarmonyLib;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Races Expanded - Waster by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaRacesExpanded-Waster"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2983471725"/>
    [MpCompatFor("vanillaracesexpanded.waster")]
    public class VanillaRacesWaster
    {
        public VanillaRacesWaster(ModContentPack mod)
        {
            // Graphic material selection happens while rendering and must not advance simulation RNG.
            var type = AccessTools.TypeByName("VanillaRacesExpandedWaster.Graphic_Flicker_Green");
            PatchingUtilities.PatchPushPopRand(AccessTools.PropertyGetter(type, "MatSingle"));
        }
    }
}
