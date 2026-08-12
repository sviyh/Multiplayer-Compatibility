using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Gravship Expanded 2 by Oskar Potocki and the Vanilla Expanded team</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaGravshipExpanded2"/>
    [MpCompatFor("vanillaexpanded.gravship2")]
    public class VanillaGravshipExpanded2
    {
        private static Type mapComponentType;
        private static Type worldComponentType;
        private static Type warpodType;
        private static Type vacuumLightType;
        private static Type escapePodType;
        private static System.Reflection.FieldInfo selectedWarpodsField;
        private static System.Reflection.FieldInfo worldComponentInstanceField;
        private static System.Reflection.PropertyInfo activeEngineProperty;
        private static System.Reflection.FieldInfo engineLockedField;
        private static System.Reflection.FieldInfo tributeDemandTickField;
        private static System.Reflection.FieldInfo salvagerDelayDaysField;

        public VanillaGravshipExpanded2(ModContentPack mod)
        {
            mapComponentType = AccessTools.TypeByName("VanillaGravshipExpanded2.VGE2_MapComponent");
            worldComponentType = AccessTools.TypeByName("VanillaGravshipExpanded2.WorldComponent_GravshipCombat");
            warpodType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompLaunchable_Warpod");
            vacuumLightType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompVacuumWarningLight");
            escapePodType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompEscapePod");

            selectedWarpodsField = AccessTools.Field(warpodType, "selectedWarpodsToLaunch");
            worldComponentInstanceField = AccessTools.Field(worldComponentType, "Instance");
            activeEngineProperty = AccessTools.Property(worldComponentType, "GetActiveGravEngine");
            engineLockedField = AccessTools.Field(worldComponentType, "engineLockedRemotely");
            tributeDemandTickField = AccessTools.Field(worldComponentType, "tributeDemandTick");
            salvagerDelayDaysField = AccessTools.Field(worldComponentType, "salvagerDelayDays");

            MP.RegisterSyncWorker<object>(SyncMapComponent, mapComponentType);
            MP.RegisterSyncWorker<object>(SyncWorldComponent, worldComponentType);

            // Escape-pod state and orders. The targeter-opening callback remains local UI;
            // only callbacks that issue jobs or mutate simulation state are synchronized.
            MP.RegisterSyncMethod(AccessTools.PropertySetter(mapComponentType, "EvacuationActive"));
            MP.RegisterSyncMethod(escapePodType, "ClaimIfNeeded");
            MpCompat.RegisterLambdaMethod(escapePodType, "CompGetGizmosExtra", 1);
            MpCompat.RegisterLambdaDelegate(escapePodType, "CompFloatMenuOptions", 0, 2, 3);

            // Vacuum-light dialog writes through these setters, so the window itself stays local.
            MP.RegisterSyncMethod(AccessTools.PropertySetter(vacuumLightType, "ConcerningVacuumLevel"));
            MP.RegisterSyncMethod(AccessTools.PropertySetter(vacuumLightType, "EvacuatePawns"));

            // Tribute dialog named actions and its direct postpone callback.
            MP.RegisterSyncMethod(worldComponentType, "PayTribute");
            MP.RegisterSyncMethod(worldComponentType, "SpawnActiveWarplatform");
            MP.RegisterSyncMethod(typeof(VanillaGravshipExpanded2), nameof(SyncedPostponeTribute));
            var postpone = MpMethodUtil.GetLambda(worldComponentType, "ShowTributeDemandDialog", 0);
            MpCompat.harmony.Patch(postpone,
                prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded2), nameof(PrePostponeTribute)));

            // The salvager-station comms option issues a pawn job from a mod-added delegate.
            MpCompat.RegisterLambdaDelegate(
                "VanillaGravshipExpanded2.Building_CommsConsole_GetFloatMenuOptions_Patch", "Postfix", 0);

            // Capture the complete selected warpod group at the final destination click.
            var launchWarpod = AccessTools.Method(warpodType, "LaunchWarpodTo");
            var launchHellpod = AccessTools.Method(warpodType, "LaunchHellpodTo");
            MpCompat.harmony.Patch(launchWarpod,
                prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded2), nameof(PreLaunchWarpod)));
            MpCompat.harmony.Patch(launchHellpod,
                prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded2), nameof(PreLaunchHellpod)));
            MP.RegisterSyncMethod(typeof(VanillaGravshipExpanded2), nameof(SyncedLaunchWarpods));
            MP.RegisterSyncMethod(typeof(VanillaGravshipExpanded2), nameof(SyncedLaunchHellpods));

            // Developer-only energy/heat/cooldown gizmos.
            MpCompat.RegisterLambdaMethod(
                "VanillaGravshipExpanded2.CompGravshipShieldGeneratorWithHeat", "CompGetGizmosExtra", 0, 1, 2)
                .SetDebugOnly();
            MpCompat.RegisterLambdaMethod(
                "VanillaGravshipExpanded2.CompPower_InputOnlyBattery", "CompGetGizmosExtra", 0, 1, 2)
                .SetDebugOnly();
            MpCompat.RegisterLambdaMethod(
                "VanillaGravshipExpanded2.CompApparelVerbOwner_Oxygen", "CompGetWornGizmosExtra", 0)
                .SetDebugOnly();
        }

        private static void SyncMapComponent(SyncWorker sync, ref object component)
        {
            if (sync.isWriting)
                sync.Write(((MapComponent)component).map);
            else
            {
                var map = sync.Read<Map>();
                component = map?.components.FirstOrDefault(x => x.GetType() == mapComponentType);
            }
        }

        private static void SyncWorldComponent(SyncWorker sync, ref object component)
        {
            if (!sync.isWriting)
                component = worldComponentInstanceField.GetValue(null);
        }

        private static bool PrePostponeTribute()
        {
            if (!MP.IsInMultiplayer)
                return true;
            if (!MP.IsExecutingSyncCommand)
                SyncedPostponeTribute();
            return false;
        }

        private static void SyncedPostponeTribute()
        {
            var component = worldComponentInstanceField.GetValue(null);
            var engine = activeEngineProperty.GetValue(null) as Thing;
            if (component == null || engine == null)
                return;

            engineLockedField.SetValue(component, true);
            var delayTicks = (int)salvagerDelayDaysField.GetValue(component) * GenDate.TicksPerDay;
            var cooldownField = AccessTools.Field(engine.GetType(), "cooldownCompleteTick");
            var cooldown = (int)cooldownField.GetValue(engine);
            if (cooldown < Find.TickManager.TicksGame)
                cooldownField.SetValue(engine, Find.TickManager.TicksGame + delayTicks);
            tributeDemandTickField.SetValue(component, Find.TickManager.TicksGame + delayTicks);
        }

        private static bool PreLaunchWarpod(object __instance, Map destMap, PlanetTile destTile, IntVec3 destCell)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            SyncedLaunchWarpods(GetSelectedWarpodParents(), destMap, destTile, destCell);
            return false;
        }

        private static bool PreLaunchHellpod(object __instance, MapParent mapParent, PawnsArrivalModeDef arrivalMode)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            SyncedLaunchHellpods(GetSelectedWarpodParents(), mapParent, arrivalMode);
            return false;
        }

        private static Thing[] GetSelectedWarpodParents()
        {
            var selected = selectedWarpodsField.GetValue(null) as IEnumerable;
            return selected?.Cast<ThingComp>().Select(x => x.parent).ToArray() ?? Array.Empty<Thing>();
        }

        private static void SyncedLaunchWarpods(Thing[] warpods, Map destMap, PlanetTile destTile, IntVec3 destCell)
        {
            var first = RestoreSelectedWarpods(warpods);
            if (first != null)
                AccessTools.Method(warpodType, "LaunchWarpodTo").Invoke(first, new object[] { destMap, destTile, destCell });
        }

        private static void SyncedLaunchHellpods(Thing[] warpods, MapParent mapParent, PawnsArrivalModeDef arrivalMode)
        {
            var first = RestoreSelectedWarpods(warpods);
            if (first != null)
                AccessTools.Method(warpodType, "LaunchHellpodTo").Invoke(first, new object[] { mapParent, arrivalMode });
        }

        private static object RestoreSelectedWarpods(Thing[] warpods)
        {
            var list = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(warpodType));
            foreach (var thing in warpods)
            {
                var comp = (thing as ThingWithComps)?.AllComps.FirstOrDefault(x => x.GetType() == warpodType);
                if (comp != null)
                    list.Add(comp);
            }
            selectedWarpodsField.SetValue(null, list);
            return list.Count > 0 ? list[0] : null;
        }
    }
}
