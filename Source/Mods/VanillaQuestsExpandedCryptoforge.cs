using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Quests Expanded - Cryptoforge by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaQuestsExpanded-Cryptoforge"/>
    [MpCompatFor("vanillaquestsexpanded.cryptoforge")]
    public class VanillaQuestsExpandedCryptoforge
    {
        private static AccessTools.FieldRef<object, FloatMenuContext> extinguishContextField;
        private static object extinguishProvider;
        private static FastInvokeHandler pawnCanExtinguish;
        private static FastInvokeHandler getCryptofreezeNearCell;
        private static JobDef extinguishJobDef;

        public VanillaQuestsExpandedCryptoforge(ModContentPack mod)
        {
            // Register the generator restart and scanner selection gizmos.
            MpCompat.RegisterLambdaMethod("VanillaQuestsExpandedCryptoforge.Restartable", "GetGizmos", 0);
            MpCompat.RegisterLambdaMethod("VanillaQuestsExpandedCryptoforge.Scannable", "GetGizmos", 0);

            // Register their pawn-order float menu callbacks.
            MpCompat.RegisterLambdaDelegate("VanillaQuestsExpandedCryptoforge.Restartable", "GetFloatMenuOptions", 0);
            MpCompat.RegisterLambdaDelegate("VanillaQuestsExpandedCryptoforge.Scannable", "GetFloatMenuOptions", 0);

            PatchExtinguishCryptofreeze();
        }

        private static void PatchExtinguishCryptofreeze()
        {
            var providerType = AccessTools.TypeByName(
                "VanillaQuestsExpandedCryptoforge.FloatMenuOptionProvider_ExtinguishCryptofreeze");
            var action = MpMethodUtil.GetLambda(providerType, "GetSingleOption", lambdaOrdinal: 0);

            extinguishContextField = AccessTools.FieldRefAccess<FloatMenuContext>(action.DeclaringType, "context");
            extinguishProvider = Activator.CreateInstance(providerType);
            pawnCanExtinguish = MethodInvoker.GetHandler(AccessTools.Method(providerType, "PawnCanExtinguish"));
            getCryptofreezeNearCell = MethodInvoker.GetHandler(AccessTools.Method(
                "VanillaQuestsExpandedCryptoforge.CryptofreezeUtility:GetCryptofreezeNearCell"));
            extinguishJobDef = DefDatabase<JobDef>.GetNamed("VGE_ExtinguishCryptofreezeNearby");

            MP.RegisterSyncMethod(typeof(VanillaQuestsExpandedCryptoforge), nameof(SyncedExtinguishCryptofreeze));
            MpCompat.harmony.Patch(action,
                prefix: new HarmonyMethod(typeof(VanillaQuestsExpandedCryptoforge), nameof(PreExtinguishCryptofreeze)));
        }

        private static bool PreExtinguishCryptofreeze(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            var context = extinguishContextField(__instance);
            SyncedExtinguishCryptofreeze(context.ValidSelectedPawns.ToList(), context.ClickedCell);
            return false;
        }

        private static void SyncedExtinguishCryptofreeze(List<Pawn> pawns, IntVec3 cell)
        {
            var map = pawns.FirstOrDefault(pawn => pawn is { Spawned: true })?.Map;
            if (map == null)
                return;

            var targets = ((IEnumerable)getCryptofreezeNearCell(null, cell, map)).Cast<Thing>().ToList();

            foreach (var pawn in pawns)
            {
                var accepted = (AcceptanceReport)pawnCanExtinguish(extinguishProvider, pawn, cell);
                if (!accepted.Accepted)
                    continue;

                var job = JobMaker.MakeJob(extinguishJobDef);
                foreach (var target in targets)
                    job.AddQueuedTarget(TargetIndex.A, target);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
        }
    }
}
