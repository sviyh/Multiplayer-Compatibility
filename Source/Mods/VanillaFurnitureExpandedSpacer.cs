using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Furniture Expanded - Spacer Module by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaFurnitureExpanded-Spacer"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2028381079"/>
    [MpCompatFor("VanillaExpanded.VFESpacer")]
    public class VanillaFurnitureExpandedSpacer
    {
        public VanillaFurnitureExpandedSpacer(ModContentPack mod)
        {
            // Toggle automatic forbidding for items stored in the repair shelf
            MpCompat.RegisterLambdaMethod("MFSpacer.CompRepairStored", "CompGetGizmosExtra", 1);
        }
    }
}
