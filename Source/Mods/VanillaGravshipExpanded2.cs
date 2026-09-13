using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Vanilla Gravship Expanded 2 by Oskar Potocki and the Vanilla Expanded team</summary>
/// <see href="https://github.com/Vanilla-Expanded/VanillaGravshipExpanded2"/>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3799737423"/>
[MpCompatFor("vanillaexpanded.gravship2")]
public class VanillaGravshipExpanded2
{
    private static Type warpodType;

    private static AccessTools.FieldRef<IList> selectedWarpodsField;

    private static FastInvokeHandler launchWarpodMethod;
    private static FastInvokeHandler launchHellpodMethod;

    public VanillaGravshipExpanded2(ModContentPack mod)
    {
        // Patching these methods can initialize DefOf caches and load gizmo textures.
        LongEventHandler.ExecuteWhenFinished(LatePatch);
    }

    private static void LatePatch()
    {
        var mapComponentType = AccessTools.TypeByName("VanillaGravshipExpanded2.VGE2_MapComponent");
        var worldComponentType = AccessTools.TypeByName("VanillaGravshipExpanded2.WorldComponent_GravshipCombat");
        warpodType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompLaunchable_Warpod");
        var vacuumLightType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompVacuumWarningLight");
        var escapePodType = AccessTools.TypeByName("VanillaGravshipExpanded2.CompEscapePod");

        selectedWarpodsField = AccessTools.StaticFieldRefAccess<IList>(
            AccessTools.DeclaredField(warpodType, "selectedWarpodsToLaunch"));

        // Escape pods
        MP.RegisterSyncMethod(mapComponentType, "EvacuationActive");
        MP.RegisterSyncMethod(escapePodType, "ClaimIfNeeded");
        MpCompat.RegisterLambdaMethod(escapePodType, nameof(ThingComp.CompGetGizmosExtra), 1);
        MpCompat.RegisterLambdaDelegate(escapePodType, nameof(ThingComp.CompFloatMenuOptions), 0, 2, 3);

        // Vacuum warning lights
        MP.RegisterSyncMethod(vacuumLightType, "ConcerningVacuumLevel");
        MP.RegisterSyncMethod(vacuumLightType, "EvacuatePawns");

        // Salvager tribute dialog
        MP.RegisterSyncMethod(worldComponentType, "PayTribute");
        MP.RegisterSyncMethod(worldComponentType, "SpawnActiveWarplatform");
        MpCompat.RegisterLambdaDelegate(worldComponentType, "ShowTributeDemandDialog", 0);

        // Salvager station comms job
        MpCompat.RegisterLambdaDelegate(
            "VanillaGravshipExpanded2.Building_CommsConsole_GetFloatMenuOptions_Patch", "Postfix", 0);

        // Warpod launches operate on the complete selected group, which the mod stores in a static list.
        var launchWarpod = AccessTools.DeclaredMethod(warpodType, "LaunchWarpodTo");
        var launchHellpod = AccessTools.DeclaredMethod(warpodType, "LaunchHellpodTo");
        launchWarpodMethod = MethodInvoker.GetHandler(launchWarpod);
        launchHellpodMethod = MethodInvoker.GetHandler(launchHellpod);
        MpCompat.harmony.Patch(launchWarpod,
            prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded2), nameof(PreLaunchWarpod)));
        MpCompat.harmony.Patch(launchHellpod,
            prefix: new HarmonyMethod(typeof(VanillaGravshipExpanded2), nameof(PreLaunchHellpod)));
        MP.RegisterSyncMethod(typeof(VanillaGravshipExpanded2), nameof(SyncedLaunchWarpods));
        MP.RegisterSyncMethod(typeof(VanillaGravshipExpanded2), nameof(SyncedLaunchHellpods));

        // ProcessInput creates the map directly; MP reconstructs the designator and ignores its unused Event.
        var generateEmptyOrbitType = AccessTools.TypeByName("VanillaGravshipExpanded2.Designator_GenerateEmptyOrbit");
        MP.RegisterSyncMethod(generateEmptyOrbitType, nameof(Designator.ProcessInput))
            .SetContext(SyncContext.CurrentMap);

        // Developer gizmos
        // These callbacks capture no comp; their target is the compiler-generated singleton.
        MpCompat.RegisterLambdaDelegate(
                "VanillaGravshipExpanded2.CompPowerEmergencyGravshipGenerator", nameof(ThingComp.CompGetGizmosExtra), 0, 1, 2)
            .SetDebugOnly();
        MpCompat.RegisterLambdaMethod(
                "VanillaGravshipExpanded2.CompGravshipShieldGeneratorWithHeat", nameof(ThingComp.CompGetGizmosExtra), 0, 1, 2)
            .SetDebugOnly();
        MpCompat.RegisterLambdaMethod(
                "VanillaGravshipExpanded2.CompPower_InputOnlyBattery", nameof(ThingComp.CompGetGizmosExtra), 0, 1, 2)
            .SetDebugOnly();
        MpCompat.RegisterLambdaMethod(
                "VanillaGravshipExpanded2.CompApparelVerbOwner_Oxygen", "CompGetWornGizmosExtra", 0)
            .SetDebugOnly();
    }

    private static bool PreLaunchWarpod(Map destMap, PlanetTile destTile, IntVec3 destCell)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return true;

        SyncedLaunchWarpods(GetSelectedWarpodParents(), destMap.Parent, destTile, destCell);
        return false;
    }

    private static bool PreLaunchHellpod(MapParent mapParent, PawnsArrivalModeDef arrivalMode)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return true;

        SyncedLaunchHellpods(GetSelectedWarpodParents(), mapParent, arrivalMode);
        return false;
    }

    private static Thing[] GetSelectedWarpodParents()
    {
        return selectedWarpodsField()?.Cast<ThingComp>()
            .Select(comp => comp.parent)
            .Where(parent => parent != null)
            .ToArray() ?? Array.Empty<Thing>();
    }

    private static void SyncedLaunchWarpods(Thing[] warpods, MapParent destination, PlanetTile destTile, IntVec3 destCell)
    {
        // A Map argument would conflict with the source pods' map context during serialization.
        // World objects serialize by ID, so resolve the destination map only during replay.
        if (destination?.Map is not Map destMap)
            return;

        InvokeWithSelectedWarpods(warpods, launchWarpodMethod, destMap, destTile, destCell);
    }

    private static void SyncedLaunchHellpods(Thing[] warpods, MapParent mapParent, PawnsArrivalModeDef arrivalMode)
    {
        InvokeWithSelectedWarpods(warpods, launchHellpodMethod, mapParent, arrivalMode);
    }

    private static void InvokeWithSelectedWarpods(Thing[] warpods, FastInvokeHandler method, params object[] args)
    {
        var previousSelection = selectedWarpodsField();
        var selected = (IList)Activator.CreateInstance(previousSelection?.GetType()
            ?? typeof(System.Collections.Generic.List<>).MakeGenericType(warpodType));

        foreach (var thing in warpods)
        {
            var comp = (thing as ThingWithComps)?.AllComps.FirstOrDefault(warpodType.IsInstanceOfType);
            if (comp != null)
                selected.Add(comp);
        }

        if (selected.Count == 0)
            return;

        try
        {
            selectedWarpodsField() = selected;
            method(selected[0], args);
        }
        finally
        {
            selectedWarpodsField() = previousSelection;
        }
    }

}
