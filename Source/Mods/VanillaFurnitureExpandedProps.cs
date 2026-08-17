using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Furniture Expanded - Props and Decor by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaFurnitureExpanded-Props"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2102143149"/>
    [MpCompatFor("VanillaExpanded.VFEPropsandDecor")]
    public class VanillaFurnitureExpandedProps
    {
        public VanillaFurnitureExpandedProps(ModContentPack mod)
        {
            // Prop placement is synchronized by Multiplayer's generic designator handling.
            // Resolve silver against the placed prop's map instead of each player's viewed map.
            PatchingUtilities.ReplaceCurrentMapUsage("VFEProps.Building_SubstractsSilver:CheckSilverInMap");
            PatchingUtilities.ReplaceCurrentMapUsage("VFEProps.Building_SubstractsSilver:RemoveSilverFromMap");
        }
    }
}
