using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>ISEKAI CREATURES ADD-ON by Poupun</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3757560461"/>
    [MpCompatFor("Poupun.IsekaiCreaturesAddon")]
    public class IsekaiCreaturesCompat
    {
        private static Type petGizmosPatchType;
        private static Type sealProviderType;
        private static Type tameProviderType;

        public IsekaiCreaturesCompat(ModContentPack mod)
        {
            petGizmosPatchType = AccessTools.TypeByName("IsekaiCreatures.Taming.Pawn_GetGizmos_Pet_Patch");
            sealProviderType = AccessTools.TypeByName("IsekaiCreatures.Sealing.FloatMenuOptionProvider_SealCreature");
            tameProviderType = AccessTools.TypeByName("IsekaiCreatures.Taming.FloatMenuOptionProvider_TameBeast");

            if (petGizmosPatchType != null)
            {
                var withDraftToggleMethod = AccessTools.DeclaredMethod(petGizmosPatchType, "WithDraftToggle");
                if (withDraftToggleMethod != null)
                {
                    MpCompat.harmony.Patch(withDraftToggleMethod,
                        postfix: new HarmonyMethod(typeof(IsekaiCreaturesCompat), nameof(WithDraftTogglePostfix)));
                    MP.RegisterSyncMethod(typeof(IsekaiCreaturesCompat), nameof(SyncedSetPetDrafted));
                }
            }

            if (sealProviderType != null)
            {
                var sealMethod = AccessTools.DeclaredMethod(sealProviderType, "GetOptionsFor");
                if (sealMethod != null)
                {
                    MpCompat.harmony.Patch(sealMethod,
                        postfix: new HarmonyMethod(typeof(IsekaiCreaturesCompat), nameof(SealOptionsPostfix)));
                    MP.RegisterSyncMethod(typeof(IsekaiCreaturesCompat), nameof(SyncedToggleSealDesignation));
                }
            }

            if (tameProviderType != null)
            {
                var tameMethod = AccessTools.DeclaredMethod(tameProviderType, "GetOptionsFor");
                if (tameMethod != null)
                {
                    MpCompat.harmony.Patch(tameMethod,
                        postfix: new HarmonyMethod(typeof(IsekaiCreaturesCompat), nameof(TameOptionsPostfix)));
                    MP.RegisterSyncMethod(typeof(IsekaiCreaturesCompat), nameof(SyncedToggleTameDesignation));
                }
            }
        }

        #region Pet Drafting

        private static void WithDraftTogglePostfix(ref IEnumerable<Gizmo> __result, Pawn pawn)
        {
            if (!MP.IsInMultiplayer || pawn == null || __result == null) return;
            __result = WrapPetGizmos(__result, pawn);
        }

        private static IEnumerable<Gizmo> WrapPetGizmos(IEnumerable<Gizmo> gizmos, Pawn pawn)
        {
            foreach (var g in gizmos)
            {
                if (g is Command_Toggle toggle && toggle.hotKey == KeyBindingDefOf.Command_ColonistDraft)
                {
                    toggle.toggleAction = () => SyncedSetPetDrafted(pawn, !pawn.Drafted);
                }
                yield return g;
            }
        }

        private static void SyncedSetPetDrafted(Pawn pawn, bool drafted)
        {
            if (pawn?.drafter != null)
                pawn.drafter.Drafted = drafted;
        }

        #endregion

        #region Sealing

        private static void SealOptionsPostfix(ref IEnumerable<FloatMenuOption> __result, Pawn clickedPawn)
        {
            if (!MP.IsInMultiplayer || clickedPawn == null || __result == null) return;
            __result = WrapSealOptions(__result, clickedPawn);
        }

        private static IEnumerable<FloatMenuOption> WrapSealOptions(IEnumerable<FloatMenuOption> options, Pawn creature)
        {
            foreach (var opt in options)
            {
                if (opt.action != null)
                {
                    opt.action = () => SyncedToggleSealDesignation(creature);
                }
                yield return opt;
            }
        }

        private static void SyncedToggleSealDesignation(Pawn creature)
        {
            if (creature?.Map == null) return;
            var dm = creature.Map.designationManager;
            if (dm == null) return;

            var sealDef = DefDatabase<DesignationDef>.GetNamedSilentFail("IsekaiSealCreature");
            if (sealDef == null) return;

            var d = dm.DesignationOn(creature, sealDef);
            if (d != null)
                dm.RemoveDesignation(d);
            else
                dm.AddDesignation(new Designation(creature, sealDef));
        }

        #endregion

        #region Taming

        private static void TameOptionsPostfix(ref IEnumerable<FloatMenuOption> __result, Thing clickedThing)
        {
            if (!MP.IsInMultiplayer || clickedThing is not Pawn beast || __result == null) return;
            __result = WrapTameOptions(__result, beast);
        }

        private static IEnumerable<FloatMenuOption> WrapTameOptions(IEnumerable<FloatMenuOption> options, Pawn beast)
        {
            foreach (var opt in options)
            {
                if (opt.action != null)
                {
                    opt.action = () => SyncedToggleTameDesignation(beast);
                }
                yield return opt;
            }
        }

        private static void SyncedToggleTameDesignation(Pawn beast)
        {
            if (beast?.Map == null) return;
            var dm = beast.Map.designationManager;
            if (dm == null) return;

            var tameDef = DefDatabase<DesignationDef>.GetNamedSilentFail("IsekaiTameBeast");
            if (tameDef == null) return;

            var d = dm.DesignationOn(beast, tameDef);
            if (d != null)
                dm.RemoveDesignation(d);
            else
                dm.AddDesignation(new Designation(beast, tameDef));
        }

        #endregion
    }
}
