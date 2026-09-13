using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Multiplayer.Compat;

/// <summary>Research Reinvented by PeteTimesSix</summary>
/// <see href="https://github.com/PeteTimesSix/ResearchReinvented"/>
/// <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2868392160"/>
[MpCompatFor("PeteTimesSix.ResearchReinvented")]
public class ResearchReinvented
{
	public ResearchReinvented(ModContentPack mod)
    {
        var method = AccessTools.PropertyGetter("PeteTimesSix.ResearchReinvented.Managers.ResearchOpportunityManager:CurrentProjectOpportunities");

        MpCompat.harmony.Patch(method, transpiler: new HarmonyMethod(AddSorting));
    }

    static IEnumerable<CodeInstruction> AddSorting(IEnumerable<CodeInstruction> insts)
    {
        var target = AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToList));
        var replace = AccessTools.Method(typeof(ResearchReinvented), nameof(SortByUniqueLoadID));

        foreach(var inst in insts)
        {
            if (inst.operand is MethodInfo method && method == target)
            {
                inst.operand = replace;
            }
            yield return inst;
        }
    }

    private static List<ILoadReferenceable> SortByUniqueLoadID(IEnumerable<ILoadReferenceable> list) => list.OrderBy(a => a.GetUniqueLoadID()).ToList();
}
