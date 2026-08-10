using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Plants Expanded - Flowers by Oskar Potocki, Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaPlantsExpanded-Flowers"/>
    [MpCompatFor("VanillaExpanded.VPEFlowers")]
    public class VanillaPlantsFlowers
    {
        public VanillaPlantsFlowers(ModContentPack mod)
        {
            // Toggle allow sow (1), allow cut (3), verified at f6f891d
            MpCompat.RegisterLambdaMethod("VanillaPlantsExpandedFlowers.Zone_BloomingFlowerZone", "GetGizmos", 1, 3);
        }
    }
}
