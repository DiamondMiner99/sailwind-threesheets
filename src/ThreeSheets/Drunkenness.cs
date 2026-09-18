using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// The drunkenness model.
    ///
    /// Vanilla clamps PlayerNeeds.alcohol to 0..100 every LateUpdate, so it cannot tell the difference
    /// between six rums downed in ten seconds and six rums over an evening. Both read as 100. This keeps
    /// an unclamped shadow value so "too much too fast" is actually measurable.
    ///
    /// Two compartments, because the delay is the whole mechanic:
    ///   stomach  - what you have swallowed but not yet felt
    ///   bac      - what is in you now, and what everything else reads
    /// A sip lands in the stomach and bleeds into the bloodstream over the next while. Chug a bottle and
    /// the stomach keeps loading you long after you stop drinking, which is how people actually get
    /// caught out. Space the same bottle over an evening and decay keeps pace with absorption.
    ///
    /// Units match vanilla: one sip of rum is 18, wine 12, beer 6 (Liquids.GetLiquidAlcohol).
    /// </summary>
    public static class Drunkenness
    {
        public static float Bac { get; private set; }
        public static float Stomach { get; private set; }

        /// <summary>Set while the player is unconscious, to stop a second blackout stacking on the first.</summary>
        public static bool BlackedOut;

        /// <summary>Counts down after a blackout. Effects still run, but you cannot black out again yet.</summary>
        public static float GraceRemaining;

        /// <summary>Everything the player has swallowed, felt or not. What the blackout threshold tests.</summary>
        public static float Committed => Bac + Stomach;

        /// <summary>Testing hold from "6. Testing/HoldDrunkAt", or 0 when off.</summary>
        public static float HoldLevel =>
            (Plugin.HoldDrunkAt != null && Plugin.HoldDrunkAt.Value > 0f) ? Plugin.HoldDrunkAt.Value : 0f;

        /// <summary>
        /// What the VISUALS read: real blood alcohol, raised to the testing hold if one is set. Real
        /// drinking above the hold still shows. Nothing that has consequences reads this - the blackout
        /// trigger and the vanilla alcohol value both use the real Bac, so holding drunk for a tuning
        /// session never knocks you out or drains your rest.
        /// </summary>
        public static float EffectiveBac => Mathf.Max(Bac, HoldLevel);

        /// <summary>0 at the sober end of the visible ramp, 1 at fully drunk. Drives the visual effects.</summary>
        public static float Intensity
        {
            get
            {
                float level = EffectiveBac;
                float start = Plugin.EffectsStart.Value;
                float full = Plugin.EffectsFull.Value;
                if (full <= start) return level > start ? 1f : 0f;
                return Mathf.Clamp01((level - start) / (full - start));
            }
        }

        /// <summary>
        /// 0 until you are near the edge, 1 at the threshold. Drives the tunnel vision, which is the only
        /// warning the player gets that a blackout is coming. Tests Committed rather than Bac so the
        /// warning arrives while the drink is still on its way in, not after it has already landed.
        /// </summary>
        public static float Peril
        {
            get
            {
                if (!Plugin.BlackoutEnabled.Value && HoldLevel <= 0f) return 0f;

                // A blackout that is currently refused has no collapse to warn about. The refusal lasts
                // as long as the hull is taking water (BlackoutGuard.RefuseToStart), and pinning the
                // vignette fully closed for that whole fight is the opposite of what this release is
                // for: the player is bailing and pumping, and they need to see the ship. The warning
                // comes straight back the moment the bilge is clear, which is also when they can drop.
                // The testing hold is exempt, so the vignette can still be tuned anywhere.
                if (HoldLevel <= 0f && BoatWatch.FloodingOrSunk()) return 0f;

                float threshold = Plugin.BlackoutThreshold.Value;
                float warn = threshold * Mathf.Clamp01(Plugin.WarnFraction.Value);
                if (threshold <= warn) return 0f;
                // The hold counts here too, so the tunnel vision can be tuned without drinking. It is
                // still visual: ShouldBlackOut reads the real Bac.
                float level = Mathf.Max(Committed, HoldLevel);
                return Mathf.Clamp01((level - warn) / (threshold - warn));
            }
        }

        /// <summary>
        /// Deliberately tests Bac and not Committed: the drink has to actually reach you before it can
        /// drop you. Chug nine rums and the tunnel vision closes in within a second, because that reads
        /// Committed, but you stay upright for another half a minute while it comes on. That gap is the
        /// whole mechanic, and it is also the window in which a player can do something about it.
        /// </summary>
        public static bool ShouldBlackOut
        {
            get
            {
                if (!Plugin.BlackoutEnabled.Value) return false;
                if (BlackedOut || GraceRemaining > 0f) return false;
                return Bac >= Plugin.BlackoutThreshold.Value;
            }
        }

        public static void Swallow(float alcohol)
        {
            if (alcohol <= 0f) return;
            Stomach += alcohol;
            Plugin.Log.LogInfo($"Swallowed {alcohol:F0}. stomach={Stomach:F0} bac={Bac:F0} committed={Committed:F0}");
        }

        /// <summary>
        /// A gulp of water. Clears alcohol that has not reached the blood yet, and nothing else: what
        /// has already hit you is cleared by time and sleep only. So water is a way to stop it getting
        /// worse in the warning window, not a way to chug yourself sober.
        /// </summary>
        public static void Dilute(float amount)
        {
            if (amount <= 0f || Stomach <= 0f) return;
            float cleared = Mathf.Min(Stomach, amount);
            Stomach -= cleared;
            if (Stomach < 0.01f) Stomach = 0f;
            Plugin.Log.LogInfo($"Diluted {cleared:F0}. stomach={Stomach:F0} bac={Bac:F0} committed={Committed:F0}");
        }

        /// <summary>
        /// How well the player rests while passed out, as a share of normal sleep: SleepQuality at the
        /// blackout threshold, 1 when sober, linear between. Blackout only; a bed rests you as vanilla.
        /// </summary>
        public static float SleepQualityNow
        {
            get
            {
                float threshold = Mathf.Max(1f, Plugin.BlackoutThreshold.Value);
                float sobriety = 1f - Mathf.Clamp01(Bac / threshold);
                return Mathf.Lerp(Mathf.Clamp01(Plugin.SleepQuality.Value), 1f, sobriety);
            }
        }

        /// <summary>Seed from a loaded save, or from anything that writes PlayerNeeds.alcohol behind our back.</summary>
        public static void SeedFromVanilla(float alcohol)
        {
            Bac = Mathf.Max(0f, alcohol);
            Stomach = 0f;
            Plugin.Log.LogInfo($"Seeded bac={Bac:F0} from vanilla alcohol.");
        }

        public static void Reset()
        {
            Bac = 0f;
            Stomach = 0f;
        }

        /// <summary>
        /// Coming round after sleeping it off: whatever is left in the blood stays, so you wake groggy
        /// and it wears off normally, but the stomach is emptied. Anything still in there would carry on
        /// absorbing and could put you straight back down.
        /// </summary>
        public static void SleepOff(float residual)
        {
            Stomach = 0f;
            Bac = Mathf.Clamp(Mathf.Min(Bac, Mathf.Max(0f, residual)), 0f, 1000f);
            GraceRemaining = Plugin.BlackoutGraceSeconds.Value;
        }

        /// <summary>
        /// Shaken awake rather than slept off: whatever is in the blood stays exactly where it is,
        /// because a blackout cut short after twenty seconds burned almost none of it and sobering the
        /// player up would be a reward for sinking. The stomach is still emptied - anything left in
        /// there would carry on absorbing and put them straight back down - and the grace period is
        /// armed so they get time to fight for the ship before the next collapse.
        /// </summary>
        public static void WakeRough()
        {
            Stomach = 0f;
            GraceRemaining = Plugin.BlackoutGraceSeconds.Value;
        }

        /// <summary>
        /// The brake for a blackout that keeps failing. Keeps the drink - vanilla's awake rest drain
        /// reads PlayerNeeds.alcohol and is not this mod's to switch off - but parks the level just
        /// under the threshold, so a sequence that is broken for a structural reason stops firing
        /// instead of dropping the player again every time the grace period runs out.
        /// </summary>
        public static void HoldUnderThreshold()
        {
            Stomach = 0f;
            float threshold = Plugin.BlackoutThreshold.Value;
            if (Bac >= threshold) Bac = Mathf.Max(0f, threshold - 1f);
            GraceRemaining = Mathf.Max(GraceRemaining, Plugin.BlackoutGraceSeconds.Value);
        }

        /// <summary>Wipe the slate. Used by the watchdog, where the safe thing is to leave nothing behind.</summary>
        public static void SoberUp()
        {
            Bac = 0f;
            Stomach = 0f;
            GraceRemaining = Plugin.BlackoutGraceSeconds.Value;
        }

        /// <summary>Counted on unscaled time, so a grace period is the same length however time is running.</summary>
        public static void TickGrace(float unscaledDt)
        {
            if (GraceRemaining > 0f) GraceRemaining -= unscaledDt;
        }

        /// <summary>
        /// Takes scaled Time.deltaTime. Sleep runs at 16x, and sleeping a night off should sober you up
        /// the way vanilla does rather than crawling along at real-time rates.
        /// </summary>
        public static void Tick(float dt, float decayMultiplier = 1f)
        {
            if (Stomach > 0f)
            {
                float absorbed = Stomach * Mathf.Clamp01(Plugin.AbsorbRate.Value * dt);
                Stomach -= absorbed;
                Bac += absorbed;
                if (Stomach < 0.01f) Stomach = 0f;
            }

            // Decay tracks the sun, matching vanilla's 12/sec * timescale, so sobering up still takes
            // roughly a night and time acceleration works the way the player expects.
            float timescale = (Sun.sun != null) ? Sun.sun.timescale : 1f;
            Bac -= Plugin.DecayRate.Value * dt * timescale * Mathf.Max(0f, decayMultiplier);
            if (Bac < 0f) Bac = 0f;
        }
    }
}
