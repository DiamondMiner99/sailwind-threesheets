using HarmonyLib;
using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Feeding the model. ShipItemBottle.Drink only ever runs on the machine of the player who actually
    /// drank - the co-op mod syncs the resulting needs delta, not the Drink call - so this is inherently
    /// local and nobody else's round lands on your tab.
    /// </summary>
    [HarmonyPatch(typeof(ShipItemBottle), "Drink")]
    public static class BottleDrinkPatch
    {
        /// <summary>
        /// Read the alcohol content BEFORE the sip. Drink() decrements health and calls EmptyBottle()
        /// on the last one, which zeroes amount - so a postfix reading __instance.amount gets 0 for the
        /// final and most incriminating sip of every bottle.
        /// </summary>
        [HarmonyPrefix]
        public static void Prefix(ShipItemBottle __instance, out float __state)
        {
            __state = 0f;
            if (!Plugin.Enabled.Value || __instance == null) return;
            // Mirrors the vanilla guard: an empty bottle is not a drink.
            if (__instance.health <= 0f) return;
            float alcohol = Liquids.GetLiquidAlcohol(__instance.amount);
            if (alcohol > 0f)
            {
                __state = alcohol;
                return;
            }
            // Water, coffee and the teas: hydrating and alcohol-free, so a gulp dilutes what is still
            // in the stomach. Sea water hydrates negatively and does nothing here either.
            if (Liquids.GetLiquidHydration(__instance.amount) > 0)
            {
                __state = -Plugin.WaterClearsStomach.Value;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(float __state)
        {
            if (!Plugin.Enabled.Value || __state == 0f) return;
            if (__state > 0f) Drunkenness.Swallow(__state);
            else Drunkenness.Dilute(-__state);
        }
    }

    /// <summary>
    /// The random elixir slams PlayerNeeds.alcohol to 50 outright. Treat that as a swallow so it feeds
    /// the same model instead of being invisible to it.
    /// </summary>
    [HarmonyPatch(typeof(ShipItemRandomElixir), "OnAltActivate")]
    public static class ElixirPatch
    {
        [HarmonyPrefix]
        public static void Prefix(out float __state)
        {
            __state = PlayerNeeds.alcohol;
        }

        [HarmonyPostfix]
        public static void Postfix(float __state)
        {
            if (!Plugin.Enabled.Value) return;
            float jump = PlayerNeeds.alcohol - __state;
            if (jump > 0f) Drunkenness.Swallow(jump);
        }
    }

    /// <summary>
    /// Vanilla owns PlayerNeeds.alcohol: it decays it and clamps it to 0..100 every LateUpdate, and
    /// PlayerAlcohol reads it the same frame for bloom and exposure. Rather than fight that, drive it
    /// from the shadow BAC right after vanilla has had its turn, so the stock visuals and vanilla's
    /// awake rest drain both follow this model instead of running on a separate number. The drain is
    /// deliberately left alone: drinking to get tired sooner is what alcohol is for in this game.
    ///
    /// The one exception is the bed. Vanilla keeps the alcohol drain running whenever you are not
    /// actually asleep, including lying in the bed between its 4.5 hour sleep cycles, so a drunk
    /// player watches the bar go down in bed. While GameState.inBed is set, vanilla is handed an
    /// alcohol of zero for its update, so rest never goes down in bed; the real value is put back
    /// straight after for the visuals and the save.
    /// </summary>
    [HarmonyPatch(typeof(PlayerNeeds), "LateUpdate")]
    public static class PlayerNeedsLateUpdatePatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            if (!Plugin.Enabled.Value) return;
            if (GameState.inBed != null) PlayerNeeds.alcohol = 0f;
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!Plugin.Enabled.Value) return;
            PlayerNeeds.alcohol = Mathf.Clamp(Drunkenness.Bac, 0f, 100f);
        }
    }

    /// <summary>
    /// A loaded save restores PlayerNeeds.alcohol but knows nothing about the shadow BAC. Seed from it,
    /// otherwise loading while drunk hands you a free sober-up on the next frame.
    /// </summary>
    [HarmonyPatch(typeof(SaveLoadManager), "LoadNeeds")]
    public static class LoadNeedsPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            Drunkenness.SeedFromVanilla(PlayerNeeds.alcohol);
            Drunkenness.BlackedOut = false;
            Drunkenness.GraceRemaining = 0f;
        }
    }

    /// <summary>
    /// Testing hold for the vanilla half of the look. PlayerAlcohol ramps bloom and exposure from
    /// PlayerNeeds.alcohol, but feeding the hold into that value would also switch on vanilla's
    /// alcohol-driven rest drain (15 per game hour at full, which empties you in about 13 real minutes
    /// of tuning). So PlayerNeeds.alcohol stays real and this redoes PlayerAlcohol's own three lines
    /// with the held level right after it runs. Turning the hold off needs no cleanup: vanilla rewrites
    /// the same settings from the real value next frame.
    /// </summary>
    [HarmonyPatch(typeof(PlayerAlcohol), "Update")]
    public static class PlayerAlcoholHoldPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PlayerAlcohol __instance)
        {
            if (!Plugin.Enabled.Value || Drunkenness.HoldLevel <= 0f) return;
            if (__instance == null || __instance.postProcessing == null) return;

            // Same formula as PlayerAlcohol.Update, driven by the held level instead.
            float t = Mathf.Clamp01(Drunkenness.EffectiveBac / 100f);
            var bloom = __instance.postProcessing.bloom.settings;
            bloom.bloom.threshold = Mathf.Lerp(1.05f, 0f, t);
            bloom.bloom.radius = Mathf.Lerp(2.5f, 5f, t);
            __instance.postProcessing.bloom.settings = bloom;
            var grading = __instance.postProcessing.colorGrading.settings;
            grading.basic.postExposure = Mathf.Lerp(0.66f, -1f, t);
            __instance.postProcessing.colorGrading.settings = grading;
        }
    }
}
