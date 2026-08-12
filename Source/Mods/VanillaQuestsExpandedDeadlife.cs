using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Deadlife by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaQuestsExpanded-Deadlife"/>
    [MpCompatFor("vanillaquestsexpanded.deadlife")]
    public class VanillaQuestsExpandedDeadlife
    {
        public VanillaQuestsExpandedDeadlife(ModContentPack mod)
        {
            // Fill the death pit and its developer spawn-timer action
            var deathPitActions = MpCompat.RegisterLambdaMethod(
                "VanillaQuestsExpandedDeadlife.DeathPit", "GetGizmos", 0, 1);
            deathPitActions[1].SetDebugOnly();

            // Order a pawn to kick-start the ancient power plant
            MP.RegisterSyncMethod(
                AccessTools.Method("VanillaQuestsExpandedDeadlife.CompInteractablePowerPlant:OrderActivation"));
        }
    }
}
