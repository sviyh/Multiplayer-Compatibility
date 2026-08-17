using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Drone Factory by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaQuestsExpanded-DroneFactory"/>
    [MpCompatFor("vanillaquestsexpanded.dronefactory")]
    public class VanillaQuestsExpandedDroneFactory
    {
        private static bool drawingDroneDraftColumn;

        public VanillaQuestsExpandedDroneFactory(ModContentPack mod)
        {
            // Autobroadcaster mode choices (the outer action only opens a local float menu).
            MpCompat.RegisterLambdaDelegate(
                "VanillaQuestsExpandedDroneFactory.CompAutobroadcaster", "CompGetGizmosExtra", 1);

            // Drone orders and controls.
            MpCompat.RegisterLambdaDelegate(
                "VanillaQuestsExpandedDroneFactory.CompDrone", "CompFloatMenuOptions", 0);
            MpCompat.RegisterLambdaDelegate(
                "VanillaQuestsExpandedDroneFactory.CompDrone", "CompGetGizmosExtra", 9, 10);
            MpCompat.RegisterLambdaDelegate(
                    "VanillaQuestsExpandedDroneFactory.CompDrone", "CompGetGizmosExtra",
                    0, 1, 2, 3, 4, 5, 7, 8)
                .SetDebugOnly();

            MpCompat.RegisterLambdaMethod(
                "VanillaQuestsExpandedDroneFactory.Building_DroneStandby", "GetGizmos", 0);
            MpCompat.RegisterLambdaDelegate(
                "VanillaQuestsExpandedDroneFactory.CompDecryptCore", "CompFloatMenuOptions", 0);
            MpCompat.RegisterLambdaMethod(
                    "VanillaQuestsExpandedDroneFactory.CompProximityFuseWithCount", "CompGetGizmosExtra", 0)
                .SetDebugOnly();

            // Drone table checkboxes. CurrentMap is required by standby drones stored as buildings.
            MP.RegisterSyncMethod(
                    AccessTools.Method("VanillaQuestsExpandedDroneFactory.PawnColumnWorker_Standby:SetValue"))
                .SetContext(SyncContext.CurrentMap);
            MP.RegisterSyncMethod(
                AccessTools.Method("VanillaQuestsExpandedDroneFactory.PawnColumnWorker_AutoRepair:SetValue"));

            PatchDroneDraftColumn();
            PatchDroneRenameDialog();
        }

        private static void PatchDroneDraftColumn()
        {
            var columnType = AccessTools.TypeByName("VanillaQuestsExpandedDroneFactory.PawnColumnWorker_Draft");
            MpCompat.harmony.Patch(AccessTools.Method(columnType, nameof(PawnColumnWorker.DoCell)),
                prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedDroneFactory), nameof(PreDrawDroneDraftColumn)),
                postfix: new HarmonyMethod(typeof(VanillaQuestsExpandedDroneFactory), nameof(PostDrawDroneDraftColumn)));

            MpCompat.harmony.Patch(
                AccessTools.PropertySetter(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted)),
                prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedDroneFactory), nameof(PreSetDrafted)));

            MP.RegisterSyncMethod(typeof(VanillaQuestsExpandedDroneFactory), nameof(SyncedSetDrafted));
        }

        private static void PreDrawDroneDraftColumn()
            => drawingDroneDraftColumn = MP.IsInMultiplayer;

        private static void PostDrawDroneDraftColumn()
            => drawingDroneDraftColumn = false;

        private static bool PreSetDrafted(Pawn_DraftController __instance, bool value)
        {
            if (!drawingDroneDraftColumn || MP.IsExecutingSyncCommand)
                return true;

            SyncedSetDrafted(__instance.pawn, value);
            return false;
        }

        private static void SyncedSetDrafted(Pawn pawn, bool drafted)
        {
            if (pawn?.drafter != null)
                pawn.drafter.Drafted = drafted;
        }

        private static void PatchDroneRenameDialog()
        {
            var dialogType = AccessTools.TypeByName("VanillaQuestsExpandedDroneFactory.Dialog_RenameDrone");
            MpCompat.harmony.Patch(AccessTools.Method(dialogType, "SetName"),
                prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedDroneFactory), nameof(PreSetDroneName)));
            MP.RegisterSyncMethod(typeof(VanillaQuestsExpandedDroneFactory), nameof(SyncedSetDroneName));
        }

        private static bool PreSetDroneName(Pawn ___pawn, string name)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            SyncedSetDroneName(___pawn, name);
            return false;
        }

        private static void SyncedSetDroneName(Pawn pawn, string name)
        {
            if (pawn != null)
                pawn.Name = new NameSingle(name);
        }
    }
}
