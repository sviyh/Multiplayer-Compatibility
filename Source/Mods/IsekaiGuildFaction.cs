using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld.Planet;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>GUILD FACTION ADD-ON by Poupun</summary>
    /// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=3718568958"/>
    [MpCompatFor("Poupun.GuildFactionAddon")]
    public class IsekaiGuildFactionCompat
    {
        private static Type dialogGuildQuestBoardType;
        private static Type guildQuestBoardWorldCompType;
        private static PropertyInfo entriesProperty;
        private static FieldInfo entriesField;
        private static MethodInfo acceptEntryMethod;
        private static MethodInfo tryRefreshMethod;

        public IsekaiGuildFactionCompat(ModContentPack mod)
        {
            dialogGuildQuestBoardType = AccessTools.TypeByName("GuildFactionAddon.Dialog_GuildQuestBoard");
            guildQuestBoardWorldCompType = AccessTools.TypeByName("GuildFactionAddon.GuildQuestBoardWorldComponent");

            if (dialogGuildQuestBoardType == null || guildQuestBoardWorldCompType == null)
            {
                Log.Warning("[IsekaiMP] GuildFactionAddon types could not be resolved — patches skipped.");
                return;
            }

            entriesProperty = AccessTools.Property(guildQuestBoardWorldCompType, "Entries");
            entriesField = AccessTools.Field(guildQuestBoardWorldCompType, "entries");
            acceptEntryMethod = AccessTools.DeclaredMethod(dialogGuildQuestBoardType, "AcceptEntry");
            tryRefreshMethod = AccessTools.DeclaredMethod(guildQuestBoardWorldCompType, "TryRefresh");

            if (acceptEntryMethod != null)
            {
                MpCompat.harmony.Patch(acceptEntryMethod,
                    prefix: new HarmonyMethod(typeof(IsekaiGuildFactionCompat), nameof(AcceptEntryPrefix)));
                MP.RegisterSyncMethod(typeof(IsekaiGuildFactionCompat), nameof(SyncedAcceptQuestEntry));
            }

            // Ensure daily quest refresh triggers deterministically in lockstep during game ticks
            var worldTickMethod = AccessTools.DeclaredMethod(typeof(World), nameof(World.WorldTick));
            if (worldTickMethod != null && tryRefreshMethod != null)
            {
                MpCompat.harmony.Patch(worldTickMethod,
                    postfix: new HarmonyMethod(typeof(IsekaiGuildFactionCompat), nameof(WorldTickPostfix)));
            }
        }

        private static void WorldTickPostfix()
        {
            if (Find.TickManager != null && Find.TickManager.TicksGame % 250 == 0)
            {
                var comp = Find.World?.GetComponent(guildQuestBoardWorldCompType);
                if (comp != null && tryRefreshMethod != null)
                {
                    tryRefreshMethod.Invoke(comp, [false]);
                }
            }
        }

        private static bool _suppressAcceptEntry = false;

        private static bool AcceptEntryPrefix(object entry)
        {
            if (!MP.IsInMultiplayer || _suppressAcceptEntry || entry == null) return true;

            var boardComp = Find.World?.GetComponent(guildQuestBoardWorldCompType);
            if (boardComp == null) return true;

            var entries = entriesProperty != null
                ? (IList)entriesProperty.GetValue(boardComp)
                : (IList)entriesField?.GetValue(boardComp);
            if (entries == null) return true;

            int index = entries.IndexOf(entry);
            if (index >= 0)
            {
                SyncedAcceptQuestEntry(index);
                return false;
            }

            return true;
        }

        private static void SyncedAcceptQuestEntry(int index)
        {
            var boardComp = Find.World?.GetComponent(guildQuestBoardWorldCompType);
            if (boardComp == null) return;

            var entries = entriesProperty != null
                ? (IList)entriesProperty.GetValue(boardComp)
                : (IList)entriesField?.GetValue(boardComp);
            if (entries == null || index < 0 || index >= entries.Count) return;

            object entry = entries[index];
            _suppressAcceptEntry = true;
            try
            {
                acceptEntryMethod?.Invoke(null, [entry]);
            }
            finally
            {
                _suppressAcceptEntry = false;
            }
        }
    }
}
