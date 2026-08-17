using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Vanilla Ideology Expanded - Splits and Schisms by Oskar Potocki and Sarg Bjornson</summary>
    /// <see href="https://github.com/Vanilla-Expanded/VanillaIdeologiesExpanded-SplitsandSchisms"/>
    [MpCompatFor("VanillaExpanded.VIESAS")]
    public class VanillaIdeologySplitsAndSchisms
    {
        private static System.Type configureWindowType;
        private static AccessTools.FieldRef<object, Ideo> windowIdeoField;
        private static AccessTools.FieldRef<object, List<Pawn>> windowColonistsField;
        private static MethodInfo trackerInstanceGetter;
        private static FieldInfo trackerOriginIdeoField;
        private static FieldInfo trackerSplitIdeoField;
        private static FieldInfo trackerNextCheckField;
        private static MethodInfo trackerGetNextCheck;

        public VanillaIdeologySplitsAndSchisms(ModContentPack mod)
        {
            configureWindowType = AccessTools.TypeByName("VIESAS.Window_ConfigureIdeo");
            windowIdeoField = AccessTools.FieldRefAccess<Ideo>(configureWindowType, "newIdeo");
            windowColonistsField = AccessTools.FieldRefAccess<List<Pawn>>(configureWindowType, "colonistsToConvert");

            var trackerType = AccessTools.TypeByName("VIESAS.IdeologyTracker");
            trackerInstanceGetter = AccessTools.PropertyGetter(trackerType, "Instance");
            trackerOriginIdeoField = AccessTools.Field(trackerType, "originIdeo");
            trackerSplitIdeoField = AccessTools.Field(trackerType, "splittedIdeo");
            trackerNextCheckField = AccessTools.Field(trackerType, "nextConversionTickCheck");
            trackerGetNextCheck = AccessTools.Method(trackerType, "GetNextConversionTickCheck");

            var accept = MP.RegisterSyncMethod(
                typeof(VanillaIdeologySplitsAndSchisms), nameof(SyncedAcceptIdeology));
            accept.ExposeParameter(0);

            // Replace the window's local Close mutation with one exposed synchronized accept command.
            MpCompat.harmony.Patch(AccessTools.Method(configureWindowType, nameof(Window.Close)),
                prefix: new HarmonyMethod(typeof(VanillaIdeologySplitsAndSchisms), nameof(PreCloseConfigureWindow)));

            PatchConfigurePage("VIESAS.Page_ConfigureIdeo_Colonists");
            PatchConfigurePage("VIESAS.Page_ConfigureFluidIdeo_Colonists");
        }

        private static void PatchConfigurePage(string typeName)
        {
            var type = AccessTools.TypeByName(typeName);
            MpCompat.harmony.Patch(AccessTools.Method(type, "DoNext"),
                prefix: new HarmonyMethod(typeof(VanillaIdeologySplitsAndSchisms), nameof(PreConfigurePageDoNext)));
        }

        private static bool PreCloseConfigureWindow(object __instance)
        {
            if (!MP.IsInMultiplayer)
                return true;

            if (!MP.IsExecutingSyncCommand)
                SyncedAcceptIdeology(windowIdeoField(__instance), windowColonistsField(__instance));
            return false;
        }

        private static bool PreConfigurePageDoNext(Window __instance, Window ___window)
        {
            if (!MP.IsInMultiplayer)
                return true;

            ___window.Close();
            __instance.Close();
            return false;
        }

        private static void SyncedAcceptIdeology(Ideo newIdeo, List<Pawn> colonists)
        {
            if (newIdeo == null || colonists == null)
                return;

            foreach (var colonist in colonists)
            {
                if (colonist?.ideo == null)
                    continue;
                colonist.ideo.SetIdeo(newIdeo);
                colonist.ideo.certaintyInt = Rand.Range(0.75f, 1f);
            }

            var tracker = trackerInstanceGetter.Invoke(null, null);
            trackerOriginIdeoField.SetValue(tracker, Faction.OfPlayer.ideos.PrimaryIdeo);
            trackerSplitIdeoField.SetValue(tracker, newIdeo);
            trackerNextCheckField.SetValue(tracker, trackerGetNextCheck.Invoke(tracker, null));

            if (!Find.IdeoManager.IdeosListForReading.Contains(newIdeo))
                Find.IdeoManager.Add(newIdeo);
            Faction.OfPlayer.ideos.Notify_ColonistChangedIdeo();

            var window = Find.WindowStack.Windows.FirstOrDefault(w => configureWindowType.IsInstanceOfType(w));
            if (window != null)
                Find.WindowStack.TryRemove(window, true);
        }
    }
}
