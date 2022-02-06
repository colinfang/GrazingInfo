#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using RimWorld;
using Verse;
using HarmonyLib;
using UnityEngine;
using System.Reflection;

namespace CF_GrazingInfo
{
    public class Patcher: Mod
    {
        public static Settings Settings = new();

        public Patcher(ModContentPack pack): base(pack)
        {
            Settings = GetSettings<Settings>();
            DoPatching();
        }
        public override string SettingsCategory()
        {
            return "Grazing Info";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listingStandard = new();
            listingStandard.Begin(inRect);
            listingStandard.CheckboxLabeled("Eat Dandelion Only If Mature", ref Settings.EatDandelionOnlyIfMature, "Player tamed animals eat Dandelion only if it is mature");
            listingStandard.End();
            base.DoSettingsWindowContents(inRect);
        }

        public void DoPatching()
        {
            var harmony = new Harmony("com.colinfang.GrazingInfo");
            harmony.PatchAll();
        }
    }

    public class Settings: ModSettings
    {
        public bool EatDandelionOnlyIfMature;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EatDandelionOnlyIfMature, "EatDandelionOnlyIfMature", false);
            base.ExposeData();
        }
    }


    public static class PenFoodCalc
    {
        public class Info
        {
            public float NutritionIngestible;
            public float NutritionGrown;
            public int Count;
        }

        public static Dictionary<ThingDef, Info> Summary = new();
        public static Dictionary<ThingDef, bool> edibleCache = new();
        public static float TotalNutritionIngestible;
        public static float TotalNutritionGrown;
        public static string? CachedTooltip;
        public static string? CachedGrazingEmptyDays;
        public static void Reset()
        {
            Summary.Clear();
            TotalNutritionIngestible = 0;
            TotalNutritionGrown = 0;
            CachedTooltip = null;
            CachedGrazingEmptyDays = null;
        }

        public static void Tally(IntVec3 c, Map map)
        {
            foreach (Thing thing in c.GetThingList(map))
            {
                if (thing is Plant plant)
                {
                    if (!edibleCache.TryGetValue(plant.def, out bool edible))
                    {
                        edible = MapPlantGrowthRateCalculator.IsEdibleByPastureAnimals(plant.def);
                        edibleCache.Add(thing.def, edible);
                    }
                    if (!edible)
                    {
                        continue;
                    }
                    float nutritionAvailable = plant.Growth * plant.GetStatValue(StatDefOf.Nutrition);
                    float nutritionIngestible = plant.IngestibleNow ? nutritionAvailable : 0;

                    TotalNutritionGrown += nutritionAvailable;
                    TotalNutritionIngestible += nutritionIngestible;

                    if (Summary.TryGetValue(plant.def, out var info))
                    {
                        info.NutritionGrown += nutritionAvailable;
                        info.NutritionIngestible += nutritionIngestible;
                        info.Count += 1;
                    }
                    else
                    {
                        Summary.Add(plant.def, new Info { NutritionGrown = nutritionAvailable, NutritionIngestible = nutritionIngestible, Count = 1 });
                    }
                }
            }
        }
        public static string GetGrazingEmptyDays(PenFoodCalculator calc)
        {
            if (CachedGrazingEmptyDays is null)
            {
                float consumptionPerDay = calc.SumNutritionConsumptionPerDay - calc.NutritionPerDayToday;
                if (consumptionPerDay > 0)
                {
                    float d = TotalNutritionIngestible / consumptionPerDay;
                    if (d < 999) {
                        CachedGrazingEmptyDays = d.ToString("F1");
                    } else {
                        // Do not display very big exhaustion days
                        CachedGrazingEmptyDays = "∞";
                    }
                }
                else
                {
                    CachedGrazingEmptyDays = "∞";

                }
            }
            return CachedGrazingEmptyDays;
        }

        public static string ToolTip()
        {
            if (CachedTooltip is null)
            {
                StringBuilder stringBuilder = new();
                stringBuilder
                    .AppendLine("GrazingInfo_TotalDescription".Translate())
                    .AppendLine();

                foreach (var kv in Summary)
                {
                    var info = kv.Value;
                    if (info.NutritionGrown < 0.1)
                    {
                        continue;
                    }

                    stringBuilder
                        .Append("- ")
                        .Append(kv.Key.LabelCap)
                        .Append(" x")
                        .Append(info.Count)
                        .Append(": ")
                        .AppendLine($"{info.NutritionIngestible:F1} ({info.NutritionGrown:F1})");
                }
                CachedTooltip = stringBuilder.ToString();
            }
            return CachedTooltip;
        }
    }


    [HarmonyPatch(typeof(PenFoodCalculator))]
    [HarmonyPatch("ProcessCell")]
    public class PatchPenFoodCalculatorProcessCell
    {
        public static void Postfix(IntVec3 c, Map map)
        {
            PenFoodCalc.Tally(c, map);
        }
    }


    [HarmonyPatch(typeof(PenFoodCalculator))]
    [HarmonyPatch("Reset")]
    public class PatchPenFoodCalculatorReset
    {
        public static void Postfix()
        {
            PenFoodCalc.Reset();
            // Log.Message($"Reset {GenTicks.TicksGame}");
        }
    }


    [HarmonyPatch(typeof(ITab_PenFood))]
    [HarmonyPatch("DrawTopPane")]
    public class PatchITab_PenFoodDrawTopPane
    {
        public static MethodInfo M_DrawStatLine = typeof(ITab_PenFood).GetMethod("DrawStatLine", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void Postfix(ref float curY, float width, ITab_PenFood __instance, PenFoodCalculator calc)
        {
            var arg = new object?[] {
                "GrazingInfo_Total".Translate().ToString(),
                $"{PenFoodCalc.TotalNutritionIngestible:F1} ({PenFoodCalc.TotalNutritionGrown:F1})",
                curY, width,
                () => PenFoodCalc.ToolTip(),
                null
            };
            M_DrawStatLine.Invoke(__instance, arg);
            arg[0] = "GrazingInfo_EmptyDays".Translate().ToString();
            arg[1] = PenFoodCalc.GetGrazingEmptyDays(calc);
            arg[4] = () => "GrazingInfo_EmptyDaysDescription".Translate().ToString();
            M_DrawStatLine.Invoke(__instance, arg);
            curY = (float)arg[2]!;
        }
    }


    [HarmonyPatch(typeof(CompAnimalPenMarker))]
    [HarmonyPatch(nameof(CompAnimalPenMarker.CompInspectStringExtra))]
    public class PatchCompAnimalPenMarkerCompInspectStringExtra
    {
        public static void Postfix(ref string __result, CompAnimalPenMarker __instance)
        {
            PenFoodCalculator calc = __instance.PenFoodCalculator;
            __result = new StringBuilder(__result)
                .AppendLine()
                .Append("GrazingInfo_Total".Translate())
                .Append(": ")
                .AppendLine($"{PenFoodCalc.TotalNutritionIngestible:F1} ({PenFoodCalc.TotalNutritionGrown:F1})")
                .Append("GrazingInfo_EmptyDays".Translate())
                .Append(": ")
                .Append(PenFoodCalc.GetGrazingEmptyDays(calc))
                .ToString();
        }
    }


    [HarmonyPatch(typeof(FoodUtility))]
    [HarmonyPatch(nameof(FoodUtility.WillEat))]
    [HarmonyPatch(new Type[] { typeof(Pawn), typeof(Thing), typeof(Pawn), typeof(bool) })]
    public static class PatchFoodUtilityWillEat
    {
        public static void Postfix(this Pawn p, Thing food, ref bool __result)
        {
            if (__result == false)
            {
                return;
            }

            if (!(p.RaceProps.Animal && (p.Faction?.IsPlayer ?? false)))
            {
                return;
            }

            if (food is not Plant plant)
            {
                return;
            }

            if (plant.def.defName == "Plant_Dandelion" && Patcher.Settings.EatDandelionOnlyIfMature)
            {
                if (plant.LifeStage != PlantLifeStage.Mature)
                {
                    __result = false;
                }
            }
        }
    }


    }

}