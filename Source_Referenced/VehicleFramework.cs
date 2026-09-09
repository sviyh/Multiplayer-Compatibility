using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using JetBrains.Annotations;
using Multiplayer.API;
using RimWorld;
using RimWorld.Planet;
using SmashTools;
using UnityEngine;
using Vehicles;
using Vehicles.Rendering;
using Vehicles.World;
using Verse;
using Verse.AI.Group;
using Verse.Sound;

namespace Multiplayer.Compat
{
    /// <summary>Vehicle Framework by Smash Phil</summary>
    /// <see href="https://github.com/SmashPhil/Vehicle-Framework"/>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3014915404"/>
    [MpCompatFor("SmashPhil.VehicleFramework")]
    public class VehicleFramework
    {
        #region Fields

        // Mp Compat fields
        private static Type caravanFormingSessionType;
        private static Type caravanFormingProxyType;
        private static MethodInfo caravanFormingChooseRouteMethod;
        private static MethodInfo caravanFormingTrySendMethod;
        private static MethodInfo vehicleTryFormAndSendMethod;
        private static PlanetTile synchronizedVehicleStartingTile = PlanetTile.Invalid;

        // VehiclesModSettings
        private static ISyncField showAllCargoItemsField;

        // VehiclePawn.<>c__DisplayClass250_0
        private static AccessTools.FieldRef<object, VehiclePawn> vehiclePawnInnerClassParentField;
        
        // Designator_AreaRoad
        private static Designator_AreaRoad.RoadType localRoadType = Designator_AreaRoad.RoadType.Prioritize;

        #endregion

        #region Constructor

        public VehicleFramework(ModContentPack mod)
        {
            // Vehicle Framework initializes several patch targets and defs during play-data loading.
            LongEventHandler.ExecuteWhenFinished(LatePatch);
        }

        #endregion

        #region Main patch

        private static void LatePatch()
        {
            #region MP Compat

            MethodInfo method;

            // Only for optional overrides on dynamically discovered add-on types. The type/name API
            // also resolves inherited methods, which would duplicate the base registration here.
            static ISyncMethod TrySyncDeclaredMethod(Type targetType, string targetMethodName)
            {
                var declaredMethod = AccessTools.DeclaredMethod(targetType, targetMethodName);
                if (declaredMethod != null)
                    return MP.RegisterSyncMethod(declaredMethod);
                return null;
            }

            // Required because this compat class defers its registrations until play data is loaded.
            MpCompatPatchLoader.LoadPatch<VehicleFramework>();

            // Needed for overlay fix.
            PatchingUtilities.PatchLongEventMarkers();

            #endregion

            #region VehicleFramework

            #region Multithreading

            {
                // Path and region grids affect simulation decisions. Keep every grid update on the
                // deterministic game thread rather than allowing completion timing to differ by peer.
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredPropertyGetter(typeof(VehiclePathingSystem), nameof(VehiclePathingSystem.ThreadAvailable)),
                    postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(DisableVehiclePathingThreadsInMultiplayer)));
            }

            #endregion

            #region Gizmos

            {
                // Buildings

                // Switch to next verb (0), cancel target (1)
                MpCompat.RegisterLambdaMethod(typeof(Building_Artillery), nameof(Building_Artillery.GetGizmos), 0, 1);


                // Vehicle pawn

                // Ordinals verified from compiled DLL IL (release build):
                // Cancel load cargo (5), fish toggle (8), cancel forming caravan (15)
                // DisembarkAll is a method reference (no ordinal).
                // Load cargo (6) opens dialog, handled by LoadVehicleCargoSession.
                // Fish isActive (7) is a getter, doesn't need syncing.
                // Disembark all — method reference (no ordinal), sync directly
                MP.RegisterSyncMethod(typeof(VehiclePawn), nameof(VehiclePawn.DisembarkAll));
                // Haul pawn target callback (10) — called when player selects a pawn in HaulTargeter
                MpCompat.RegisterLambdaDelegate(typeof(VehiclePawn), nameof(VehiclePawn.GetGizmos), 5, 8, 10, 15);

                // VehicleRoleHandler isn't a supported parent in Multiplayer's native held-Thing
                // serializer. Identify the pawn through its synced vehicle instead.
                MP.RegisterSyncMethod(typeof(VehiclePawn), nameof(VehiclePawn.DisembarkPawn))
                    .TransformArgument(0, Serializer.New(
                        (Pawn pawn, object target, object[] _) =>
                            (vehicle: (VehiclePawn)target, pawnId: pawn.thingIDNumber),
                        tuple => tuple.vehicle?.AllPawnsAboard
                            .FirstOrDefault(pawn => pawn.thingIDNumber == tuple.pawnId)))
                    .CancelIfAnyArgNull();

                // Use VF's vehicle-aware departure action both from its own gizmo and from the
                // vanilla force-departure confirmation shown on pawns in a vehicle caravan.
                MP.RegisterSyncMethod(
                        typeof(LordJob_FormAndSendVehicles),
                        nameof(LordJob_FormAndSendVehicles.ForceCaravanLeave))
                    .TransformTarget(Serializer.New(
                        (LordJob_FormAndSendVehicles job) => job.lord,
                        (Lord lord) => lord?.LordJob as LordJob_FormAndSendVehicles));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(
                        typeof(CaravanFormingUtility),
                        nameof(CaravanFormingUtility.ForceCaravanDepart)),
                    prefix: new HarmonyMethod(
                        typeof(VehicleFramework),
                        nameof(RedirectVehicleCaravanForceDeparture)));

                // State-changing developer actions. Menu/targeter openers and visual-only actions stay local.
                MpCompat.RegisterLambdaDelegate(typeof(VehiclePawn), nameof(VehiclePawn.GetGizmos), 17, 19, 22, 24, 25, 26, 28)
                    .SetDebugOnly();

                // Toggle drafted or (if moving) engage brakes.
                MpCompat.RegisterLambdaMethod(typeof(VehicleIgnitionController), nameof(VehicleIgnitionController.GetGizmos), 1);

                // Comps

                // Target fuel level setter, used from Gizmo_RefuelableFuelTravel
                MP.RegisterSyncMethod(typeof(CompFueledTravel), nameof(CompFueledTravel.TargetFuelPercent));
                // Refuel from inventory, used from Gizmo_RefuelableFuelTravel
                MP.RegisterSyncMethod(typeof(CompFueledTravel), nameof(CompFueledTravel.Refuel), [typeof(List<Thing>)]);
                MP.RegisterSyncMethod(typeof(CompFueledTravel), nameof(CompFueledTravel.Refuel), [typeof(float)]);
                // CompGetGizmosExtra has no lambdas now (refuelGizmo is a cached field)
                // Auto-refuel toggle, charging toggle, and refuel-from-cargo moved to Gizmo_RefuelableFuelTravel.
                // Refuel from cargo opens Dialog_Slider → calls ConsumeFuelFromInventory
                MP.RegisterSyncMethod(typeof(CompFueledTravel), nameof(CompFueledTravel.ConsumeFuelFromInventory));
                MP.RegisterSyncMethod(typeof(Gizmo_RefuelableFuelTravel), nameof(Gizmo_RefuelableFuelTravel.ToggleAutoRefuel));
                MP.RegisterSyncMethod(typeof(Gizmo_RefuelableFuelTravel), nameof(Gizmo_RefuelableFuelTravel.ToggleCharging));
                MP.RegisterSyncMethod(typeof(CompFueledTravel), nameof(CompFueledTravel.ConsumeFuel), [typeof(float)])
                    .SetDebugOnly();
                // (Dev) set fuel to 0 (0), set fuel to max (1), set fuel to 99.99% (2)
                // RefuelHalfway is a method reference (not a lambda), so doesn't consume an ordinal
                // Only 99.99% needs an atomic lambda; the other actions reach the named methods above.
                MpCompat.RegisterLambdaMethod(typeof(CompFueledTravel), nameof(CompFueledTravel.DevModeGizmos), 2).SetDebugOnly();

                MP.RegisterSyncMethod(typeof(CompVehicleTurrets), nameof(CompVehicleTurrets.SetQuotaLevel));
                // Deploy turret is now a cached field (deployToggle), no lambda to register
                // The dev gizmo's iterator closure is not a stable sync target: synchronizing the
                // delegate can execute only on the issuing peer. Sync the underlying action instead.
                MP.RegisterSyncMethod(typeof(CompVehicleTurrets), "DevModeReloadTurret").SetDebugOnly();


                // Turret syncing in separate region

                // `Vehicles.Gizmos` includes patches to add or edit gizmos in existing GetGizmos methods:
                // `AddVehicleGizmosPassthrough` opens `Dialog_FormVehicleCaravan`, which we need to handle instead.
                // `GizmosForVehicleCaravans` calls `CaravanFormingUtility.LateJoinFormingCaravan`, which is synced through MP.

                // Pause movement (1) and toggle repairs (3).
                MpCompat.RegisterLambdaMethod(typeof(VehicleCaravan), nameof(VehicleCaravan.GetGizmos), 1, 3);
                // Down pawn (5), kill pawn (7), teleport (9), and repair all vehicles (10).
                MpCompat.RegisterLambdaMethod(typeof(VehicleCaravan), nameof(VehicleCaravan.GetGizmos), 5, 7, 9, 10)
                    .SetDebugOnly();
                // Land at a player settlement and initiate a crash.
                MP.RegisterSyncMethod(typeof(Patch_Debug), nameof(Patch_Debug.DebugLandAerialVehicle))
                    .SetDebugOnly();
                MP.RegisterSyncMethod(typeof(AerialVehicleInFlight), nameof(AerialVehicleInFlight.InitiateCrashEvent))
                    .SetDebugOnly();

                // Multiplayer deliberately marks its proxy as previously opened so vanilla keeps
                // the session-owned transferables. Let Vehicle Framework initialize its own tabs
                // without changing that vanilla lifecycle flag.
                caravanFormingSessionType = AccessTools.TypeByName("Multiplayer.Client.CaravanFormingSession");
                caravanFormingProxyType = AccessTools.TypeByName("Multiplayer.Client.CaravanFormingProxy");
                caravanFormingChooseRouteMethod = caravanFormingSessionType == null
                    ? null
                    : AccessTools.DeclaredMethod(caravanFormingSessionType, "ChooseRoute");
                caravanFormingTrySendMethod = caravanFormingSessionType == null
                    ? null
                    : AccessTools.DeclaredMethod(caravanFormingSessionType, "TryFormAndSendCaravan");

                var vehicleFormCaravanPatchType = typeof(Patch_FormCaravanDialog);
                var createTabListPostOpen = AccessTools.DeclaredMethod(
                    vehicleFormCaravanPatchType,
                    "CreateTabListPostOpen");
                if (createTabListPostOpen != null && caravanFormingProxyType != null)
                {
                    MpCompat.harmony.Patch(
                        createTabListPostOpen,
                        prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PrepareVehicleTabsForCaravanFormingProxy)));
                }

                // Vehicle Framework uses its own route callback, bypassing Multiplayer's
                // synchronized Dialog_FormCaravan.Notify_ChoseRoute hook. Route it back through
                // that public dialog method so Multiplayer's existing caravan session handles it.
                var vehicleChoseRouteMethod = MpMethodUtil.GetLocalFunc(
                    vehicleFormCaravanPatchType,
                    "WorldRoutePannerReroute",
                    localFunc: "ChoseVehicleRoute");
                if (vehicleChoseRouteMethod != null &&
                    caravanFormingProxyType != null &&
                    caravanFormingChooseRouteMethod != null)
                {
                    MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedChooseVehicleCaravanRoute));
                    MpCompat.harmony.Patch(
                        vehicleChoseRouteMethod,
                        prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(SyncVehicleCaravanRoute)));
                    MpCompat.harmony.Patch(
                        AccessTools.DeclaredMethod(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.Notify_ChoseRoute)),
                        transpiler: new HarmonyMethod(typeof(VehicleFramework), nameof(UseSynchronizedVehicleStartingTile)));

                    // VF keeps a shuffled, process-local edge-cell cache. Rebuild it from the map's
                    // canonical edge order before every MP use so prior UI calls cannot affect simulation.
                    MpCompat.harmony.Patch(
                        AccessTools.DeclaredMethod(typeof(CellFinderExtended), "CacheAndShuffleMapEdgeCells"),
                        prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(ResetVehicleEdgeCellCache)));
                }

                // Keep Vehicle Framework's validation and warning dialogs local. Once its final
                // formation function is reached, route the action through Multiplayer's existing
                // caravan-forming session and execute that same function on the session dummy.
                vehicleTryFormAndSendMethod = MpMethodUtil.GetLocalFunc(
                    typeof(CaravanFormation),
                    nameof(CaravanFormation.TrySendVehicleCaravan),
                    localFunc: "TryFormAndSendCaravan");
                if (vehicleTryFormAndSendMethod != null &&
                    caravanFormingTrySendMethod != null)
                {
                    MpCompat.harmony.Patch(
                        vehicleTryFormAndSendMethod,
                        prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(RedirectVehicleCaravanSendToSession)));
                    MpCompat.harmony.Patch(
                        AccessTools.DeclaredMethod(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.TryFormAndSendCaravan)),
                        prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(FormVehicleCaravanFromSessionDummy)));
                }

            }

            #endregion

            #region Turrets

            {
                // We need to selectively pick which "FireTurret" method get synced.
                // It would be beneficial if we could sync `FireTurrets` method, as it would
                // prevent a lot of duplicated syncing of "FireTurret" if there are multiple turrets.
                // However, the issue is that not all calls to "FireTurret" are an action we want to sync,
                // for example when it opens targeter.

                // Static turrets
                MP.RegisterSyncMethod(typeof(Command_CooldownAction), nameof(Command_CooldownAction.FireTurret));
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Command_CooldownAction), nameof(Command_CooldownAction.GizmoOnGUI)),
                    transpiler: new HarmonyMethod(typeof(VehicleFramework), nameof(ReplaceSetTargetCall)));

                // Targetable turrets
                method = MpMethodUtil.GetLambda(typeof(Command_TargeterCooldownAction), nameof(Command_TargeterCooldownAction.FireTurret), lambdaOrdinal: 0);
                var targetableTurretField = AccessTools.FieldRefAccess<VehicleTurret>(method.DeclaringType, "turret");
                MP.RegisterSyncDelegateLambda(typeof(Command_TargeterCooldownAction), nameof(Command_TargeterCooldownAction.FireTurret), 0)
                    .SetPreInvoke((target, _) => ResetTurretTarget(targetableTurretField(target)));

                // Called from Vehicles.TurretTargeter:BeginTargeting
                PatchingUtilities.PatchCancelInInterface(AccessTools.DeclaredMethod(typeof(VehicleTurret), nameof(VehicleTurret.StartTicking)));
                // Called from Vehicles.TurretTargeter:TargeterUpdate and Vehicles.TurretTargeter:StopTargeting(bool)
                PatchingUtilities.PatchCancelInInterface(AccessTools.DeclaredMethod(typeof(VehicleTurret), nameof(VehicleTurret.AlignToAngleRestricted)));
                MP.RegisterSyncMethod(typeof(VehicleTurret), nameof(VehicleTurret.CycleFireMode));
                MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncSetTarget));
                // Register overrides supplied by Vehicle Framework and vehicle add-ons.
                foreach (var subclass in typeof(VehicleTurret).AllSubclasses().Concat(typeof(VehicleTurret)))
                {
                    // Reload has multiple overloads — sync both parameterless and (ThingDef, bool)
                    // VVE's FueledVehicleTurret.SubGizmo_ReloadFromFuel calls Reload(null, true)
                    var reloadMethod = AccessTools.DeclaredMethod(subclass, nameof(VehicleTurret.Reload), []);
                    if (reloadMethod != null)
                        MP.RegisterSyncMethod(reloadMethod);
                    var reloadWithArgs = AccessTools.DeclaredMethod(subclass, nameof(VehicleTurret.Reload), [typeof(ThingDef), typeof(bool)]);
                    if (reloadWithArgs != null)
                        MP.RegisterSyncMethod(reloadWithArgs);
                    // TryClearChamber — used by VVE's FueledVehicleTurret.SubGizmo_AmmoToFuel
                    TrySyncDeclaredMethod(subclass, nameof(VehicleTurret.TryClearChamber));
                    TrySyncDeclaredMethod(subclass, nameof(VehicleTurret.SwitchAutoTarget));
                }

                // Stop the call from interface, called from TurretRotation getter. We update it during ticking.
                PatchingUtilities.PatchCancelInInterface(AccessTools.DeclaredMethod(typeof(VehicleTurret), nameof(VehicleTurret.UpdateRotationLock)));
            }

            #endregion

            #region Float Menus

            {
                // Entering a vehicle also updates Vehicle Framework state before issuing the job.
                MpCompat.RegisterLambdaDelegate(typeof(VehiclePawn), nameof(VehiclePawn.GetFloatMenuOptions), 0);
                // MultiplePawnFloatMenuOptions now uses OrderPawns method reference, no lambda to sync
                // The boarding action is handled through the method reference directly.

                // Multi-selection cargo commands depend on the initiating player's selection.
                MP.RegisterSyncMethod(typeof(Command_TransferToVehicle_Order), nameof(Command_TransferToVehicle_Order.Action))
                    .SetContext(SyncContext.MapSelected);
                MP.RegisterSyncMethod(typeof(Command_TransferToVehicle_Cancel), nameof(Command_TransferToVehicle_Cancel.Action))
                    .SetContext(SyncContext.MapSelected);
            }

            #endregion

            #region RNG

            {
                // Motes
                PatchingUtilities.PatchPushPopRand(new[]
                {
                    "Vehicles.Verb_ShootRealistic:InitTurretMotes",
                    "Vehicles.VehicleTurret:InitTurretMotes",
                    "Vehicles.CompFueledTravel:DrawMotes",
                });
                // The two instance overloads choose cosmetic fleck parameters before the static
                // overload enters Vehicle Framework's own Rand.PushState scope.
                foreach (var throwFleck in AccessTools.GetDeclaredMethods(typeof(LaunchProtocol))
                    .Where(m => m.Name == nameof(LaunchProtocol.ThrowFleck) && !m.IsStatic))
                    PatchingUtilities.PatchPushPopRand(throwFleck);
            }

            #endregion

            #region DrawAt determinism

            {
                // VehiclePawn.DrawAt(in Vector3, Rot8, float) uses `in` keyword which
                // MpCompatPrefix attribute can't match. Patch manually.
                var drawAtMethod = AccessTools.DeclaredMethod(typeof(VehiclePawn), nameof(VehiclePawn.DrawAt),
                    [typeof(Vector3).MakeByRefType(), typeof(Rot8), typeof(float)]);
                if (drawAtMethod != null)
                {
                    MpCompat.harmony.Patch(drawAtMethod,
                        prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreRenderPawnInternal)),
                        finalizer: new HarmonyMethod(typeof(VehicleFramework), nameof(PostRenderPawnInternal)));
                }
            }

            #endregion

            #region Dialogs

            {
                #region Other

                // Called when accepted from change color dialog
                MpCompat.RegisterLambdaMethod("Vehicles.VehiclePawn", "ChangeColor", 0);

                // Seat assignment is edited in a local dialog but consumed by caravan formation.
                MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedSetSeatAssignments));
                MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedRemoveSeatAssignments));
                MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedClearSeatAssignments));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(VehicleAssignment), nameof(VehicleAssignment.SetAssignments)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreSetSeatAssignments)));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(VehicleAssignment), nameof(VehicleAssignment.RemoveAssignments)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreRemoveSeatAssignments)));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(VehicleAssignment), nameof(VehicleAssignment.Clear)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreClearSeatAssignments)));

                // Stashing vehicles creates and destroys world objects from a custom transfer dialog.
                MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedStashVehicles));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(Dialog_StashVehicle), "TransferPawns"),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreStashVehicles)));

                #endregion

                #region Load cargo

                // Sync creation of session
                MP.RegisterSyncMethod(typeof(LoadVehicleCargoSession), nameof(LoadVehicleCargoSession.CreateLoadVehicleCargoSession));
                // Sync load cargo session methods
                MP.RegisterSyncMethod(typeof(LoadVehicleCargoSession), nameof(LoadVehicleCargoSession.Accept));
                MP.RegisterSyncMethod(typeof(LoadVehicleCargoSession), nameof(LoadVehicleCargoSession.Reset));
                MP.RegisterSyncMethod(typeof(LoadVehicleCargoSession), nameof(LoadVehicleCargoSession.PackInstantly));
                MP.RegisterSyncMethod(typeof(LoadVehicleCargoSession), nameof(LoadVehicleCargoSession.SetToSendEverything));
                MP.RegisterSyncMethod(typeof(LoadVehicleCargoSession), nameof(LoadVehicleCargoSession.Remove));

                // Setting value is changeable from load cargo dialog
                showAllCargoItemsField = MP.RegisterSyncField(typeof(VehiclesModSettings), nameof(VehiclesModSettings.showAllCargoItems))
                    .PostApply(PostShowAllCargoItemsChanged);
                // Resolve the mod's singleton settings instance when the sync field is applied.
                MP.RegisterSyncWorker<VehiclesModSettings>(SyncVehicleSettings);

                // Capture drawing so we can tie the dialog to the session and set the correct current session with transferables.
                // Mp prefers making a subclass of the session itself for it, we're doing it by patching it to avoid making extra classes.
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Dialog_LoadCargo), nameof(Dialog_LoadCargo.DoWindowContents)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreDrawLoadCargo)),
                    finalizer: new HarmonyMethod(typeof(VehicleFramework), nameof(FinalizeDrawLoadCargo)));

                // Catch dev option to select everything to be sent
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Dialog_LoadCargo), "SetToSendEverything"),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreLoadCargoSetToSendEverything)));

                // Route state-changing buttons through the synchronized cargo session using the
                // same widget interception points as Multiplayer's own persistent dialogs.
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(Widgets), nameof(Widgets.ButtonText),
                        [typeof(Rect), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(TextAnchor?)]),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreLoadCargoButtonText)),
                    postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(PostLoadCargoButtonText)));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(Widgets), nameof(Widgets.ButtonTextWorker)),
                    postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(PostLoadCargoButtonTextWorker)));

                // Catch load cargo dialog gizmo to open session dialog tied to it or create session if there's none
                // Ordinal 6 = open Dialog_LoadCargo (verified from compiled DLL IL)
                method = MpMethodUtil.GetLambda(typeof(VehiclePawn), nameof(VehiclePawn.GetGizmos), lambdaOrdinal: 6);
                vehiclePawnInnerClassParentField = AccessTools.FieldRefAccess<VehiclePawn>(method.DeclaringType, "<>4__this");
                MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreLoadCargoDialog)));

                // Prevent the call in MP, as it'll mess with transferables if re-opening the window.
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(Dialog_LoadCargo), "CalculateAndRecacheTransferables"),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreLoadCargoCalculateAndRecache)));

                #endregion

                #region Shared

                // Insert "Switch to map" button to the dialogs with session
                var types = new[]
                {
                    typeof(Dialog_LoadCargo),
                };

                foreach (var type in types)
                {
                    MpCompat.harmony.Patch(
                        AccessTools.DeclaredMethod(type, nameof(Window.DoWindowContents), [typeof(Rect)]),
                        postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(InsertSwitchToMap)));
                }

                #endregion
            }

            #endregion

            #region ITabs and WITabs

            {
                // Register the base container tab and its concrete variants.
                foreach (var type in typeof(ITab_Airdrop_Container).AllSubclasses().Concat(typeof(ITab_Airdrop_Container)))
                {
                    TrySyncDeclaredMethod(type, "InterfaceDrop")?.SetContext(SyncContext.MapSelected);
                    TrySyncDeclaredMethod(type, "InterfaceDropAll")?.SetContext(SyncContext.MapSelected);
                }

                // Used by Vehicles.ITab_Vehicle_Passengers and Vehicles.WITab_Vehicle_Manifest
                method = AccessTools.DeclaredMethod(typeof(VehicleTabHelper_Passenger), nameof(VehicleTabHelper_Passenger.HandleDragEvent));
                MpCompat.harmony.Patch(method, prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreHandleDragEvent)));
                MP.RegisterSyncMethod(typeof(VehicleFramework), nameof(SyncedHandleDragEvent));

                // WITab_AerialVehicle_Items
                // Aerial vehicle inventory tab
                var typesThing = new[] { typeof(Thing), typeof(AerialVehicleInFlight) };
                var typesTransferable = new[] { typeof(TransferableImmutable), typeof(AerialVehicleInFlight) };

                // Abandon non-pawn Thing
                MP.RegisterSyncDelegateLambda(
                    typeof(AerialVehicleAbandonOrBanishHelper),
                    nameof(AerialVehicleAbandonOrBanishHelper.TryAbandonOrBanishViaInterface),
                    0,
                    typesThing);
                // Pawn banishment reaches Multiplayer's existing PawnBanishUtility.Banish sync method.

                // Abandon non-pawn Transferable
                MP.RegisterSyncDelegateLambda(
                    typeof(AerialVehicleAbandonOrBanishHelper),
                    nameof(AerialVehicleAbandonOrBanishHelper.TryAbandonOrBanishViaInterface),
                    0,
                    typesTransferable);

                // Abandon specific count Thing
                MP.RegisterSyncDelegateLambda(
                    typeof(AerialVehicleAbandonOrBanishHelper),
                    nameof(AerialVehicleAbandonOrBanishHelper.TryAbandonSpecificCountViaInterface),
                    0,
                    typesThing);

                // Abandon specific count Transferable
                MP.RegisterSyncDelegateLambda(
                    typeof(AerialVehicleAbandonOrBanishHelper),
                    nameof(AerialVehicleAbandonOrBanishHelper.TryAbandonSpecificCountViaInterface),
                    0,
                    typesTransferable);

                // CompUpgradeTree, methods called from ITab_Vehicle_Upgrades
                // Identify an upgrade node by its owning tree def and stable key.
                var upgradeNodeSerializer = Serializer.New(
                    (UpgradeNode upgrade, object target, object[] _) => (target: ((CompUpgradeTree)target).Props.def, key: upgrade.key),
                    tuple => tuple.target.GetNode(tuple.key)
                );
                // Start installing upgrade
                MP.RegisterSyncMethod(typeof(CompUpgradeTree), nameof(CompUpgradeTree.StartUnlock))
                    .TransformArgument(0, upgradeNodeSerializer);
                // Start removing upgrade
                MP.RegisterSyncMethod(typeof(CompUpgradeTree), nameof(CompUpgradeTree.RemoveUnlock))
                    .TransformArgument(0, upgradeNodeSerializer);
                // Cancel upgrading, doesn't use UpgradeNode
                MP.RegisterSyncMethod(typeof(CompUpgradeTree), nameof(CompUpgradeTree.ClearUpgrade));
                // Dev mode upgrade instantly
                MP.RegisterSyncMethod(typeof(CompUpgradeTree), nameof(CompUpgradeTree.FinishUnlock))
                    .TransformArgument(0, upgradeNodeSerializer)
                    .SetDebugOnly();
                // Dev mode remove upgrade
                MP.RegisterSyncMethod(typeof(CompUpgradeTree), nameof(CompUpgradeTree.ResetUnlock))
                    .TransformArgument(0, upgradeNodeSerializer)
                    .SetDebugOnly();

                // Per-turret ammunition enablement and reload quotas.
                MP.RegisterSyncMethod(typeof(AutoLoadConfig), nameof(AutoLoadConfig.SetEnabled));
                MP.RegisterSyncMethod(typeof(AutoLoadConfig), nameof(AutoLoadConfig.Set));
            }

            #endregion

            #region Flying vehicles

            {
                MP.RegisterSyncWorker<LaunchProtocol>(SyncLaunchProtocol, isImplicit: true);
                MP.RegisterSyncWorker<FlightNode>(SyncFlightNode);
                MP.RegisterSyncWorker<SmashTools.Targeting.TargetData<GlobalTargetInfo>>(SyncGlobalTargetData);

                MP.RegisterSyncMethod(typeof(CompVehicleLauncher), nameof(CompVehicleLauncher.Launch))
                    .ExposeParameter(1)
                    .SetPreInvoke(SetLaunchArrivalVehicle);
                MP.RegisterSyncMethod(typeof(AerialVehicleInFlight), nameof(AerialVehicleInFlight.OrderFlyToTiles))
                    .ExposeParameter(1)
                    .SetPreInvoke(SetOrderArrivalVehicle);
                MP.RegisterSyncMethod(typeof(VehicleCaravan), nameof(VehicleCaravan.Launch))
                    .ExposeParameter(1)
                    .SetPreInvoke(SetCaravanLaunchArrivalVehicle)
                    .SetPostInvoke(CleanupDestroyedCaravanAfterLaunch);
                MP.RegisterSyncMethod(typeof(LaunchProtocol), nameof(LaunchProtocol.StartTargetingLocalMap));

                // VF bug: VehicleSkyfaller_Leaving.ExposeData doesn't save arrivalAction.
                // In MP, saves can happen between skyfaller creation and LeaveMap, losing the action.
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(VehicleSkyfaller_Leaving), nameof(VehicleSkyfaller_Leaving.ExposeData)),
                    postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(PostSkyfallerLeavingExposeData)));

                // Also repair actions restored from a save before they enter the synced action path.
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(CompVehicleLauncher), nameof(CompVehicleLauncher.Launch)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreLaunchSetArrivalVehicle)));
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(AerialVehicleInFlight), nameof(AerialVehicleInFlight.OrderFlyToTiles)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreOrderFlySetArrivalVehicle)));

                // Ensure arrival action's vehicle reference is valid before arrival.
                // The vehicle Scribe_Reference may fail to resolve when inside AerialVehicleInFlight.
                // Patch MoveForward to fix the vehicle ref before ConsumeNode calls Arrived.
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(AerialVehicleInFlight), nameof(AerialVehicleInFlight.MoveForward)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreMoveForwardFixArrivalVehicle)));

                // VF's ResumePathPostLoad calls OrderFlyToTiles which resets transition to 0.
                // In MP, save/load cycles happen during sync, resetting flight progress.
                // Preserve transition and position across the reload.
                MpCompat.harmony.Patch(
                    AccessTools.DeclaredMethod(typeof(AerialVehicleInFlight), "ResumePathPostLoad"),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreResumePathPostLoad)),
                    postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(PostResumePathPostLoad)));

                // Aerial vehicles have multiple arrival actions at settlements
                // and the like. This specific ones orders the vehicle to fly to
                // a settlement and land on a specific tile, despite it not even
                // being loaded in the first place. Once the vehicle arrives it
                // load the map and forces the player to target the location to
                // land on. We need to sync the land-and-pick-cell interaction.

                // Capture and stop the vehicle arrival, instead starting a session
                MpCompat.harmony.Patch(AccessTools.DeclaredMethod(typeof(AerialVehicleArrivalModeWorker_TargetedDrop), nameof(AerialVehicleArrivalModeWorker_TargetedDrop.VehicleArrived)),
                    prefix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreTargetedDropVehicleArrival)));

                // Prevent the map from being removed if the landing session is active
                MpCompat.harmony.Patch(AccessTools.DeclaredPropertyGetter(typeof(MapPawns), nameof(MapPawns.AnyPawnBlockingMapRemoval)),
                    postfix: new HarmonyMethod(typeof(VehicleFramework), nameof(PreventMapRemovalForLandingSessions)) { after = ["SmashPhil.VehicleFramework"] });

                MP.RegisterSyncMethod(typeof(FlyingVehicleTargetedLandingSession), nameof(FlyingVehicleTargetedLandingSession.Remove));
                MP.RegisterSyncMethod(typeof(FlyingVehicleTargetedLandingSession), nameof(FlyingVehicleTargetedLandingSession.VehicleArrivalById));
            }

            #endregion

            #region SyncWorkers

            {
                MP.RegisterSyncWorker<Gizmo_RefuelableFuelTravel>(SyncFuelGizmo);
                // Turret
                MP.RegisterSyncWorker<Command_Turret>(SyncCommandTurret, typeof(Command_Turret), true, true);
                // Vehicle pawn elements
                MP.RegisterSyncWorker<VehicleComponent>(SyncVehicleComponent, isImplicit: true);
                MP.RegisterSyncWorker<VehicleTurret>(SyncVehicleTurret, isImplicit: true);
                MP.RegisterSyncWorker<AutoLoadConfig>(SyncAutoLoadConfig, isImplicit: true);
                MP.RegisterSyncWorker<VehicleIgnitionController>(SyncVehicleIgnitionController);
                MP.RegisterSyncWorker<VehicleRoleHandler>(SyncVehicleRoleHandler);
            }

            #endregion

            #endregion
        }

        #endregion

        #region Multithreading

        #region Disable multithreading

        private static void DisableVehiclePathingThreadsInMultiplayer(ref bool __result)
            => __result &= !MP.IsInMultiplayer;

        [MpCompatPrefix(typeof(VehiclePathingSystem), nameof(VehiclePathingSystem.MapComponentTick))]
        private static void StopExistingVehiclePathingThread(VehiclePathingSystem __instance)
        {
            if (MP.IsInMultiplayer && __instance.ThreadAlive)
                __instance.ReleaseThread();
        }

        [MpCompatTranspiler(typeof(VehiclePathFollower), nameof(VehiclePathFollower.RequestNewPath))]
        [MpCompatTranspiler(typeof(WorldVehiclePathGrid), "RecalculateAllPathCostsAsync")]
        [MpCompatTranspiler(typeof(WorldVehiclePathGrid), "RecalculateReachabilityGrid")]
        private static IEnumerable<CodeInstruction> RunVehiclePathfindingSynchronously(
            IEnumerable<CodeInstruction> instr,
            MethodBase baseMethod)
        {
            var target = AccessTools.DeclaredMethod(
                typeof(TaskManager),
                nameof(TaskManager.Run),
                [typeof(Action), typeof(CancellationToken)]);
            var replacement = MpMethodUtil.MethodOf(RunVehiclePathAction);

            return instr.ReplaceMethod(target, replacement, baseMethod, expectedReplacements: 1);
        }

        private static Task RunVehiclePathAction(Action action, CancellationToken token)
        {
            if (!MP.IsInMultiplayer)
                return TaskManager.Run(action, token);

            if (!token.IsCancellationRequested)
                action();

            return Task.CompletedTask;
        }

        #endregion

        #endregion

        #region Vehicle jobs

        [MpCompatPrefix(typeof(JobDriver_IdleVehicle), "MakeNewToils")]
        private static void RestoreMissingIdleVehicleTarget(JobDriver_IdleVehicle __instance)
        {
            if (MP.IsInMultiplayer && !__instance.job.targetA.IsValid)
                __instance.job.targetA = __instance.pawn;
        }

        #endregion

        #region Caravan seat assignment sync

        private static bool RedirectVehicleCaravanForceDeparture(Lord lord)
        {
            if (!MP.IsInMultiplayer || lord?.LordJob is not LordJob_FormAndSendVehicles vehicleJob)
                return true;

            vehicleJob.ForceCaravanLeave();
            return false;
        }

        private static bool PreSetSeatAssignments(VehicleAssignment __instance, VehiclePawn vehicle, List<AssignedSeat> assignments)
        {
            if (!ShouldSyncCaravanSeatAssignment(__instance))
                return true;

            SyncedSetSeatAssignments(
                vehicle,
                assignments.Select(assignment => assignment.pawn).ToList(),
                assignments.Select(assignment => assignment.handler).ToList());
            return false;
        }

        private static void SyncedSetSeatAssignments(
            VehiclePawn vehicle,
            List<Pawn> pawns,
            List<VehicleRoleHandler> handlers)
        {
            if (vehicle == null || pawns == null || handlers == null || pawns.Count != handlers.Count)
                return;

            var previouslyAssignedPawns = CaravanHelper.assignedSeats.GetAssignments(vehicle)
                .Select(assignment => assignment.pawn)
                .ToList();
            var assignments = new List<AssignedSeat>();
            for (var i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                var handler = handlers[i];
                if (pawn != null && handler?.vehicle == vehicle)
                    assignments.Add(new AssignedSeat(pawn, handler));
            }

            var session = GetCaravanFormingSession(vehicle.Map);
            foreach (var pawn in previouslyAssignedPawns)
                SetCaravanFormingTransferCount(session, pawn, 0);
            foreach (var pawn in assignments.Select(assignment => assignment.pawn))
                SetCaravanFormingTransferCount(session, pawn, 1);

            CaravanHelper.assignedSeats.SetAssignments(vehicle, assignments);

            var vehicleTransferable = session?.GetTransferableByThingId(vehicle.thingIDNumber);
            if (vehicleTransferable != null)
            {
                vehicleTransferable.AdjustTo(assignments.Count > 0 ? vehicleTransferable.GetMaximumToTransfer() : 0);
                session.Notify_CountChanged(vehicleTransferable);
            }
        }

        private static bool PreRemoveSeatAssignments(VehicleAssignment __instance, VehiclePawn vehicle)
        {
            if (!ShouldSyncCaravanSeatAssignment(__instance))
                return true;

            SyncedRemoveSeatAssignments(vehicle);
            return false;
        }

        private static void SyncedRemoveSeatAssignments(VehiclePawn vehicle)
        {
            if (vehicle == null)
                return;

            var previouslyAssignedPawns = CaravanHelper.assignedSeats.GetAssignments(vehicle)
                .Select(assignment => assignment.pawn)
                .ToList();

            var session = GetCaravanFormingSession(vehicle.Map);
            SetCaravanFormingTransferCount(session, vehicle, 0);
            foreach (var pawn in previouslyAssignedPawns.Where(pawn => pawn != null && !pawn.InVehicle()))
                SetCaravanFormingTransferCount(session, pawn, 0);
            foreach (var pawn in vehicle.AllPawnsAboard)
                SetCaravanFormingTransferCount(session, pawn, 0);

            CaravanHelper.assignedSeats.RemoveAssignments(vehicle);
        }

        private static bool PreClearSeatAssignments(VehicleAssignment __instance)
        {
            if (!ShouldSyncCaravanSeatAssignment(__instance))
                return true;

            SyncedClearSeatAssignments();
            return false;
        }

        private static void SyncedClearSeatAssignments()
            => CaravanHelper.assignedSeats.Clear();

        private static ISessionWithTransferables GetCaravanFormingSession(Map map)
            => map == null || caravanFormingSessionType == null
                ? null
                : MP.GetLocalSessionManager(map).AllSessions
                    .FirstOrDefault(caravanFormingSessionType.IsInstanceOfType) as ISessionWithTransferables;

        private static void SetCaravanFormingTransferCount(ISessionWithTransferables session, Thing thing, int count)
        {
            var transferable = thing == null ? null : session?.GetTransferableByThingId(thing.thingIDNumber);
            if (transferable == null)
                return;

            transferable.ForceTo(count);
            session.Notify_CountChanged(transferable);
        }

        private static bool ShouldSyncCaravanSeatAssignment(VehicleAssignment assignment)
            => MP.IsInMultiplayer
               && MP.InInterface
               && !MP.IsExecutingSyncCommand
               && ReferenceEquals(assignment, CaravanHelper.assignedSeats);

        #endregion

        #region Stash vehicle sync

        private static bool PreStashVehicles(Dialog_StashVehicle __instance, ref bool __result)
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            var things = new List<Thing>();
            var groupSizes = new List<int>();
            var counts = new List<int>();

            foreach (var transferable in __instance.transferables)
            {
                if (transferable.CountToTransfer <= 0)
                    continue;

                groupSizes.Add(transferable.things.Count);
                counts.Add(transferable.CountToTransfer);
                things.AddRange(transferable.things);
            }

            SyncedStashVehicles(__instance.caravan, things, groupSizes, counts);
            __result = true;
            return false;
        }

        private static void SyncedStashVehicles(
            VehicleCaravan caravan,
            List<Thing> things,
            List<int> groupSizes,
            List<int> counts)
        {
            if (caravan == null || things == null || groupSizes == null || counts == null ||
                groupSizes.Count != counts.Count || groupSizes.Sum() != things.Count)
                return;

            var transferables = new List<TransferableOneWay>();
            var thingIndex = 0;

            for (var groupIndex = 0; groupIndex < groupSizes.Count; groupIndex++)
            {
                var transferable = new TransferableOneWay();
                for (var i = 0; i < groupSizes[groupIndex]; i++, thingIndex++)
                {
                    var thing = things[thingIndex];
                    if (thing != null)
                        transferable.things.Add(thing);
                }

                if (transferable.things.Count > 0)
                {
                    transferable.AdjustTo(counts[groupIndex]);
                    transferables.Add(transferable);
                }
            }

            StashedVehicle.Create(caravan, out _, transferables);
        }

        #endregion

        #region ITabs and WITabs

        private static bool PreHandleDragEvent()
        {
            if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
                return true;

            // If the method wasn't going to handle the event, just cancel the method execution
            if (Event.current.type != EventType.MouseUp || Event.current.button != 0)
                return false;

            // If the method was going to handle the event, we're going to sync it instead
            SyncedHandleDragEvent(VehicleTabHelper_Passenger.draggedPawn, VehicleTabHelper_Passenger.hoveringOverPawn, VehicleTabHelper_Passenger.transferToHolder);
            // If the event was handled, the dragged pawn is set to null.
            VehicleTabHelper_Passenger.draggedPawn = null;
            return false;
        }

        private static void SyncedHandleDragEvent(Pawn dragged, Pawn hovering, IThingHolder holder)
        {
            // Get current values as temporary values
            var currentDraggedPawn = VehicleTabHelper_Passenger.draggedPawn;
            var currentHoveringPawn = VehicleTabHelper_Passenger.hoveringOverPawn;
            var currentTransferToHolder = VehicleTabHelper_Passenger.transferToHolder;
            var currentEvent = Event.current;

            try
            {
                // Change fields to the synced values
                VehicleTabHelper_Passenger.draggedPawn = dragged;
                VehicleTabHelper_Passenger.hoveringOverPawn = hovering;
                VehicleTabHelper_Passenger.transferToHolder = holder;
                Event.current = new Event
                {
                    type = EventType.MouseUp,
                    button = 0,
                };
                // Run the original method with values we synced
                VehicleTabHelper_Passenger.HandleDragEvent();
            }
            finally
            {
                // Restore fields to their previous values
                VehicleTabHelper_Passenger.draggedPawn = currentDraggedPawn;
                VehicleTabHelper_Passenger.hoveringOverPawn = currentHoveringPawn;
                VehicleTabHelper_Passenger.transferToHolder = currentTransferToHolder;
                Event.current = currentEvent;
            }
        }

        #endregion

        private static void PrepareVehicleTabsForCaravanFormingProxy(
            Dialog_FormCaravan formCaravan,
            List<TabRecord> tabsList,
            ref bool thisWindowInstanceEverOpened)
        {
            if (MP.IsInMultiplayer &&
                caravanFormingProxyType.IsInstanceOfType(formCaravan) &&
                tabsList.Count == 0)
                thisWindowInstanceEverOpened = false;
        }

        [MpCompatPostfix(typeof(VehicleRoutePlanner), nameof(VehicleRoutePlanner.ShouldStop), methodType: MethodType.Getter)]
        private static void KeepVehicleRoutePlannerOpen(VehicleRoutePlanner __instance, ref bool __result)
        {
            if (MP.IsInMultiplayer && __result && __instance.IsActive && WorldRendererUtility.WorldSelected)
                __result = false;
        }

        private static bool SyncVehicleCaravanRoute(PlanetTile tile)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface)
                return true;

            var formation = CaravanFormation.formation;
            if (formation == null ||
                !caravanFormingProxyType.IsInstanceOfType(formation.Dialog) ||
                !tile.Valid)
                return true;

            var vehicles = TransferableUtility.GetPawnsFromTransferables(formation.Dialog.transferables)
                .OfType<VehiclePawn>()
                .ToList();
            if (vehicles.Count == 0)
                return true;

            PlanetTile startingTile;
            Rand.PushState();
            try
            {
                startingTile = CaravanHelper.BestExitTileToGoTo(
                    vehicles.Select(vehicle => vehicle.VehicleDef).Distinct().ToList(),
                    tile,
                    formation.Map);
            }
            finally
            {
                Rand.PopState();
            }

            SyncedChooseVehicleCaravanRoute(formation.Map, tile, startingTile);
            return false;
        }

        private static void SyncedChooseVehicleCaravanRoute(
            Map map,
            PlanetTile destinationTile,
            PlanetTile startingTile)
        {
            var session = GetCaravanFormingSession(map);
            if (session == null)
                return;

            synchronizedVehicleStartingTile = startingTile;
            try
            {
                caravanFormingChooseRouteMethod.Invoke(session, new object[] { destinationTile });
            }
            finally
            {
                synchronizedVehicleStartingTile = PlanetTile.Invalid;
            }
        }

        private static IEnumerable<CodeInstruction> UseSynchronizedVehicleStartingTile(
            IEnumerable<CodeInstruction> instr,
            MethodBase baseMethod)
        {
            var target = AccessTools.DeclaredMethod(
                typeof(CaravanExitMapUtility),
                nameof(CaravanExitMapUtility.BestExitTileToGoTo),
                [typeof(PlanetTile), typeof(Map)]);
            var replacement = MpMethodUtil.MethodOf(GetSynchronizedVehicleStartingTile);

            return instr.ReplaceMethod(target, replacement, baseMethod, expectedReplacements: 1);
        }

        private static PlanetTile GetSynchronizedVehicleStartingTile(
            PlanetTile destinationTile,
            Map map)
            => MP.IsInMultiplayer &&
               MP.IsExecutingSyncCommand &&
               synchronizedVehicleStartingTile.Valid
                ? synchronizedVehicleStartingTile
                : CaravanExitMapUtility.BestExitTileToGoTo(destinationTile, map);

        private static void ResetVehicleEdgeCellCache(ref List<IntVec3> ___mapEdgeCells)
        {
            if (MP.IsInMultiplayer)
                ___mapEdgeCells = null;
        }

        private static bool RedirectVehicleCaravanSendToSession(ref bool __result)
        {
            if (!MP.IsInMultiplayer || !MP.InInterface || MP.IsExecutingSyncCommand)
                return true;

            var session = GetCaravanFormingSession(CaravanFormation.formation?.Map);
            if (session == null)
                return true;

            caravanFormingTrySendMethod.Invoke(session, null);
            // The synchronized session removes itself only when VF's formation succeeds. Keep the
            // proxy open until then so a validation failure neither closes the UI nor clears seats.
            __result = false;
            return false;
        }

        private static bool FormVehicleCaravanFromSessionDummy(Dialog_FormCaravan __instance, ref bool __result)
        {
            if (!MP.IsInMultiplayer || !MP.IsExecutingSyncCommand)
                return true;

            var vehicle = __instance.transferables?
                .FirstOrDefault(transferable => transferable.CountToTransfer > 0 && transferable.AnyThing is VehiclePawn)?
                .AnyThing as VehiclePawn;
            if (vehicle?.Map == null)
                return true;

            var previousFormation = CaravanFormation.formation;
            try
            {
                CaravanFormation.formation = new FormationInfo(__instance, vehicle.Map);
                // The outer VF method normally prepares these lists before reaching this final
                // function. We intentionally bypass the outer UI warnings during synchronized
                // execution, so perform the same VF-owned recache explicitly on the session dummy.
                CaravanFormation.formation.RecacheTransferables();
                __result = (bool)vehicleTryFormAndSendMethod.Invoke(null, null);
                return false;
            }
            finally
            {
                CaravanFormation.formation = previousFormation;
            }
        }

        #region Turrets

        private static void SyncSetTarget(VehicleTurret turret, LocalTargetInfo target)
            => turret.SetTarget(target);

        private static IEnumerable<CodeInstruction> ReplaceSetTargetCall(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            var target = AccessTools.DeclaredMethod(typeof(VehicleTurret), nameof(VehicleTurret.SetTarget));
            var replacement = AccessTools.DeclaredMethod(typeof(VehicleFramework), nameof(SyncSetTarget));

            var replacedCount = 0;

            foreach (var ci in instr)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodInfo method && method == target)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    replacedCount++;
                }

                yield return ci;
            }

            const int expected = 1;
            if (replacedCount != expected)
            {
                var name = (baseMethod.DeclaringType?.Namespace).NullOrEmpty() ? baseMethod.Name : $"{baseMethod.DeclaringType!.Name}:{baseMethod.Name}";
                Log.Warning($"Patched incorrect number of SetTarget calls (patched {replacedCount}, expected {expected}) for method {name}");
            }
        }

        // Normally, a turret would start warmup once it starts pointing at the target.
        // Starting the local targeter skips Vehicle Framework's target reset. Reset and realign
        // before the synchronized target is applied so the turret does not begin warmup early.
        private static void ResetTurretTarget(VehicleTurret turret)
        {
            if (turret == null)
                return;

            // Only needed for the first shot. All the other ones would be fine without it.
            turret.SetTarget(LocalTargetInfo.Invalid);
            turret.AlignToAngleRestricted(0f);
        }

        #endregion

        #region Flying Vehicles

        private static void SetLaunchArrivalVehicle(object target, object[] args)
        {
            if (target is CompVehicleLauncher launcher && args.Length > 1)
                SetArrivalActionVehicle(args[1] as IArrivalAction, launcher.Vehicle);
        }

        private static void SetOrderArrivalVehicle(object target, object[] args)
        {
            if (target is AerialVehicleInFlight aerialVehicle && args.Length > 1)
                SetArrivalActionVehicle(args[1] as IArrivalAction, aerialVehicle.vehicle);
        }

        private static void SetCaravanLaunchArrivalVehicle(object target, object[] args)
        {
            if (target is VehicleCaravan caravan && args.Length > 1)
                SetArrivalActionVehicle(args[1] as IArrivalAction, caravan.LeadVehicle);
        }

        private static void SetArrivalActionVehicle(IArrivalAction arrivalAction, VehiclePawn vehicle)
        {
            if (arrivalAction is VehicleArrivalAction vehicleAction)
                vehicleAction.vehicle = vehicle;
        }

        private static void CleanupDestroyedCaravanAfterLaunch(object target, object[] _)
        {
            if (MP.IsExecutingSyncCommandIssuedBySelf && target is VehicleCaravan { Destroyed: true } caravan)
                Find.WorldSelector.Deselect(caravan);
        }

        private static void PostSkyfallerLeavingExposeData(VehicleSkyfaller_Leaving __instance)
        {
            // VF doesn't save arrivalAction in ExposeData. In MP, the game may save
            // between skyfaller creation and LeaveMap, losing the arrival action.
            Scribe_Deep.Look(ref __instance.arrivalAction, "arrivalAction");
        }

        private static void PreLaunchSetArrivalVehicle(CompVehicleLauncher __instance, IArrivalAction arrivalAction)
        {
            if (MP.IsInMultiplayer)
                SetArrivalActionVehicle(arrivalAction, __instance.Vehicle);
        }

        private static void PreOrderFlySetArrivalVehicle(AerialVehicleInFlight __instance, IArrivalAction arrivalAction)
        {
            if (MP.IsInMultiplayer)
                SetArrivalActionVehicle(arrivalAction, __instance.vehicle);
        }

        private static void SyncGlobalTargetData(SyncWorker sync,
            ref SmashTools.Targeting.TargetData<GlobalTargetInfo> targetData)
        {
            if (sync.isWriting)
            {
                sync.Write(targetData.targets);
                return;
            }

            var targets = sync.Read<List<GlobalTargetInfo>>();
            targetData = new SmashTools.Targeting.TargetData<GlobalTargetInfo>();
            if (targets != null)
                targetData.targets.AddRange(targets);
        }

        private static void PreMoveForwardFixArrivalVehicle(AerialVehicleInFlight __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            // Ensure the arrival action's vehicle reference is set before ConsumeNode
            // calls Arrived. The vehicle Scribe_Reference may fail to resolve after
            // MP save/load because the vehicle is inside the aerial vehicle's container.
            if (__instance.flightPath?.ArrivalAction is VehicleArrivalAction action
                && action.vehicle == null
                && __instance.vehicle != null)
            {
                action.vehicle = __instance.vehicle;
            }
        }

        private static void PreResumePathPostLoad(AerialVehicleInFlight __instance, ref (float transition, Vector3 position)? __state)
        {
            if (MP.IsInMultiplayer)
                __state = (__instance.transition, __instance.position);
        }

        private static void PostResumePathPostLoad(AerialVehicleInFlight __instance, (float transition, Vector3 position)? __state)
        {
            if (__state.HasValue)
            {
                __instance.transition = __state.Value.transition;
                __instance.position = __state.Value.position;
            }
        }

        #endregion

        #region SyncWorkers

        private static void SyncFuelGizmo(SyncWorker sync, ref Gizmo_RefuelableFuelTravel gizmo)
        {
            if (sync.isWriting)
                sync.Write(gizmo.refuelable);
            else
                gizmo = new Gizmo_RefuelableFuelTravel(sync.Read<CompFueledTravel>(), false);
        }

        private static void SyncVehicleComponent(SyncWorker sync, ref VehicleComponent comp)
        {
            if (sync.isWriting)
            {
                sync.Write(comp?.props?.key);
                sync.Write(comp?.vehicle);
            }
            else
            {
                var key = sync.Read<string>();
                var vehicle = sync.Read<VehiclePawn>();
                comp = key == null ? null : vehicle?.statHandler.GetComponent(key);
            }
        }

        private static void SyncVehicleTurret(SyncWorker sync, ref VehicleTurret turret)
        {
            if (sync.isWriting)
            {
                sync.Write(turret?.key);
                sync.Write(turret?.vehicle);
            }
            else
            {
                var key = sync.Read<string>();
                var vehicle = sync.Read<VehiclePawn>();
                turret = key == null ? null : vehicle?.CompVehicleTurrets?.GetTurret(key);
            }
        }

        private static void SyncAutoLoadConfig(SyncWorker sync, ref AutoLoadConfig config)
        {
            if (sync.isWriting)
                sync.Write(config.turret);
            else
                config = sync.Read<VehicleTurret>()?.loadConfig;
        }

        private static void SyncVehicleIgnitionController(SyncWorker sync, ref VehicleIgnitionController controller)
        {
            if (sync.isWriting)
            {
                sync.Write(controller.vehicle);
            }
            else
            {
                var vehiclePawn = sync.Read<VehiclePawn>();
                controller = vehiclePawn?.ignition;
            }
        }

        private static void SyncVehicleRoleHandler(SyncWorker sync, ref VehicleRoleHandler handler)
        {
            if (sync.isWriting)
            {
                sync.Write(handler?.role?.key);
                sync.Write(handler?.vehicle);
            }
            else
            {
                var roleKey = sync.Read<string>();
                var vehicle = sync.Read<VehiclePawn>();
                handler = roleKey == null ? null : vehicle?.GetHandler(roleKey);
            }
        }

        private static void SyncCommandTurret(SyncWorker sync, ref Command_Turret command)
        {
            SyncVehicleTurret(sync, ref command.turret);
            if (!sync.isWriting)
                command.vehicle = command.turret?.vehicle;
        }

        // Needed by the load cargo dialog/session sync field
        private static void SyncVehicleSettings(SyncWorker sync, ref VehiclesModSettings settings)
        {
            if (!sync.isWriting)
                settings = VehicleMod.settings;
        }

        // Needed for flying vehicle syncing
        private static void SyncLaunchProtocol(SyncWorker sync, ref LaunchProtocol launchProtocol)
        {
            if (sync.isWriting)
                sync.Write(launchProtocol?.vehicle?.CompVehicleLauncher);
            else
                launchProtocol = sync.Read<CompVehicleLauncher>()?.launchProtocol;
        }

        private static void SyncFlightNode(SyncWorker sync, ref FlightNode node)
        {
            SyncType type = typeof(FlightNode);
            type.expose = true;

            if (sync.isWriting)
                sync.Write(node, type);
            else
                node = sync.Read<FlightNode>(type);
        }

        [MpCompatSyncWorker(typeof(Designator_AreaRoadExpand), shouldConstruct = true)]
        private static void SyncAreaRoadDesignator(SyncWorker sync, ref Designator_AreaRoadExpand designator)
        {
            // We need to sync the road type (prioritize/avoid) to properly sync the designator.
            // Sync the local player's road type, as the normal one may become overwritten by
            // this specific sync worker delegate.
            if (sync.isWriting)
                sync.Write(localRoadType);
            else
                Designator_AreaRoad.roadType = sync.Read<Designator_AreaRoad.RoadType>();
        }

        #endregion

        #region Sessions

        #region Load cargo session

        #region Session class

        [MpCompatRequireMod("SmashPhil.VehicleFramework")]
        private class LoadVehicleCargoSession : ExposableSession, ISessionWithTransferables, ISessionWithCreationRestrictions
        {
            public static LoadVehicleCargoSession drawingSession;
            public static bool allowedToRecacheTransferables = false;

            public override Map Map => vehicle.Map;

            private VehiclePawn vehicle;
            public List<TransferableOneWay> transferables = [];

            public bool uiDirty;
            public bool widgetDirty;

            [UsedImplicitly]
            public LoadVehicleCargoSession(Map map) : base(map)
            {
            }

            public LoadVehicleCargoSession(Map map, VehiclePawn vehicle) : base(map)
            {
                this.vehicle = vehicle;

                AddItems();
            }

            public void AddItems()
            {
                var dialog = new Dialog_LoadCargo(vehicle);
                try
                {
                    allowedToRecacheTransferables = true;
                    uiDirty = true;
                    widgetDirty = true;
                    dialog.CalculateAndRecacheTransferables();
                    transferables = dialog.transferables;
                }
                finally
                {
                    allowedToRecacheTransferables = false;
                }
            }

            public override void ExposeData()
            {
                base.ExposeData();

                Scribe_References.Look(ref vehicle, "vehicle");
                Scribe_Collections.Look(ref transferables, "transferables", LookMode.Deep);
            }

            public override bool IsCurrentlyPausing(Map map) => map == Map;

            private void OpenWindow(bool sound = true)
            {
                var dialog = PrepareDummyDialog();
                if (!sound)
                    dialog.soundAppear = null;

                Find.WindowStack.Add(dialog);
                uiDirty = true;
                widgetDirty = true;
            }

            private Dialog_LoadCargo PrepareDummyDialog()
            {
                return new Dialog_LoadCargo(vehicle)
                {
                    transferables = transferables,
                };
            }

            public void Accept()
            {
                vehicle.cargoToLoad = transferables.Where(t => t.CountToTransfer > 0).ToList();
                vehicle.Map.GetCachedMapComponent<VehicleReservationManager>().RegisterLister(vehicle, "LoadVehicle");
                Remove();
            }

            public void Reset()
            {
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                transferables.ForEach(t => t.CountToTransfer = 0);
                uiDirty = true;
            }

            public void PackInstantly()
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();

                foreach (var transferable in transferables)
                {
                    var things = transferable.things;
                    var count = transferable.CountToTransfer;

                    TransferableUtility.Transfer(things, count, (t, _) => vehicle.AddOrTransfer(t));
                }

                Remove();
            }

            public void SetToSendEverything()
            {
                PrepareDummyDialog().SetToSendEverything();
                uiDirty = true;
            }

            public void Remove()
            {
                MP.GetLocalSessionManager(Map).RemoveSession(this);
            }

            public static bool TryOpenLoadVehicleCargoDialog(VehiclePawn vehicle)
            {
                if (vehicle?.Map == null)
                    return false;

                var session = MP.GetLocalSessionManager(vehicle.Map).GetFirstOfType<LoadVehicleCargoSession>();
                if (session == null)
                    return false;

                session.OpenWindow();
                return true;
            }

            public static void CreateLoadVehicleCargoSession(VehiclePawn vehicle)
            {
                if (vehicle?.Map == null)
                    return;

                var manager = MP.GetLocalSessionManager(vehicle.Map);
                var session = manager.GetFirstOfType<LoadVehicleCargoSession>();
                if (session == null)
                {
                    session = new LoadVehicleCargoSession(vehicle.Map, vehicle);
                    if (!manager.AddSession(session))
                        session = null;
                }

                if (session != null && MP.IsExecutingSyncCommandIssuedBySelf)
                    session.OpenWindow();
            }

            public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
            {
                return new FloatMenuOption("MpVehicleCargoLoadingSession".Translate(), () =>
                {
                    SwitchToMapOrWorld(Map);
                    OpenWindow();
                });
            }

            public Transferable GetTransferableByThingId(int thingId)
                => transferables.Find(tr => tr.things.Any(t => t.thingIDNumber == thingId));

            public void Notify_CountChanged(Transferable tr) => uiDirty = true;

            public bool CanExistWith(Session other) => other is not LoadVehicleCargoSession;
        }

        #endregion

        #region Dialog Patches

        private static void SetCurrentLoadCargoSessionState(LoadVehicleCargoSession session)
        {
            LoadVehicleCargoSession.drawingSession = session;
            MP.SetCurrentSessionWithTransferables(session);
        }

        private static void PreDrawLoadCargo(Dialog_LoadCargo __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            var session = MP.GetLocalSessionManager(__instance.vehicle.Map).GetFirstOfType<LoadVehicleCargoSession>();
            if (session == null)
            {
                __instance.Close();
                return;
            }

            SetCurrentLoadCargoSessionState(session);
            MP.WatchBegin();
            showAllCargoItemsField.Watch(VehicleMod.settings);

            if (session.uiDirty)
            {
                __instance.CountToTransferChanged();
                session.uiDirty = false;
            }

            if (session.widgetDirty)
            {
                __instance.transferables = session.transferables;
                // Initialize UI
                __instance.itemsTransfer = new TransferableOneWayWidget(
                    session.transferables,
                    null,
                    null,
                    null,
                    true,
                    IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload,
                    false,
                    () => __instance.MassCapacity - __instance.MassUsage);

                session.widgetDirty = false;
            }
        }

        private static void FinalizeDrawLoadCargo()
        {
            if (LoadVehicleCargoSession.drawingSession != null)
            {
                MP.WatchEnd();
                SetCurrentLoadCargoSessionState(null);
            }
        }

        private static void PostShowAllCargoItemsChanged(object instances, object value)
        {
            // If the setting to see all was selected, it resets the dialog transferables.
            // We need to make sure it's done to all dialogs, as it's a global setting.
            foreach (var map in Find.Maps)
                MP.GetLocalSessionManager(map).GetFirstOfType<LoadVehicleCargoSession>()?.AddItems();
        }

        private static void PreLoadCargoButtonText(string label, ref bool __state)
        {
            if (LoadVehicleCargoSession.drawingSession != null && label == "CancelButton".Translate())
            {
                GUI.color = new Color(1f, 0.3f, 0.35f);
                __state = true;
            }
        }

        private static void PostLoadCargoButtonText(bool __state)
        {
            if (__state)
                GUI.color = Color.white;
        }

        private static void PostLoadCargoButtonTextWorker(string label, ref Widgets.DraggableResult __result)
        {
            var session = LoadVehicleCargoSession.drawingSession;
            if (session == null || !__result.AnyPressed())
                return;

            if (label == "AcceptButton".Translate())
                session.Accept();
            else if (label == "ResetButton".Translate())
                session.Reset();
            else if (label == "CancelButton".Translate())
                session.Remove();
            else if (label == "Dev: Pack Instantly")
                session.PackInstantly();
            else
                return;

            __result = Widgets.DraggableResult.Idle;
        }

        private static bool PreLoadCargoSetToSendEverything()
        {
            if (LoadVehicleCargoSession.drawingSession == null)
                return true;

            LoadVehicleCargoSession.drawingSession.SetToSendEverything();
            return false;
        }

        private static bool PreLoadCargoCalculateAndRecache()
            => !MP.IsInMultiplayer || LoadVehicleCargoSession.allowedToRecacheTransferables;

        #endregion

        #region Gizmo patches

        private static bool PreLoadCargoDialog(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var vehicle = vehiclePawnInnerClassParentField(__instance);
            if (!LoadVehicleCargoSession.TryOpenLoadVehicleCargoDialog(vehicle))
                LoadVehicleCargoSession.CreateLoadVehicleCargoSession(vehicle);

            return false;
        }

        #endregion

        #endregion

        #region Flying vehicle landing session

        #region Session class

        [MpCompatRequireMod("SmashPhil.VehicleFramework")]
        private class FlyingVehicleTargetedLandingSession : ExposableSession, ISessionWithCreationRestrictions
        {
            private List<VehiclePawn> vehicles = [];
            public override Map Map { get; }
            public override bool IsSessionValid => !vehicles.NullOrEmpty();

            private FlyingVehicleTargetedLandingSession(Map map) : base(map)
                => Map = map;

            public override bool IsCurrentlyPausing(Map map)
                => map == Map;

            public override FloatMenuOption GetBlockingWindowOptions(ColonistBar.Entry entry)
            {
                if (entry.map != Map)
                    return null;

                return new FloatMenuOption("MpVehicleAerialLandingSession".Translate(), () =>
                {
                    if (!IsSessionValid)
                    {
                        Remove();
                    }
                    else
                    {
                        SwitchToMapOrWorld(Map);

                        // If one vehicle, just start targeter for it
                        if (vehicles.Count == 1)
                            StartVehicleLandingTargeter(vehicles[0]);
                        // If multiple vehicles, open list of the ones waiting to land
                        else
                            SetupVehicleListFloatMenu();
                    }
                });
            }

            public override void ExposeData()
            {
                base.ExposeData();

                Scribe_Deep.Look(ref vehicles, "vehicles", this);
            }

            public bool CanExistWith(Session other)
                => other is not FlyingVehicleTargetedLandingSession;

            public void VehicleArrival(VehiclePawn vehicle, LocalTargetInfo target, Rot4 rot)
                => VehicleArrivalById(vehicle.thingIDNumber, target, rot);

            // The vehicle is not spawned, and it doesn't have a holder.
            // And even if we make this session class a IThingHolder, MP
            // doesn't include session classes as valid implementations.
            public void VehicleArrivalById(int vehicleId, LocalTargetInfo target, Rot4 rot)
            {
                var vehicle = vehicles.Find(v => v.thingIDNumber == vehicleId);
                if (vehicle == null)
                    return;

                if (vehicle.Spawned)
                {
                    vehicles.Remove(vehicle);
                    return;
                }

                var vehicleSkyfaller = (VehicleSkyfaller_Arriving)ThingMaker.MakeThing(vehicle.CompVehicleLauncher.Props.skyfallerIncoming);
                vehicleSkyfaller.vehicle = vehicle;
                GenSpawn.Spawn(vehicleSkyfaller, target.Cell, Map, rot);

                vehicles.Remove(vehicle);
                if (LandingTargeter.Instance.vehicle == vehicle)
                    LandingTargeter.Instance.StopTargeting();

                if (!IsSessionValid)
                    Remove();
            }

            public void Remove() => MP.GetLocalSessionManager(Map).RemoveSession(this);

            // Should only ever be called during ticking, no need for sync methods here.
            public static void HandleTargetedVehicleArrival(VehiclePawn vehicle, Map map)
            {
                MP.GetLocalSessionManager(map)
                    .GetOrAddSession(new FlyingVehicleTargetedLandingSession(map))
                    .vehicles
                    .AddDistinct(vehicle);
            }

            private void SetupVehicleListFloatMenu()
            {
                var list = new List<FloatMenuOption>();

                foreach (var vehicle in vehicles)
                {
                    string name;
                    if (vehicle.Nameable && vehicle.Name != null)
                        name = $"{vehicle.VehicleDef.LabelCap} - {vehicle.Name}";
                    else
                        name = vehicle.VehicleDef.LabelCap;

                    list.Add(new FloatMenuOption(name, () => StartVehicleLandingTargeter(vehicle)));
                }

                Find.WindowStack.Add(new FloatMenu(list, "MpVehiclesWaitingToLand"));
            }

            private void StartVehicleLandingTargeter(VehiclePawn vehicle)
            {
                var allowRotating = false;
                if (vehicle.VehicleDef.rotatable)
                    allowRotating = vehicle.CompVehicleLauncher.launchProtocol.LandingProperties?.forcedRotation == null;

                LandingTargeter.Instance.BeginTargeting(
                    vehicle,
                    Map,
                    (target, rot) => VehicleArrival(vehicle, target, rot),
                    allowRotating: allowRotating);
            }
        }

        #endregion

        #region Map patches

        private static void PreventMapRemovalForLandingSessions(ref bool __result, Map ___map)
        {
            // The MP landing session replaces Vehicle Framework's local targeter, so keep the
            // destination map alive while that session is waiting for a landing cell.
            if (MP.IsInMultiplayer && !__result)
                __result = MP.GetLocalSessionManager(___map).GetFirstOfType<FlyingVehicleTargetedLandingSession>() != null;
        }

        private static bool PreTargetedDropVehicleArrival(VehiclePawn vehicle, Map map)
        {
            if (!MP.IsInMultiplayer)
                return true;

            // Prevent the targeter from being started in MP, as we'll instead handle
            // this ourselves with the session we've made.
            FlyingVehicleTargetedLandingSession.HandleTargetedVehicleArrival(vehicle, map);
            return false;
        }

        #endregion

        #endregion

        #region Shared

        private static void InsertSwitchToMap(Window __instance, Rect __0)
        {
            if (!MP.IsInMultiplayer)
                return;

            using (new TextBlock(GameFont.Tiny))
            {
                // TODO: Switch to the MP translation once it's included in the mod
                var switchToMapText = "MpCompatSwitchToMap".Translate();
                var width = switchToMapText.GetWidthCached() + 25;

                if (Widgets.ButtonText(new Rect(__0.xMax - width, 5, width, 24), switchToMapText))
                    __instance.Close();
            }
        }

        #endregion

        #endregion

        #region Determinism

        [MpCompatPostfix(typeof(VehiclePawn), nameof(VehiclePawn.Tick))]
        private static void PostVehicleTick(VehiclePawn __instance)
        {
            if (!MP.IsInMultiplayer)
                return;

            if (!__instance.Spawned)
                return;

            var turretsComp = __instance.CompVehicleTurrets;
            if (turretsComp == null)
                return;

            // This would normally be done during ticking or drawing for each turret inside
            // TurretRotation getter. However, calling it during drawing will cause issues,
            // and the turrets don't always tick, so we need to ensure this is updated when
            // the turret is not ticking, and it's done in a deterministic manner.
            foreach (var turret in turretsComp.turrets)
            {
                if (turret.IsTargetable || turret.attachedTo != null)
                {
                    turret.UpdateRotationLock();
                    turret.TurretRotation = Mathf.Repeat(turret.TurretRotation, 360f);
                }
            }
        }

        [MpCompatPrefix(typeof(TurretTargeter), nameof(TurretTargeter.Turret), methodType: MethodType.Getter)]
        private static bool PreTurretTargeterCurrentTurretGetter()
        {
            return !MP.IsInMultiplayer || MP.InInterface;
        }

        [MpCompatPrefix(typeof(VehicleTweener), nameof(VehicleTweener.TweenedPos), methodType: MethodType.Getter)]
        private static bool PreTweenedPosGetter(VehicleTweener __instance, ref Vector3 __result)
        {
            // Out of MP or in interface, return the tweened pos
            if (!MP.IsInMultiplayer || MP.InInterface)
                return true;

            // Give the root position during ticking, same as MP does with pawns.
            __result = __instance.TweenedPosRoot();
            return false;
        }

        // VehiclePawn.DrawAt uses `in Vector3` which MpCompatPrefix can't match by type array.
        // Patched manually in LatePatch instead.
        private static void PreRenderPawnInternal(VehiclePawn __instance, ref (Rot4 rotation, float angle)? __state)
        {
            if (MP.InInterface)
                __state = (__instance.Rotation, __instance.angle);
        }

        private static void PostRenderPawnInternal(VehiclePawn __instance, ref (Rot4 rotation, float angle)? __state)
        {
            if (__state is {} state)
                (__instance.Rotation, __instance.angle) = (state.rotation, state.angle);
        }

        #endregion

        #region Designator

        [MpCompatPrefix(typeof(Designator_AreaRoad), nameof(Designator_AreaRoad.ProcessInput), 1)]
        private static void StoreNewLocalRoadType(Designator_AreaRoad.RoadType ___roadType)
            => localRoadType = ___roadType;

        [MpCompatPrefix(typeof(Designator_AreaRoad), nameof(Designator_AreaRoad.DesignateSingleCell))]
        [MpCompatPrefix(typeof(Designator_AreaRoad), nameof(Designator_AreaRoad.CanDesignateCell))]
        private static void RestoreLocalRoadType()
        {
            // If in MP and not executing synced commands, restore the current player's road type.
            // It may become overwritten by a different value when syncing.
            if (MP.IsInMultiplayer && !MP.IsExecutingSyncCommand)
                Designator_AreaRoad.roadType = localRoadType;
        }

        #endregion

        #region Upgrade Fixes

        private static bool ShouldExecuteWhenFinished()
        {
            // Preserve Vehicle Framework's background-thread behavior.
            if (!UnityData.IsInMainThread)
                return false;
            if (!MP.IsInMultiplayer)
                return true;

            // During MP loading, defer overlay initialization until play data is ready.
            return PatchingUtilities.AllowedToRunLongEvents;
        }

        [MpCompatTranspiler(typeof(UpgradeNode), nameof(UpgradeNode.AddOverlays))]
        private static IEnumerable<CodeInstruction> FixHostOverlayInit(IEnumerable<CodeInstruction> instr, MethodBase baseMethod)
        {
            // The host reaches this main-thread path while MP is still loading play data. Treat it
            // like the background path so Vehicle Framework defers overlay initialization safely.

            var target = AccessTools.DeclaredPropertyGetter(typeof(UnityData), nameof(UnityData.IsInMainThread));
            var replacement = MpMethodUtil.MethodOf(ShouldExecuteWhenFinished);

            return instr.ReplaceMethod(target, replacement, baseMethod, expectedReplacements: 1);
        }

        #endregion
    }
}
