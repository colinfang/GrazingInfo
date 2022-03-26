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
    public class Patcher : Mod
    {
        public static Settings Settings = new();

        public Patcher(ModContentPack pack) : base(pack)
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
            listingStandard.CheckboxLabeled("Eat Dandelion Only If Mature", ref Settings.EatDandelionOnlyIfMature, "Player tamed animals would not eat immature Dandelion");
            listingStandard.CheckboxLabeled("Floating Text When Grazing", ref Settings.FloatingTextWhenGrazing, "Show nutrition consumed & food saturation increased as a floating text when animals graze");
            listingStandard.CheckboxLabeled("Animal Productivity Info", ref Settings.AnimalProductivityInfo, "Show more stats on animal info card e.g. best male to female ratio, nutrition efficiency");
            listingStandard.End();
            base.DoSettingsWindowContents(inRect);
        }

        public void DoPatching()
        {
            var harmony = new Harmony("com.colinfang.GrazingInfo");
            harmony.PatchAll();
        }
    }

    public class Settings : ModSettings
    {
        public bool EatDandelionOnlyIfMature = false;
        public bool FloatingTextWhenGrazing = false;
        public bool AnimalProductivityInfo = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref EatDandelionOnlyIfMature, "EatDandelionOnlyIfMature", false);
            Scribe_Values.Look(ref FloatingTextWhenGrazing, "FloatingTextWhenGrazing", false);
            Scribe_Values.Look(ref AnimalProductivityInfo, "AnimalProductivityInfo", true);
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
            foreach (var plant in c.GetThingList(map).OfType<Plant>())
            {
                if (!edibleCache.TryGetValue(plant.def, out bool edible))
                {
                    edible = MapPlantGrowthRateCalculator.IsEdibleByPastureAnimals(plant.def);
                    edibleCache.Add(plant.def, edible);
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
        public static string GetGrazingEmptyDays(PenFoodCalculator calc)
        {
            if (CachedGrazingEmptyDays is null)
            {
                float consumptionPerDay = calc.SumNutritionConsumptionPerDay - calc.NutritionPerDayToday;
                if (consumptionPerDay > 0)
                {
                    float d = TotalNutritionIngestible / consumptionPerDay;
                    if (d < 999)
                    {
                        CachedGrazingEmptyDays = d.ToString("F1");
                    }
                    else
                    {
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
        public static readonly MethodInfo M_DrawStatLine = typeof(ITab_PenFood).GetMethod("DrawStatLine", BindingFlags.NonPublic | BindingFlags.Instance);

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


    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch(nameof(Thing.Ingested))]
    public static class PatchThingIngested
    {
        public static void Postfix(ref float __result, Thing __instance, Pawn ingester)
        {
            if (Patcher.Settings.FloatingTextWhenGrazing && __instance is Plant && __result > 0)
            {
                float foodLevel = ingester.needs.food.CurLevel;
                float maxLevel = ingester.needs.food.MaxLevel;
                float currentLevel = foodLevel + __result;
                // No need to show max level if it is full.
                var maxLeveltext = (maxLevel - currentLevel) < 0.01 ? "" : $" ({maxLevel:F2})";
                MoteMaker.ThrowText(ingester.DrawPos, ingester.Map, $"Graze {__instance.Label} {__result:F2}: {foodLevel:F2} => {currentLevel:F2}{maxLeveltext}", 4);
            }
        }
    }


    [HarmonyPatch(typeof(Pawn))]
    [HarmonyPatch(nameof(Pawn.SpecialDisplayStats))]
    public static class PatchPawnSpecialDisplayStats
    {
        public static double HoursAwakenPerDay = 2.0 / 3;
        public static double NumAdultsPerGestation(double numMates) => 1 + 1 / numMates;
        public static double BaseHuntryPerDay(ThingDef pawnDef) => Need_Food.BaseFoodFallPerTickAssumingCategory(HungerCategory.Fed, pawnDef.race.baseHungerRate) * GenDate.TicksPerDay;
        public static void Postfix(ref IEnumerable<StatDrawEntry> __result, Pawn __instance)
        {
            if (Patcher.Settings.AnimalProductivityInfo && __instance.RaceProps.Animal)
            {
                __result = __result.Concat(Stats(__instance));
            }
        }

        public static IEnumerable<StatDrawEntry> Stats(Pawn pawn)
        {
            {
                var (value, explain) = MateInfo(pawn.def);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Max females per male", value, explain, 10050);
            }
            var ageTracker = pawn.ageTracker;
            if (!ageTracker.Adult)
            {
                var (value, explain) = MeatPerConsumptionOnGrowthNow(pawn);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Growth efficiency now", value, explain, 10040);
            }
            {
                var (value, explain) = MeatPerConsumptionOnGrowth(pawn);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Growth efficiency", value, explain, 10030);
            }
            {
                var (value, explain) = MeatPerConsumption(pawn);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Meat per consumption", value, explain, 10020);
            }
            {
                var (value, explain) = BabyPerConsumption(pawn.def);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Baby meat per consumption", value, explain, 10015);
            }
            if (pawn.def.GetCompProperties<CompProperties_EggLayer>() is { } eggLayer)
            {
                var (value, explain) = EggPerConsumption(pawn.def, eggLayer);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Egg per consumption", value, explain, 10010);
            }

            if ((pawn.def.GetCompProperties<CompProperties_Milkable>() is { } milkable) && milkable.milkDef.ingestible is not null)
            {
                var (value, explain) = MilkPerConsumption(pawn.def, milkable);
                yield return new StatDrawEntry(StatCategoryDefOf.AnimalProductivity, "Milk per consumption", value, explain, 10005);
            }
        }

        public static float GetHungerRate(Pawn pawn, LifeStageDef lifeStageDef)
        {
            return lifeStageDef.hungerRateFactor * pawn.RaceProps.baseHungerRate;
        }

        public static float GetMeat(ThingDef pawnDef, LifeStageDef lifeStageDef)
        {
            float meat = StatDefOf.MeatAmount.Worker.GetBaseValueFor(StatRequest.For(pawnDef, null));
            meat *= lifeStageDef.bodySizeFactor * pawnDef.race.baseBodySize;
            return StatDefOf.MeatAmount.postProcessCurve.Evaluate(meat);
        }

        public static double GetConsumption(Pawn pawn, bool fromNow)
        {
            int startLifeStageIndex = fromNow ? pawn.ageTracker.CurLifeStageIndex : 0;
            long startAgeTick = fromNow ? pawn.ageTracker.AgeBiologicalTicks : 0;

            double nutritionConsumptionTillAdult = 0;
            long currentAgeTick = startAgeTick;
            var lifeStages = pawn.RaceProps.lifeStageAges;

            for (var nextIdx = startLifeStageIndex + 1; nextIdx < lifeStages.Count; nextIdx += 1)
            {
                long nextAgeTick = (long)(lifeStages[nextIdx].minAge * GenDate.TicksPerYear);
                long ticksToNextStage = nextAgeTick - currentAgeTick;
                currentAgeTick = nextAgeTick;
                var currentLifeStage = lifeStages[nextIdx - 1];
                nutritionConsumptionTillAdult += Need_Food.BaseFoodFallPerTickAssumingCategory(HungerCategory.Fed, pawn.RaceProps.baseHungerRate) * currentLifeStage.def.hungerRateFactor * ticksToNextStage;
            }
            return nutritionConsumptionTillAdult;
        }

        public static (string value, string explain) MeatPerConsumptionRaw(Pawn pawn, bool fromNow, double extraNutritionConsumption = 0)
        {
            float currentMeat = fromNow ? GetMeat(pawn.def, pawn.ageTracker.CurLifeStage) : 0;
            {
                // debug
                var meat_original = pawn.GetStatValue(StatDefOf.MeatAmount);
                var meat_mine = GetMeat(pawn.def, pawn.ageTracker.CurLifeStage);
                if (Math.Abs(meat_original - meat_mine) > 0.001)
                {
                    Log.Message($"Meat_original {meat_original}, meat_mine {meat_mine}");
                }

            }
            double nutritionConsumptionTillAdult = GetConsumption(pawn, fromNow);

            double meat = pawn.def.GetStatValueAbstract(StatDefOf.MeatAmount) - currentMeat;
            double nutritionFromMeat = meat * pawn.RaceProps.meatDef.ingestible.CachedNutrition;
            double totalNutritionConsumption = nutritionConsumptionTillAdult + extraNutritionConsumption;
            var value = $"{nutritionFromMeat / totalNutritionConsumption * 100:F1}%";
            StringBuilder sb = new();
            sb
                .AppendLine($"Nutrition efficiency: {nutritionFromMeat:F2} / {totalNutritionConsumption:F2} = {value}")
                .AppendLine()
                .AppendLine($"Meat till adult: {nutritionFromMeat:F2} nutrition")
                .AppendLine($"Consumption till adult: {nutritionConsumptionTillAdult:F2} nutrition");
            return (value, sb.ToString().TrimEndNewlines());
        }

        public static (string value, string explain) MeatPerConsumptionOnGrowthNow(Pawn pawn)
        {
            var (value, explain) = MeatPerConsumptionRaw(pawn, true);
            explain = "The extra nutrition from meat if the animal is raised from the current age till adulthood, compared to the food eaten.\n\n" + explain;
            return (value, explain);
        }
        public static (string value, string explain) MeatPerConsumptionOnGrowth(Pawn pawn)
        {
            var (value, explain) = MeatPerConsumptionRaw(pawn, false);
            explain = "The nutrition from meat if the animal is raised from birth till adulthood, compared to the food eaten.\n\n" + explain;
            return (value, explain);
        }
        public static (string value, string explain) MeatPerConsumption(Pawn pawn)
        {
            double gestationDays = AnimalProductionUtility.GestationDaysLitter(pawn.def);
            var litterSize = AnimalProductionUtility.OffspringRange(pawn.def);
            double nutritionInGestation = BaseHuntryPerDay(pawn.def) * gestationDays / litterSize.Average;
            double numAdults = NumAdultsPerGestation(GetNumMates(pawn.def));
            nutritionInGestation *= numAdults;
            var (value, explain) = MeatPerConsumptionRaw(pawn, false, nutritionInGestation);
            explain = $"The nutrition from meat if the animal is raised from birth till adulthood, compared to the food eaten by it during growth and by {numAdults:F1} parents during gestation.\n\n"
                + explain
                + $"\nConsumption: { nutritionInGestation:F2} nutrition per offspring during gestation ({numAdults:F1} parents)";
            return (value, explain);
        }

        public static (string value, string explain) EggPerConsumption(ThingDef pawnDef, CompProperties_EggLayer eggLayer)
        {
            // Ignore father
            double nutritionInGestation = BaseHuntryPerDay(pawnDef) * eggLayer.eggLayIntervalDays / eggLayer.eggCountRange.Average;
            double nutritionPerEgg = (eggLayer.eggUnfertilizedDef ?? eggLayer.eggFertilizedDef).ingestible.CachedNutrition;

            var value = $"{nutritionPerEgg / nutritionInGestation * 100:F1}%";
            StringBuilder sb = new();
            sb
                .AppendLine("The nutrition from eggs, compared to the food eaten during egg laying intervel.\n")
                .AppendLine($"Nutrition efficiency: {nutritionPerEgg:F2} / {nutritionInGestation:F2} = {value}")
                .AppendLine()
                .AppendLine($"Egg: {nutritionPerEgg:F2} nutrition")
                .AppendLine($"Consumption: {nutritionInGestation:F2} nutrition per egg during gestation");
            return (value, sb.ToString().TrimEndNewlines());
        }

        public static (string value, string explain) MilkPerConsumption(ThingDef pawnDef, CompProperties_Milkable milkable)
        {
            // Ignore father
            double nutritionInMilkInterval = BaseHuntryPerDay(pawnDef) * milkable.milkIntervalDays;
            double nutritionFromMilk = milkable.milkDef.ingestible.CachedNutrition * milkable.milkAmount;

            var value = $"{nutritionFromMilk / nutritionInMilkInterval * 100:F1}%";
            StringBuilder sb = new();
            sb
                .AppendLine("The nutrition from milk, compared to the food eaten during milk interval.\n")
                .AppendLine($"Nutrition efficiency: {nutritionFromMilk:F2} / {nutritionInMilkInterval:F2} = {value}")
                .AppendLine()
                .AppendLine($"Milk: {nutritionFromMilk:F2} nutrition")
                .AppendLine($"Consumption: {nutritionInMilkInterval:F2} nutrition during milk interval");
            return (value, sb.ToString().TrimEndNewlines());
        }

        public static (string value, string explain) BabyPerConsumption(ThingDef pawnDef)
        {
            double gestationDays = AnimalProductionUtility.GestationDaysLitter(pawnDef);
            var litterSize = AnimalProductionUtility.OffspringRange(pawnDef);
            double nutritionInGestation = BaseHuntryPerDay(pawnDef) * gestationDays / litterSize.Average;
            double numAdults = NumAdultsPerGestation(GetNumMates(pawnDef));
            nutritionInGestation *= numAdults;
            double meat = GetMeat(pawnDef, pawnDef.race.lifeStageAges[0].def);
            double nutritionFromMeat = meat * pawnDef.race.meatDef.GetStatValueAbstract(StatDefOf.Nutrition);

            var value = $"{nutritionFromMeat / nutritionInGestation * 100:F1}%";
            StringBuilder sb = new();
            sb
                .AppendLine($"The nutrition from meat if we butcher offsprings straight after birth, compared to the food eaten by {numAdults:F1} parents during gestation.\n")
                .AppendLine($"Nutrition efficiency: {nutritionFromMeat:F2} / {nutritionInGestation:F2} = {value}")
                .AppendLine()
                .AppendLine($"Baby meat: {nutritionFromMeat:F2} nutrition")
                .AppendLine($"Consumption: {nutritionInGestation:F2} nutrition per offspring during gestation ({numAdults:F1} parents)");
            return (value, sb.ToString().TrimEndNewlines());
        }

        public static double GetNumMates(ThingDef pawnDef)
        {
            double gestationDays = AnimalProductionUtility.GestationDaysLitter(pawnDef);
            double chanceToPregnantPerDay = GenDate.HoursPerDay * HoursAwakenPerDay / pawnDef.race.mateMtbHours;
            if (pawnDef.GetCompProperties<CompProperties_EggLayer>() is null)
            {
                chanceToPregnantPerDay *= 0.5f;
            }
            return gestationDays * chanceToPregnantPerDay;
        }

        public static (string value, string explain) MateInfo(ThingDef pawnDef)
        {
            double numMates = GetNumMates(pawnDef);
            var value = $"{numMates:F1}";
            StringBuilder sb = new();
            sb
                .AppendLine($"The max number of females one male can impregnate. In practice this value would be lower.\n")
                .AppendLine($"Each male can satisfy: {value} female");
            return (value, sb.ToString().TrimEndNewlines());
        }
    }
}