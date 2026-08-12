using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Ancients by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaQuestsExpanded-Ancients"/>
    [MpCompatFor("vanillaquestsexpanded.ancients")]
    public class VanillaQuestsExpandedAncients
    {
        private static AccessTools.FieldRef<object, Thing> tubeDialogParentField;
        private static AccessTools.FieldRef<object, List<TransferableOneWay>> tubeDialogTransferablesField;
        private static FastInvokeHandler tubeMassUsageGetter;
        private static FastInvokeHandler tubeMassCapacityGetter;

        private static AccessTools.FieldRef<object, Pawn> carryTargetField;
        private static AccessTools.FieldRef<object, FloatMenuContext> carryContextField;
        private static FastInvokeHandler findBiobattery;
        private static JobDef carryToBiobatteryJob;

        public VanillaQuestsExpandedAncients(ModContentPack mod)
        {
            var processorType = AccessTools.TypeByName("VanillaQuestsExpandedAncients.Building_PawnProcessor");
            var injectorType = AccessTools.TypeByName("VanillaQuestsExpandedAncients.Building_ArchogenInjector");

            // Processor selection uses vanilla Building_Enterable.SelectPawn, already synchronized by Multiplayer.
            MP.RegisterSyncMethod(AccessTools.Method(processorType, "CancelProcess"));
            MP.RegisterSyncMethod(AccessTools.Method(injectorType, "CancelProcess"));
            MP.RegisterSyncMethod(AccessTools.Method(injectorType, "ConfirmInjection"));
            MpCompat.RegisterLambdaMethod(processorType, "GetPawnProcessorGizmos", 1).SetDebugOnly();
            MpCompat.RegisterLambdaMethod(injectorType, "GetGizmos", 0).SetDebugOnly();

            // Gene choices produced by a successful archogen injection.
            MpCompat.RegisterLambdaDelegate(injectorType, "HandleSuccessOutcome", 1);

            // Wonderdoc cycle selection.
            MpCompat.RegisterLambdaDelegate(
                "VanillaQuestsExpandedAncients.Building_AncientWonderdoc", "GetGizmos", 0);

            // Pneumatic tube launch and developer delivery.
            var tubeType = AccessTools.TypeByName("VanillaQuestsExpandedAncients.Building_PneumaticTubeLaunchPort");
            MP.RegisterSyncMethod(AccessTools.Method(tubeType, "Launch"));
            MpCompat.RegisterLambdaMethod(tubeType, "GetGizmos", 2).SetDebugOnly();
            PatchPneumaticTubeDialog();

            // Ancient power plant interaction.
            MP.RegisterSyncMethod(AccessTools.Method(
                "VanillaQuestsExpandedAncients.CompInteractablePowerPlant:OrderActivation"));

            PatchCarryToBiobattery();
        }

        private static void PatchPneumaticTubeDialog()
        {
            var dialogType = AccessTools.TypeByName("VanillaQuestsExpandedAncients.Dialog_LoadPneumaticTube");
            tubeDialogParentField = AccessTools.FieldRefAccess<Thing>(dialogType, "parent");
            tubeDialogTransferablesField =
                AccessTools.FieldRefAccess<List<TransferableOneWay>>(dialogType, "transferables");
            tubeMassUsageGetter = MethodInvoker.GetHandler(AccessTools.PropertyGetter(dialogType, "MassUsage"));
            tubeMassCapacityGetter = MethodInvoker.GetHandler(AccessTools.PropertyGetter(dialogType, "MassCapacity"));

            var sync = MP.RegisterSyncMethod(typeof(VanillaQuestsExpandedAncients), nameof(SyncedAcceptTubeLoad));
            sync.ExposeParameter(1);
            MpCompat.harmony.Patch(AccessTools.Method(dialogType, "TryAccept"),
                prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedAncients), nameof(PreAcceptTubeLoad)));
        }

        private static bool PreAcceptTubeLoad(object __instance, ref bool __result)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var massUsage = (float)tubeMassUsageGetter(__instance);
            var massCapacity = (float)tubeMassCapacityGetter(__instance);
            if (massUsage > massCapacity)
                return true;

            SyncedAcceptTubeLoad(tubeDialogParentField(__instance), tubeDialogTransferablesField(__instance));
            __result = true;
            return false;
        }

        private static void SyncedAcceptTubeLoad(Thing parent, List<TransferableOneWay> transferables)
        {
            if (parent is not ThingWithComps thingWithComps || transferables == null)
                return;

            var transporter = thingWithComps.AllComps.OfType<CompTransporter>().FirstOrDefault();
            if (transporter == null)
                return;

            AccessTools.Method(transporter.GetType(), "ResetNotifiedFlag")?.Invoke(transporter, null);
            TransporterUtility.InitiateLoading(new List<CompTransporter> { transporter });
            foreach (var transferable in transferables)
            {
                if (transferable.CountToTransfer > 0)
                    transporter.AddToTheToLoadList(transferable, transferable.CountToTransfer);
            }
        }

        private static void PatchCarryToBiobattery()
        {
            var providerType = AccessTools.TypeByName(
                "VanillaQuestsExpandedAncients.FloatMenuOptionProvider_CarryToBiobattery");
            var action = MpMethodUtil.GetLambda(providerType, "GetSingleOptionFor", lambdaOrdinal: 0);
            carryTargetField = AccessTools.FieldRefAccess<Pawn>(action.DeclaringType, "clickedPawn");
            carryContextField = AccessTools.FieldRefAccess<FloatMenuContext>(action.DeclaringType, "context");

            var batteryType = AccessTools.TypeByName("VanillaQuestsExpandedAncients.CompBioBattery");
            findBiobattery = MethodInvoker.GetHandler(AccessTools.Method(
                batteryType, "FindBatteryFor", new[] { typeof(Pawn), typeof(Pawn), typeof(bool) }));
            carryToBiobatteryJob = DefDatabase<JobDef>.GetNamed("VQEA_CarryToBioBattery");

            MP.RegisterSyncMethod(typeof(VanillaQuestsExpandedAncients), nameof(SyncedCarryToBiobattery));
            MpCompat.harmony.Patch(action,
                prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedAncients), nameof(PreCarryToBiobattery)));
        }

        private static bool PreCarryToBiobattery(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var context = carryContextField(__instance);
            SyncedCarryToBiobattery(context.FirstSelectedPawn, carryTargetField(__instance));
            return false;
        }

        private static void SyncedCarryToBiobattery(Pawn carrier, Pawn target)
        {
            if (carrier == null || target == null)
                return;

            var battery = (Thing)findBiobattery(null, target, carrier, false)
                ?? (Thing)findBiobattery(null, target, carrier, true);
            if (battery == null)
            {
                Messages.Message("CannotCarryToCryptosleepCasket".Translate() + ": " +
                    "NoCryptosleepCasket".Translate(), target, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            var job = JobMaker.MakeJob(carryToBiobatteryJob, target, battery);
            job.count = 1;
            carrier.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }
    }
}
