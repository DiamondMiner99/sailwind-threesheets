using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Passing out.
    ///
    /// This deliberately does NOT reuse Sleep.FallAsleep or Recovery.RecoverPlayer:
    ///   - Recovery teleports you and your boat to the last port and takes a cut of every currency.
    ///     Fine for scurvy; as a drinking penalty it is a free tow home from wherever you got lost.
    ///   - Sleep.FallAsleep is gated by the co-op mod behind everyone being in bed. A guest who calls
    ///     it gets a prefix that returns false, so the blackout would silently do nothing.
    ///
    /// Order matters here. You fall at normal speed, because a physics fall under a 16x timescale on a
    /// 10x fixed step is not a fall, it is a glitch. Only once you are down and the screen is black does
    /// the clock start running.
    /// </summary>
    public static class BlackoutSequence
    {
        // Vanilla's own sleep constants, from Sleep.Awake.
        private const float SleepTimescale = 16f;
        private const float InitialTimeStep = 0.02222f;
        private const float SleepTimeStep = InitialTimeStep * 10f;

        /// <summary>Vanilla sleep restores 8 per game hour: PlayerNeeds.LateUpdate, dt * 8 * timescale.</summary>
        private const float VanillaSleepPerHour = 8f;

        /// <summary>
        /// Hard real-time ceiling on a warped blackout, however many game hours were asked for. If the
        /// warp is defeated the same hours take 16x longer, and no drinking mechanic is worth leaving
        /// someone staring at a black screen for eight minutes.
        /// </summary>
        private const float RealSecondsCap = 75f;

        private static FieldInfo sleepDurationField;
        private static bool sleepDurationLookedUp;

        /// <summary>Game hours the last wait actually covered, used to size the hangover honestly.</summary>
        private static float hoursSlept;

        public static IEnumerator Run(MonoBehaviour runner)
        {
            Drunkenness.BlackedOut = true;

            bool warp = Plugin.TimeWarpEnabled.Value && !Compat.OtherPlayerConnected && Sun.sun != null;
            float outSeconds = Mathf.Max(1f, Plugin.BlackoutSeconds.Value);
            hoursSlept = 0f;

            // Watchdog budget, in real seconds. Deliberately NOT derived from the warp rate: if the warp
            // gets defeated, the wait takes 16x longer than the arithmetic says and the player sits
            // behind a black screen for minutes.
            float budget = (warp ? RealSecondsCap : outSeconds) + 45f;
            Plugin.WatchdogDeadline = Time.unscaledTime + budget;

            Plugin.Log.LogWarning(
                $"Blacking out. bac={Drunkenness.Bac:F0} committed={Drunkenness.Committed:F0} " +
                $"warp={warp}");

            SuspendControl();

            // --- Go down, at normal speed ---
            var ragdoll = Plugin.FallEnabled.Value ? runner.GetComponent<RagdollFall>() : null;
            var viewFall = Plugin.FallEnabled.Value ? runner.GetComponent<CollapseAnimator>() : null;

            bool physical = ragdoll != null && Plugin.FallPhysical.Value && ragdoll.Begin();
            bool scripted = !physical && viewFall != null && viewFall.Begin(Plugin.CollapseSeconds.Value);

            if (physical)
            {
                // Fade weighted late and tied to the fall's own length, so vision closes as you land.
                runner.StartCoroutine(Fade(1f, Plugin.FallWatchSeconds.Value * 0.9f, 2.6f));
                float guard = 0f;
                while (!ragdoll.Settled && guard < Plugin.FallWatchSeconds.Value + 2f)
                {
                    guard += Time.unscaledDeltaTime;
                    yield return null;
                }
                SetFadeLevel(1f);
                // Stop the body wandering the deck for the rest of the blackout.
                ragdoll.Freeze();
            }
            else if (scripted)
            {
                runner.StartCoroutine(Fade(1f, Plugin.CollapseSeconds.Value * 0.95f, 2.4f));
                while (viewFall.Running) yield return null;
                SetFadeLevel(1f);
            }
            else
            {
                yield return runner.StartCoroutine(Fade(1f, 2.5f));
            }

            // --- Out cold. Now the clock runs ---
            StartWarp(warp);
            ShowPassedOut();

            if (warp)
                yield return runner.StartCoroutine(WaitUntilSleptOff());
            else
                yield return runner.StartCoroutine(WaitRealSeconds(outSeconds));

            ClearStatus();

            // Restore time BEFORE the wake-up fade, so coming round runs at normal speed and the player
            // is not handed back a ship moving at 16x while their eyes are still opening.
            Restore();

            ApplyHangover(hoursSlept);

            // Stand back up behind the black screen, so waking is a fade-in and not a snap.
            if (physical && ragdoll != null) ragdoll.Stop();
            else if (scripted && viewFall != null) viewFall.Stop();

            yield return runner.StartCoroutine(Fade(0f, 3.5f));

            LookDrift.ResetAccumulator();
            Plugin.WatchdogDeadline = 0f;
            Drunkenness.BlackedOut = false;
            Plugin.Log.LogInfo($"Came round after {hoursSlept:F1} game hours.");
        }

        /// <summary>
        /// Counts game hours the way Sun does, so it stays correct at any clock rate and stops advancing
        /// when the sun is paused. Bails early if the player leaves the world, which would otherwise
        /// strand the warp in a dead scene.
        /// </summary>
        private static IEnumerator WaitUntilSleptOff()
        {
            float realElapsed = 0f;
            int reasserts = 0;
            float minHours = Plugin.MinOutHours.Value;
            float maxHours = Mathf.Max(minHours, Plugin.MaxOutHours.Value);
            float wakeAt = Plugin.WakeAtBac.Value;

            while (true)
            {
                if (!GameState.playing)
                {
                    Plugin.Log.LogWarning("Left the world mid-blackout, cutting it short.");
                    yield break;
                }

                // Someone else put the clock back. Reassert a couple of times in case it was a one-off,
                // then stop fighting: whatever is doing it is entitled to, and a tug of war over
                // Time.timeScale is worse for the player than a shorter blackout.
                if (Time.timeScale < SleepTimescale - 0.5f)
                {
                    if (reasserts < 3)
                    {
                        reasserts++;
                        Plugin.Log.LogWarning(
                            $"Time warp was reset by something else (timeScale={Time.timeScale:F2}), " +
                            $"reasserting ({reasserts}/3).");
                        Time.fixedDeltaTime = SleepTimeStep;
                        Time.timeScale = SleepTimescale;
                    }
                    else
                    {
                        Plugin.Log.LogWarning("Time warp keeps being reset, ending the blackout early.");
                        yield break;
                    }
                }

                // Vanilla Sleep.Update auto-wakes at 4.5 game hours and it is counting while our sleep
                // flag is set. Hold its counter down so any configured duration is safe.
                ZeroVanillaSleepDuration();

                float timescale = (Sun.sun != null) ? Sun.sun.timescale : 0f;
                float dtHours = Time.deltaTime * timescale;
                hoursSlept += dtHours;

                // Sobering runs faster while unconscious. At the normal rate a serious binge is half a
                // day on a black screen; this keeps a heavy night to a few hours without flattening the
                // difference between a heavy one and a light one.
                Drunkenness.Tick(Time.deltaTime, Plugin.SoberingWhileOut.Value);
                SleepItOff(dtHours);

                // Out until it has worn off, which is what makes a big night cost more of the ship's day
                // than a couple of rums.
                if (hoursSlept >= minHours && Drunkenness.Bac <= wakeAt) yield break;
                if (hoursSlept >= maxHours)
                {
                    Plugin.Log.LogInfo(
                        $"Still at {Drunkenness.Bac:F0} after the {maxHours:F1}h limit; waking up drunk.");
                    yield break;
                }

                realElapsed += Time.unscaledDeltaTime;
                if (realElapsed > RealSecondsCap)
                {
                    Plugin.Log.LogWarning(
                        $"Blackout hit the {RealSecondsCap:F0}s real-time cap after {hoursSlept:F1} game " +
                        "hours. Ending it rather than holding the screen black.");
                    yield break;
                }

                yield return null;
            }
        }

        /// <summary>The co-op path: no timescale change, so only the hours the world runs anyway pass.</summary>
        private static IEnumerator WaitRealSeconds(float seconds)
        {
            float realElapsed = 0f;
            while (realElapsed < seconds)
            {
                if (!GameState.playing) yield break;
                realElapsed += Time.unscaledDeltaTime;
                float timescale = (Sun.sun != null) ? Sun.sun.timescale : 0f;
                hoursSlept += Time.deltaTime * timescale;
                Drunkenness.Tick(Time.deltaTime);
                SleepItOff(Time.deltaTime * timescale);
                yield return null;
            }
        }

        // ---- status text on the black screen ----

        /// <summary>
        /// Vanilla pass-out wording, in the TextMesh vanilla uses for it: Sleep.instance.recoveryText is
        /// where Recovery writes "You passed out from thirst." and it renders over a fully faded screen.
        ///
        /// Deliberately NOT a clock or a count of hours. Sailwind has no free clock - working out the time
        /// from the sun and whatever timepiece you carry is part of finding your position - so the
        /// blackout must not hand the player the time. The sleep bar filling is the only sense of time
        /// passing, the same as when sleeping.
        /// </summary>
        private static void ShowPassedOut()
        {
            try
            {
                if (Sleep.instance == null || Sleep.instance.recoveryText == null) return;
                var tm = Sleep.instance.recoveryText;

                // Vanilla never shows this text and the sleep bar together - Recovery sets
                // GameState.recovering, which hides SleepUI's bar - so both sit dead center and overlap.
                // Lift the text above the bar with trailing blank lines rather than by moving the
                // object: that keeps it inside the text's own layout, so it holds however the view is
                // tilted after a fall. How far blank lines move it depends on where the TextMesh is
                // anchored, which lives in the scene rather than the code, hence the switch.
                string msg = "You passed out.";
                switch (tm.anchor)
                {
                    case TextAnchor.MiddleLeft:
                    case TextAnchor.MiddleCenter:
                    case TextAnchor.MiddleRight:
                        msg += "\n\n";      // three-line block centered on the anchor: text 1 line up
                        break;
                    case TextAnchor.LowerLeft:
                    case TextAnchor.LowerCenter:
                    case TextAnchor.LowerRight:
                        msg += "\n";        // block grows upward from the anchor: text 1 line up
                        break;
                    default:
                        // Top-anchored text only grows downward, so blank lines cannot lift it. Say so
                        // in the log rather than guess at a transform offset.
                        Plugin.Log.LogWarning($"Pass-out text is top-anchored ({tm.anchor}); it may overlap the sleep bar.");
                        break;
                }
                tm.text = msg;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Could not show the pass-out message: " + e.Message);
            }
        }

        private static void ClearStatus()
        {
            try
            {
                if (Sleep.instance != null && Sleep.instance.recoveryText != null)
                    Sleep.instance.recoveryText.text = "";
            }
            catch { /* nothing to clear */ }
        }

        // ---- suspend and restore ----

        private static void SuspendControl()
        {
            try
            {
                MouseLook.ToggleMouseLook(false);
                Refs.SetPlayerControl(false);
                Plugin.ControlTaken = true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Could not take player control for blackout: " + e.Message);
            }

            // Suspend needs entirely while unconscious. Without this, warping four hours drains food and
            // water at 16x, and a player who was already thirsty gets a vanilla thirst PassOut
            // mid-blackout - which is Recovery, which teleports them and their boat to port. The cost of
            // the lost hours is charged deliberately in ApplyHangover instead, INCLUDING the rest that
            // would otherwise have been regained, since this flag stops that too.
            try
            {
                if (PlayerNeeds.instance != null)
                {
                    Plugin.GodModeWas = PlayerNeeds.instance.godMode;
                    Plugin.GodModeHeld = true;
                    PlayerNeeds.instance.godMode = true;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Could not suspend needs for blackout: " + e.Message);
            }
        }

        private static void StartWarp(bool warp)
        {
            if (!warp) return;
            try
            {
                // GameState.sleeping is what the game treats as "time is warping because the player is
                // unconscious", and warping without it is not a private decision. Sailwind Co-op runs an
                // orphan-timewarp backstop that resets any timeScale != 1 while awake, written to catch a
                // vanilla sleep coroutine firing after its sleep was aborted - and it caught this mod
                // instead, healing the warp within a frame or two. Its own comment names the legitimate
                // case: "recovery's legit warp has GameState.sleeping==true". So claim the flag honestly.
                //
                // eyesFullyClosed is deliberately left alone. Sleep.WakeUp early-returns without it, so
                // vanilla cannot wake the player out from under this sequence.
                Plugin.SleepFlagWas = GameState.sleeping;
                Plugin.SleepFlagHeld = true;
                GameState.sleeping = true;

                // eyesFullyClosed is what makes vanilla SleepUI show the sleep bar (it hides the bar
                // while this is false), and what OceanUpdaterCrest reads to calm the sea to a quarter of
                // its inertia during a sleep warp. Both are the vanilla sleep behavior wanted here. The
                // cost is that Sleep.WakeUp stops refusing; nothing calls it during a solo blackout
                // except Sleep.Update's 4.5-hour cap, and that counter is held at zero.
                Plugin.EyesFlagWas = GameState.eyesFullyClosed;
                Plugin.EyesFlagHeld = true;
                GameState.eyesFullyClosed = true;

                Plugin.TimeScaleWas = Time.timeScale;
                Plugin.FixedStepWas = Time.fixedDeltaTime;
                Plugin.TimeWarpHeld = true;
                Time.fixedDeltaTime = SleepTimeStep;
                Time.timeScale = SleepTimescale;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Could not start the time warp: " + e.Message);
                Plugin.TimeWarpHeld = false;
            }
        }

        /// <summary>
        /// Undoes everything SuspendControl and StartWarp did. Safe to call twice, and safe to call from
        /// the watchdog on a sequence that died halfway through.
        /// </summary>
        public static void Restore()
        {
            if (Plugin.TimeWarpHeld)
            {
                try
                {
                    // Restore to what was actually there, but never to a stopped or warped clock -
                    // reading a bad value back would leave the game frozen or stuck at 16x.
                    Time.timeScale = (Plugin.TimeScaleWas > 0.01f && Plugin.TimeScaleWas < 2f)
                        ? Plugin.TimeScaleWas : 1f;
                    Time.fixedDeltaTime = (Plugin.FixedStepWas > 0.001f && Plugin.FixedStepWas < 0.1f)
                        ? Plugin.FixedStepWas : InitialTimeStep;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError("Could not end the time warp cleanly: " + e.Message);
                    Time.timeScale = 1f;
                    Time.fixedDeltaTime = InitialTimeStep;
                }
                Plugin.TimeWarpHeld = false;
            }

            if (Plugin.EyesFlagHeld)
            {
                try { GameState.eyesFullyClosed = Plugin.EyesFlagWas; }
                catch { GameState.eyesFullyClosed = false; }
                Plugin.EyesFlagHeld = false;
            }

            if (Plugin.SleepFlagHeld)
            {
                try
                {
                    GameState.sleeping = Plugin.SleepFlagWas;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError("Could not clear the sleep flag: " + e.Message);
                    GameState.sleeping = false;
                }
                Plugin.SleepFlagHeld = false;

                // SleepUI switches the mixer to the muffled sleep snapshot while its bar is up, and only
                // switches back if the bar is still active when sleep ends. Anything that hid the bar
                // part-way (a cursor menu does) leaves the audio muffled, a bug Sailwind Co-op has
                // already chased once. Asking for the normal mix again is harmless if it is already on.
                try
                {
                    if (AudioMixers.instance != null)
                        AudioMixers.instance.gameActiveSnapshot.TransitionTo(4f);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Could not restore the normal audio mix: " + e.Message);
                }
            }

            if (Plugin.GodModeHeld)
            {
                try
                {
                    if (PlayerNeeds.instance != null) PlayerNeeds.instance.godMode = Plugin.GodModeWas;
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError("Could not restore needs after blackout: " + e.Message);
                }
                Plugin.GodModeHeld = false;
            }

            if (Plugin.ControlTaken)
            {
                try
                {
                    Refs.SetPlayerControl(true);
                    MouseLook.ToggleMouseLook(true);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogError("Could not hand player control back: " + e.Message);
                }
                Plugin.ControlTaken = false;
            }
        }

        /// <summary>
        /// Runs the needs the way sleeping does, because godMode stopped the game running them.
        ///
        /// godMode is still necessary: without it a player who went down thirsty hits the vanilla thirst
        /// PassOut part-way through, and that is Recovery, which teleports them and their boat to port.
        /// So this does the vanilla per-hour arithmetic itself, with floors instead of pass-outs:
        ///   food -3/h, water -4/h, vitamins and protein -0.2/h  (PlayerNeeds.LateUpdate)
        ///   rest +8/h while asleep, sleep debt paid down first  (same place)
        /// The only change is the rest rate. You are unconscious, not rested, so it starts at
        /// SleepQuality of normal and climbs toward normal as the alcohol wears off during the blackout.
        /// Because it lands on PlayerNeeds.sleep every frame, the vanilla sleep bar fills on screen.
        /// </summary>
        private static void SleepItOff(float hours)
        {
            if (hours <= 0f) return;
            float floor = Mathf.Clamp(Plugin.HangoverFloor.Value, 0f, 100f);

            PlayerNeeds.food = Drain(PlayerNeeds.food, 3f * hours, floor);
            PlayerNeeds.water = Drain(PlayerNeeds.water, 4f * hours, floor);
            PlayerNeeds.vitamins = Drain(PlayerNeeds.vitamins, 0.2f * hours, floor);
            PlayerNeeds.protein = Drain(PlayerNeeds.protein, 0.2f * hours, floor);

            float threshold = Mathf.Max(1f, Plugin.BlackoutThreshold.Value);
            float sobriety = 1f - Mathf.Clamp01(Drunkenness.Bac / threshold);
            float quality = Mathf.Lerp(Mathf.Clamp01(Plugin.SleepQuality.Value), 1f, sobriety);

            float rest = VanillaSleepPerHour * hours * quality;
            if (PlayerNeeds.sleepDebt < 100f)
            {
                PlayerNeeds.sleepDebt = Mathf.Min(100f, PlayerNeeds.sleepDebt + rest);
                rest *= 0.2f;
            }
            PlayerNeeds.sleep = Mathf.Min(100f, PlayerNeeds.sleep + rest);
        }

        private static float Drain(float value, float amount, float floor)
        {
            if (value <= floor) return value;
            return Mathf.Max(floor, value - amount);
        }

        /// <summary>
        /// What is left after the sleep itself: the alcohol's own dehydration, flat because it came from
        /// the drink and not from the hours (and so it still applies in co-op, where no hours pass).
        /// </summary>
        private static void ApplyHangover(float hours)
        {
            // Keep what is still in the blood, so you come round groggy rather than magically sober,
            // and it wears off the rest of the way while you sail. The stomach is emptied either way.
            Drunkenness.SleepOff(Plugin.WakeAtBac.Value);
            PlayerNeeds.alcohol = Mathf.Clamp(Drunkenness.Bac, 0f, 100f);

            float floor = Mathf.Clamp(Plugin.HangoverFloor.Value, 0f, 100f);
            PlayerNeeds.water = Drain(PlayerNeeds.water, Plugin.HangoverWaterCost.Value, floor);

            Plugin.Log.LogInfo(
                $"Came round: {hours:F1}h out, bac {Drunkenness.Bac:F0}, rest {PlayerNeeds.sleep:F0}, " +
                $"debt {PlayerNeeds.sleepDebt:F0}, water {PlayerNeeds.water:F0}, food {PlayerNeeds.food:F0}.");
        }

        /// <summary>
        /// Sleep.Update counts currentSleepDuration whenever GameState.sleeping is set and calls WakeUp
        /// past 4.5 game hours. This sequence owns when the player wakes, so keep that counter at zero.
        /// </summary>
        private static void ZeroVanillaSleepDuration()
        {
            if (!sleepDurationLookedUp)
            {
                sleepDurationLookedUp = true;
                sleepDurationField = AccessTools.Field(typeof(Sleep), "currentSleepDuration");
                if (sleepDurationField == null)
                    Plugin.Log.LogWarning("Sleep.currentSleepDuration not found; keep OutHours under 4.5.");
            }
            if (sleepDurationField == null || Sleep.instance == null) return;
            try
            {
                sleepDurationField.SetValue(Sleep.instance, 0f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Could not hold the vanilla sleep counter: " + e.Message);
                sleepDurationField = null;
            }
        }

        // ---- fading ----

        /// <summary>
        /// Vanilla Blackout.FadeTo does the same job but steps on Time.deltaTime, which runs at 16x
        /// during the warp and would make both fades flash past. Unscaled only.
        /// </summary>
        private static IEnumerator Fade(float target, float duration, float ease = 1f)
        {
            var fade = GetFade();
            if (fade == null)
            {
                yield return new WaitForSecondsRealtime(duration);
                yield break;
            }

            float start = fade.currentAlpha;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                // ease > 1 holds the screen clear then drops it late, so a fall stays watchable.
                float k = Mathf.Pow(Mathf.Clamp01(elapsed / duration), ease);
                fade.SetFadeLevel(Mathf.Lerp(start, target, k));
                yield return null;
            }
            fade.SetFadeLevel(target);
        }

        private static void SetFadeLevel(float level)
        {
            var fade = GetFade();
            if (fade != null) fade.SetFadeLevel(level);
        }

        private static OVRScreenFade GetFade()
        {
            try
            {
                return (Camera.main != null) ? Camera.main.GetComponent<OVRScreenFade>() : null;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("No screen fade available: " + e.Message);
                return null;
            }
        }

        /// <summary>Last resort, called by the runner's watchdog if the sequence never finished.</summary>
        public static void ForceRecover()
        {
            Plugin.Log.LogError("Blackout watchdog fired - forcing everything back.");
            Restore();
            ClearStatus();
            try
            {
                var ragdoll = Object.FindObjectOfType<RagdollFall>();
                if (ragdoll != null) ragdoll.Stop();
                var viewFall = Object.FindObjectOfType<CollapseAnimator>();
                if (viewFall != null) viewFall.Stop();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Could not stand the camera back up: " + e.Message);
            }
            try
            {
                var fade = GetFade();
                if (fade != null) fade.SetFadeLevel(0f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("Could not clear the screen fade: " + e.Message);
            }
            Drunkenness.SoberUp();
            Drunkenness.BlackedOut = false;
            Plugin.WatchdogDeadline = 0f;
        }
    }
}
