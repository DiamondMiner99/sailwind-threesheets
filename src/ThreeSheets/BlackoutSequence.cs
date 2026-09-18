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

        /// <summary>How long the plain fade to black takes when there is no fall to watch.</summary>
        private const float BlindFadeSeconds = 2.5f;

        private static FieldInfo sleepDurationField;
        private static bool sleepDurationLookedUp;

        /// <summary>Game hours the last wait actually covered, used to size the hangover honestly.</summary>
        private static float hoursSlept;

        /// <summary>Why the last blackout ended early, or None if it ran its course.</summary>
        private static AbortReason abortReason;

        /// <summary>Set by DrunkRunner when the guard fires, so a dead coroutine is still escalated.</summary>
        private static AbortReason reported;
        private static float reportedAt;

        // One line per episode, not one per frame. Restore is called from DrunkRunner.Update every frame
        // while anything is still held, so any log inside it is a log at frame rate unless it is latched.
        private static bool standDownLogged;
        private static bool clockDeferredLogged;
        private static bool handbackFailedLogged;

        /// <summary>
        /// Forced recoveries since the last blackout that unwound on its own. Two in a row means the
        /// failure is structural rather than bad luck, and the escalation in ForceRecover stops it
        /// looping forever.
        /// </summary>
        private static int consecutiveForceRecovers;

        /// <summary>
        /// The exact string this mod last wrote into Sleep.instance.recoveryText, so ClearStatus can
        /// tell its own message from Recovery's. Null when we have written nothing.
        /// </summary>
        private static string statusWeWrote;

        /// <summary>
        /// Told from outside that the world has changed under a running blackout. The coroutine gets
        /// first refusal, because it unwinds in the right order and with a fade, so this only records
        /// when the reason first appeared and lets the runner see whether anyone acted on it.
        /// </summary>
        public static void ReportAbort(AbortReason reason)
        {
            if (reason == AbortReason.None) { reported = AbortReason.None; return; }
            if (reported == AbortReason.None) { reported = reason; reportedAt = Time.unscaledTime; }
        }

        /// <summary>True when the guard has been firing for half a second and the coroutine has not acted.</summary>
        public static bool AbortOverdue =>
            reported != AbortReason.None && Time.unscaledTime > reportedAt + 0.5f;

        public static AbortReason ReportedReason => reported;

        public static IEnumerator Run(MonoBehaviour runner)
        {
            abortReason = AbortReason.None;
            reported = AbortReason.None;

            // Vanilla will not let you into a bunk on a sinking ship (GPButtonBed.cs:21). Same idea one
            // notch earlier: sober enough to bail, drunk enough to have caused it.
            if (BlackoutGuard.RefuseToStart())
            {
                Plugin.Log.LogWarning("Not blacking out: the boat is taking water, or you are swimming or aloft.");
                // Short, so the log does not repeat at frame rate. Deliberately NOT BlackoutGraceSeconds:
                // the player drops the moment the bilge is clear, if they are still over the threshold.
                Drunkenness.GraceRemaining = Mathf.Max(Drunkenness.GraceRemaining, 5f);
                yield break;
            }

            int gen = ++Plugin.BlackoutGeneration;
            BlackoutGuard.Arm();

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

            // The fade to black runs beside the fall rather than inside it, so it has its own clock and
            // can outlive the loop that started it. Every path below therefore stops it before anything
            // else writes the screen alpha: an abort can end the fall a tenth of a second in, and a fade
            // left alive would finish on its own SetFadeLevel(1f) seconds AFTER the wake-up fade had
            // cleared the screen. That leaves the player awake, in control, and looking at a black
            // screen nothing will ever clear, which is the worst thing this mod can do to anyone.

            if (physical)
            {
                // Fade weighted late and tied to the fall's own length, so vision closes as you land.
                Coroutine toBlack = runner.StartCoroutine(Fade(gen, 1f, Plugin.FallWatchSeconds.Value * 0.9f, 2.6f));
                float guard = 0f;
                while (!ragdoll.Settled && guard < Plugin.FallWatchSeconds.Value + 2f)
                {
                    if (gen != Plugin.BlackoutGeneration) { StopFade(runner, toBlack); yield break; }
                    abortReason = BlackoutGuard.Check();
                    if (abortReason != AbortReason.None) break;
                    guard += Time.unscaledDeltaTime;
                    yield return null;
                }
                StopFade(runner, toBlack);
                // Full black even when this was cut short: the unwind stands the camera back up, and
                // that snap is what the black screen is there to hide.
                SetFadeLevel(1f);
                // Stop the body wandering the deck for the rest of the blackout.
                ragdoll.Freeze();
            }
            else if (scripted)
            {
                Coroutine toBlack = runner.StartCoroutine(Fade(gen, 1f, Plugin.CollapseSeconds.Value * 0.95f, 2.4f));
                while (viewFall.Running)
                {
                    if (gen != Plugin.BlackoutGeneration) { StopFade(runner, toBlack); yield break; }
                    abortReason = BlackoutGuard.Check();
                    if (abortReason != AbortReason.None) break;
                    yield return null;
                }
                StopFade(runner, toBlack);
                SetFadeLevel(1f);
            }
            else
            {
                // Watched frame by frame like the two fall loops rather than awaited. This stretch is
                // two and a half seconds long, and a leak crossing the wake line must not have to wait
                // it out before the blackout can end.
                Coroutine toBlack = runner.StartCoroutine(Fade(gen, 1f, BlindFadeSeconds));
                float waited = 0f;
                while (waited < BlindFadeSeconds)
                {
                    if (gen != Plugin.BlackoutGeneration) { StopFade(runner, toBlack); yield break; }
                    abortReason = BlackoutGuard.Check();
                    if (abortReason != AbortReason.None) break;
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                StopFade(runner, toBlack);
                // Nothing moved the camera on this path, so an abort leaves the alpha where it got to
                // and the wake-up fade takes it back from there instead of flashing the screen black.
                if (abortReason == AbortReason.None) SetFadeLevel(1f);
            }

            if (gen != Plugin.BlackoutGeneration) yield break;

            // The pause menu parks the clock at zero (StartMenu.cs:410-411). Writing 16x into a parked
            // clock takes the game back off pause and runs the world at 16x underneath the open menu, so
            // the warp waits for the clock to run again. The guard keeps running while it waits, so a
            // hull that floods while the menu is open still wakes the player.
            while (abortReason == AbortReason.None && warp && BlackoutGuard.GamePaused())
            {
                if (gen != Plugin.BlackoutGeneration) yield break;
                abortReason = BlackoutGuard.Check();
                if (abortReason != AbortReason.None) break;
                yield return null;
            }

            if (abortReason == AbortReason.None)
            {
                // --- Out cold. Now the clock runs ---
                // A flooding hull never sees fixedDeltaTime 0.2222, not even for the one frame between
                // starting the warp and the wait loop's first test.
                StartWarp(warp && !BoatWatch.FloodingOrSunk());
                ShowPassedOut();

                if (warp) yield return runner.StartCoroutine(WaitUntilSleptOff(gen));
                else yield return runner.StartCoroutine(WaitRealSeconds(outSeconds, gen));

                if (gen != Plugin.BlackoutGeneration) yield break;
            }

            bool rough = abortReason != AbortReason.None;
            if (rough) Plugin.Log.LogWarning($"Blackout ended early: {abortReason}.");

            // Disarm BEFORE the unwind, not after it. The runner checks the guard every frame while a
            // blackout is held, and the reason that ended this one is usually still true - the ship is
            // still flooding - so leaving it armed through the wake-up fade would have the runner's
            // escalation force a recovery on top of the orderly one already in progress.
            BlackoutGuard.Disarm();
            reported = AbortReason.None;

            ClearStatus();

            // Restore time BEFORE the wake-up fade, so coming round runs at normal speed and the player
            // is not handed back a ship moving at 16x while their eyes are still opening.
            Restore(rough);

            if (rough) ApplyRoughWake(hoursSlept);
            else ApplyHangover(hoursSlept);

            // Stand back up behind the black screen, so waking is a fade-in and not a snap.
            if (physical && ragdoll != null) ragdoll.Stop();
            else if (scripted && viewFall != null) viewFall.Stop();

            yield return runner.StartCoroutine(Fade(gen, 0f, rough ? Plugin.RoughWakeSeconds.Value : 3.5f));

            // Same rule as every other write in here: a sequence that has been replaced or
            // force-recovered leaves the tidying to whoever replaced it. ForceRecover has already
            // zeroed the deadline and cleared the flag, and it counts the failure it just handled.
            if (gen != Plugin.BlackoutGeneration) yield break;

            LookDrift.ResetAccumulator();
            Plugin.WatchdogDeadline = 0f;
            Drunkenness.BlackedOut = false;
            // A blackout that unwound on its own is proof the sequence still works, so the forced
            // recoveries that came before it were bad luck rather than something structural.
            consecutiveForceRecovers = 0;
            Plugin.Log.LogInfo($"Came round after {hoursSlept:F1} game hours.");
        }

        /// <summary>
        /// Counts game hours the way Sun does, so it stays correct at any clock rate and stops advancing
        /// when the sun is paused. Bails early if the player leaves the world, which would otherwise
        /// strand the warp in a dead scene.
        /// </summary>
        private static IEnumerator WaitUntilSleptOff(int gen)
        {
            float realElapsed = 0f;
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

                // The lease on the world this blackout was taken in. Checked before anything else in
                // the loop body on purpose: a check placed after a write would re-force 16x for one
                // frame on the way out. There is no reassert any more - if something takes the clock,
                // that is ClockTaken and the blackout ends rather than fighting over Time.timeScale.
                if (gen != Plugin.BlackoutGeneration) yield break;

                abortReason = BlackoutGuard.Check();
                if (abortReason != AbortReason.None) yield break;

                // Vanilla Sleep.Update auto-wakes at 4.5 game hours and it is counting while our sleep
                // flag is set. Hold its counter down so any configured duration is safe.
                ZeroVanillaSleepDuration();

                float timescale = (Sun.sun != null) ? Sun.sun.timescale : 0f;
                float dtHours = Time.deltaTime * timescale;
                hoursSlept += dtHours;

                // Sobering runs faster while unconscious. At the normal rate a serious binge is half a
                // day on a black screen; this keeps a heavy night to a few hours without flattening the
                // difference between a heavy one and a light one.
                Drunkenness.Tick(Time.deltaTime, Plugin.SoberingAsleep.Value);
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

                // The real-time cap is there to stop a defeated warp holding the screen black for
                // minutes. A paused game is not that: nothing is advancing, so nothing is overdue, and
                // spending the budget on a player who walked away from the pause menu would end the
                // blackout behind their back while the clock is still parked at zero.
                if (!BlackoutGuard.GamePaused()) realElapsed += Time.unscaledDeltaTime;
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
        private static IEnumerator WaitRealSeconds(float seconds, int gen)
        {
            float realElapsed = 0f;
            while (realElapsed < seconds)
            {
                if (!GameState.playing) yield break;

                if (gen != Plugin.BlackoutGeneration) yield break;

                abortReason = BlackoutGuard.Check();
                if (abortReason != AbortReason.None) yield break;

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
                statusWeWrote = msg;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Could not show the pass-out message: " + e.Message);
            }
        }

        /// <summary>
        /// Blanks our own pass-out message and nothing else. Sleep.instance.recoveryText is not this
        /// mod's field: Recovery writes its "recovering..." line into the same TextMesh about three real
        /// seconds in (Recovery.cs:36-50) and clears it itself at the end (Recovery.cs:110). ForceRecover
        /// can land in the middle of that, so the text is only blanked while it is still the exact string
        /// ShowPassedOut put there.
        /// </summary>
        private static void ClearStatus()
        {
            if (statusWeWrote == null) return;
            try
            {
                if (Sleep.instance != null && Sleep.instance.recoveryText != null
                    && Sleep.instance.recoveryText.text == statusWeWrote)
                    Sleep.instance.recoveryText.text = "";
            }
            catch { /* nothing to clear */ }
            statusWeWrote = null;
        }

        // ---- suspend and restore ----

        private static void SuspendControl()
        {
            // Set the flag BEFORE the write. Refs.SetPlayerControl is two unguarded assignments
            // (Refs.cs:31-35), so a throw between them must leave the mod believing it took control,
            // which is the safe direction. The old order, with the flag after the call, left the
            // controller disabled and the mod believing it had taken nothing.
            Plugin.ControlTaken = true;
            try
            {
                MouseLook.ToggleMouseLook(false);
                SetControl(false);
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

        /// <summary>
        /// Refs.SetPlayerControl (Refs.cs:31-35) has no null guards and writes two components in
        /// sequence, so a throw on the first line leaves both off, and a throw on the second re-enables
        /// OVRPlayerController while the CharacterController stays off - no movement, menus fine, which
        /// is the reported symptom exactly. Write each one in its own try.
        /// </summary>
        /// <returns>
        /// True only if BOTH components ended up in the requested state. A component that is missing or
        /// destroyed counts as a failure when TAKING control and as a success when handing it back:
        /// there is nothing left to hand back once the rig has gone with the scene or the main menu, and
        /// reading that as a failed handback pinned ControlTaken true for the rest of the session, with
        /// DrunkRunner retrying and logging every frame against references that were never coming back.
        /// </returns>
        private static bool SetControl(bool state)
        {
            bool ok = true;
            try
            {
                if ((bool)Refs.ovrController) Refs.ovrController.enabled = state;
                else if (!state) ok = false;
            }
            catch (System.Exception e) { ok = false; Plugin.Log.LogError("ovrController: " + e.Message); }

            try
            {
                if ((bool)Refs.charController) Refs.charController.enabled = state;
                else if (!state) ok = false;
            }
            catch (System.Exception e) { ok = false; Plugin.Log.LogError("charController: " + e.Message); }

            return ok;
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
                Plugin.SleepFlagWas = GameState.sleeping;
                Plugin.SleepFlagHeld = true;
                GameState.sleeping = true;

                // eyesFullyClosed is what makes vanilla SleepUI show the sleep bar (it hides the bar
                // while this is false), and what OceanUpdaterCrest reads to calm the sea to a quarter of
                // its inertia during a sleep warp. Both are the vanilla sleep behavior wanted here.
                //
                // The cost is that Sleep.WakeUp stops refusing. Three vanilla paths can then reach into
                // a live blackout: BoatDamage.Overflow at waterLevel > 0.1 (BoatDamage.cs:173, via
                // WaveSplashZone), a hard impact (BoatImpactSounds.cs:57-61) and running aground
                // (BoatImpactSounds.cs:110-116). All three are good reasons to come round, so the guard
                // treats a cleared sleeping flag as a wake request (AbortReason.VanillaWoke) and stands
                // the sequence down rather than fighting it.
                Plugin.EyesFlagWas = GameState.eyesFullyClosed;
                Plugin.EyesFlagHeld = true;
                GameState.eyesFullyClosed = true;

                Plugin.TimeScaleWas = Time.timeScale;
                Plugin.FixedStepWas = Time.fixedDeltaTime;
                Plugin.TimeWarpHeld = true;
                Time.fixedDeltaTime = SleepTimeStep;
                Time.timeScale = SleepTimescale;
                // Stamp what we wrote, so Restore can tell whether the clock it is looking at is still
                // ours to put back.
                Plugin.FixedStepWeWrote = SleepTimeStep;
                Plugin.TimeScaleWeWrote = SleepTimescale;
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
        /// <param name="rough">
        /// True when something woke the player rather than them sleeping it off, which shortens the
        /// audio transition so the sea and the rigging come back with them.
        /// </param>
        public static void Restore(bool rough = false)
        {
            // If a vanilla Recovery is running, it owns the sleep flags and the clock now. Recovery.cs:31
            // calls Sleep.FallAsleep and Recovery.cs:91-94 is the ONLY thing that puts Time.timeScale
            // back to 1 and clears eyesFullyClosed - clearing GameState.sleeping first makes it skip that
            // and leaves the game at 16x on a 0.2222 second step permanently. Release our bookkeeping and
            // write nothing. Recovery.cs:107-109 ends with its own unconditional MouseLook and
            // SetPlayerControl(true).
            bool recovering = false;
            try { recovering = GameState.recovering; } catch { }
            if (recovering)
            {
                // Logged once per recovery, not once per frame. DrunkRunner calls this every frame
                // while anything is still held, ControlTaken is deliberately left true below, and
                // BepInEx writes its log file synchronously - so the frame cost of one line per frame
                // lands on top of a recovery that is already teleporting the player and their boat.
                if (!standDownLogged)
                {
                    Plugin.Log.LogWarning(
                        "A recovery is running; standing down without touching the clock or the sleep flags.");
                    standDownLogged = true;
                }
                Plugin.TimeWarpHeld = false;
                Plugin.EyesFlagHeld = false;
                Plugin.SleepFlagHeld = false;
                // godMode is ours alone - Recovery never reads or writes it - and leaving it set would
                // stop needs ticking forever, so it is still restored.
                RestoreGodMode();
                // Control is left to Recovery. ControlTaken stays TRUE so the unstick net still covers us
                // if Recovery never reaches its own restore, which it does not when it cannot find a
                // boat (Recovery.cs:77-80 skips RecoverBoat, so the wait at Recovery.cs:103-106 never
                // ends). DrunkRunner gives that a long dwell and then frees the player anyway.
                return;
            }
            standDownLogged = false;

            if (Plugin.TimeWarpHeld)
            {
                // A parked clock is still ours. StartMenu.GameToSettings saves the live timeScale and
                // writes zero (StartMenu.cs:410-411), and SettingsToGame writes the saved value straight
                // back on close (StartMenu.cs:424) - and the value it saved is our 16x. Reading that zero
                // as somebody else's clock, writing nothing and clearing the hold is exactly how the
                // game ended up running at 16x for good with nothing left to undo it. So while the clock
                // is parked, keep the hold and write neither value: DrunkRunner calls this every frame
                // while anything is held, so the retry lands the moment the menu hands 16x back.
                // The clock alone decides this, not the menu flag. GameToSettings always zeroes the
                // clock, so the pause case is covered by the timeScale test on its own, and a cursor
                // menu that does NOT stop time (the map table, a shop) must not hold the warp open:
                // that would leave the world running at 16x for as long as the menu is up.
                bool parked = false;
                try { parked = Time.timeScale <= 0.01f; } catch { }

                if (parked)
                {
                    if (!clockDeferredLogged)
                    {
                        Plugin.Log.LogInfo(
                            "The clock is parked while the game is paused; holding the time warp until it runs again.");
                        clockDeferredLogged = true;
                    }
                }
                else
                {
                    clockDeferredLogged = false;
                    try
                    {
                        // Only put a value back if it is still the value we wrote. Someone else's clock
                        // is someone else's business, and stomping it is how the 16x leak happened.
                        if (Mathf.Abs(Time.timeScale - Plugin.TimeScaleWeWrote) < 0.5f)
                        {
                            Time.timeScale = (Plugin.TimeScaleWas > 0.01f && Plugin.TimeScaleWas < 2f)
                                ? Plugin.TimeScaleWas : 1f;
                        }
                        // timeScale first, then the step: a small fixed step under a 16x scale makes the
                        // engine try to catch up sixteen times the fixed steps in one frame.
                        if (Mathf.Abs(Time.fixedDeltaTime - Plugin.FixedStepWeWrote) < 0.01f)
                        {
                            Time.fixedDeltaTime = (Plugin.FixedStepWas > 0.001f && Plugin.FixedStepWas < 0.1f)
                                ? Plugin.FixedStepWas : InitialTimeStep;
                        }
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogError("Could not end the time warp cleanly: " + e.Message);
                        Time.timeScale = 1f;
                        Time.fixedDeltaTime = InitialTimeStep;
                    }
                    Plugin.TimeWarpHeld = false;
                }
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
                        AudioMixers.instance.gameActiveSnapshot.TransitionTo(rough ? 0.35f : 4f);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning("Could not restore the normal audio mix: " + e.Message);
                }
            }

            RestoreGodMode();

            if (Plugin.ControlTaken)
            {
                bool ok = SetControl(true);
                try { MouseLook.ToggleMouseLook(true); }
                catch (System.Exception e) { Plugin.Log.LogError("Could not re-enable mouse look: " + e.Message); }

                // Only forget that we took control if we actually gave it back. The old code cleared this
                // unconditionally after swallowing the exception, so a half-failed handback left the
                // player frozen with AnythingHeld() false and nothing, watchdog included, ever retrying.
                // DrunkRunner.Update retries every frame while this stays true. The retry itself is two
                // bool writes and a static bool (MouseLook.cs:112-115), so it is the LOG that has to be
                // held down to one line per episode rather than one per frame.
                Plugin.ControlTaken = !ok;
                if (!ok)
                {
                    if (!handbackFailedLogged)
                    {
                        Plugin.Log.LogError("Player control was not fully handed back; will retry every frame.");
                        handbackFailedLogged = true;
                    }
                }
                else handbackFailedLogged = false;
            }
        }

        /// <summary>
        /// Needs are ours alone for the length of a blackout, so this is the one piece of bookkeeping
        /// that is put back even on the paths where we hand the rest of the world to somebody else.
        /// Leaving godMode set stops hunger, thirst and rest ticking for the rest of the save.
        /// </summary>
        private static void RestoreGodMode()
        {
            if (!Plugin.GodModeHeld) return;
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

            float rest = VanillaSleepPerHour * hours * Drunkenness.SleepQualityNow;
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
        /// Coming round because something woke you, not because you slept it off. Twenty real seconds of
        /// blackout burned almost no alcohol, so you come back exactly as drunk as you went down, which
        /// is the point when the ship is going under and you have to fight for her. The stomach is still
        /// emptied, so drink that was in flight cannot put you straight back down.
        ///
        /// No HangoverWaterCost: that is the alcohol's own dehydration from a night's sleep, and you did
        /// not get one. The hours that did pass were already charged frame by frame in SleepItOff.
        /// </summary>
        private static void ApplyRoughWake(float hours)
        {
            Drunkenness.WakeRough();
            PlayerNeeds.alcohol = Mathf.Clamp(Drunkenness.Bac, 0f, 100f);
            Plugin.Log.LogInfo(
                $"Shaken awake after {hours:F1}h, bac {Drunkenness.Bac:F0}, rest {PlayerNeeds.sleep:F0}.");
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
        ///
        /// The generation stamp is not decoration. Both fades write the same OVRScreenFade on
        /// Camera.main that vanilla's Blackout.FadeTo writes (Blackout.cs:8), so whichever finishes last
        /// wins the screen. Once the generation has moved on this loop writes nothing more AND skips its
        /// closing SetFadeLevel, which is what keeps an orphaned fade to black from re-blacking a screen
        /// the wake-up fade has already cleared. ForceRecover bumps the generation before it clears the
        /// alpha, so that path is covered by this stamp alone.
        /// </summary>
        private static IEnumerator Fade(int gen, float target, float duration, float ease = 1f)
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
                if (gen != Plugin.BlackoutGeneration) yield break;
                elapsed += Time.unscaledDeltaTime;
                // ease > 1 holds the screen clear then drops it late, so a fall stays watchable.
                float k = Mathf.Pow(Mathf.Clamp01(elapsed / duration), ease);
                fade.SetFadeLevel(Mathf.Lerp(start, target, k));
                yield return null;
            }
            if (gen != Plugin.BlackoutGeneration) yield break;
            fade.SetFadeLevel(target);
        }

        /// <summary>
        /// Stops a fade this sequence started. The generation stamp inside Fade covers a blackout that
        /// was replaced or force-recovered, but an ORDERLY abort does not bump the generation - it
        /// breaks out of the fall loop and unwinds - so the handle is what covers that, which is the
        /// common case now that the guard can end a blackout mid-fall.
        /// </summary>
        private static void StopFade(MonoBehaviour runner, Coroutine fade)
        {
            if (runner == null || fade == null) return;
            try { runner.StopCoroutine(fade); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Could not stop the fade: " + e.Message); }
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

        /// <summary>
        /// Last resort, called by the runner's watchdog or when the guard has been firing and the
        /// coroutine has not acted on it.
        /// </summary>
        public static void ForceRecover(AbortReason reason = AbortReason.None)
        {
            Plugin.Log.LogError($"Blackout force-recover ({reason}) - putting everything back.");

            // Orphan any coroutine still alive. Every write it makes is generation-guarded, so it exits
            // at its next yield writing nothing. This is the direct kill for the worst leak in v0.2.0:
            // ForceRecover cleared TimeWarpHeld and zeroed WatchdogDeadline while the coroutine was still
            // running, and the reassert branch then re-forced 16x with nobody left to undo it.
            Plugin.BlackoutGeneration++;

            Restore(rough: true);
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
            // Deliberately NOT Drunkenness.SoberUp(). Wiping the blood alcohol on every failure path is
            // a free sober-up the player did not earn, and it makes real bug reports read as "sometimes
            // I wake up completely sober". Keep the drink, empty the stomach, arm the grace period.
            Drunkenness.WakeRough();

            // Twice in a row is not bad luck. If the cause is structural - a renamed vanilla field that
            // throws on JIT inside the coroutine, say - WakeRough leaves the player over the threshold,
            // so the grace period expires and they go straight back down: a black screen every couple of
            // minutes for the rest of the session. Parking the level just under the threshold as well
            // makes a broken blackout stop happening instead of looping, which is the right way for this
            // to fail. The drink itself is kept, so vanilla's awake rest drain is untouched.
            consecutiveForceRecovers++;
            if (consecutiveForceRecovers >= 2)
            {
                Drunkenness.HoldUnderThreshold();
                Plugin.Log.LogError(
                    $"That is {consecutiveForceRecovers} forced recoveries in a row, so something is " +
                    "wrong with the blackout itself. Holding you under the threshold so it cannot loop.");
            }
            Drunkenness.BlackedOut = false;
            BlackoutGuard.Disarm();
            abortReason = AbortReason.None;
            reported = AbortReason.None;
            Plugin.WatchdogDeadline = 0f;
        }
    }
}
