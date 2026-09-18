using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Drives the model and the effects, and owns the blackout coroutine.
    ///
    /// Everything here is client-side. Nothing is sent, nothing host-authoritative is touched, and no
    /// boat or player is moved, so this runs in a co-op session without a wire change or a parity gate.
    /// If two people are drinking together they each get their own hangover.
    /// </summary>
    public class DrunkRunner : MonoBehaviour
    {
        private readonly DrunkEffects effects = new DrunkEffects();
        private PlayerAlcohol playerAlcohol;
        private float rescanTimer;
        private bool moveScaleDirty;
        private float frozenFor;
        private float recoveringFor;
        private bool unstuckLogged;

        /// <summary>
        /// How long a vanilla Recovery is allowed to hold control before the unstick net stops treating
        /// it as a legitimate owner. Recovery.DoRecoverPlayer is about 6 real seconds of waits plus the
        /// boat coroutine, so a minute is well past a healthy one and only a hung one reaches it.
        /// </summary>
        private const float RecoveryPatienceSeconds = 60f;

        private void Update()
        {
            if (!Plugin.Enabled.Value)
            {
                // Turning the mod off mid-blackout must not leave the player frozen at 16x. Only a
                // LIVE blackout needs the forced recovery, though: once that has run, anything still
                // held is leftover bookkeeping (a clock parked by the pause menu, say) and the ordinary
                // restore retries it quietly, instead of writing a force-recover error every frame for
                // as long as the mod stays switched off.
                if (Drunkenness.BlackedOut) BlackoutSequence.ForceRecover();
                else if (AnythingHeld()) BlackoutSequence.Restore();
                if (moveScaleDirty) RestoreMoveScale();
                effects.Restore();
                return;
            }

            UnstickPlayer();

            // The blackout watchdog. If the sequence throws, or a scene load kills the coroutine
            // mid-fade, the player would otherwise be left frozen behind a black screen with no way
            // out. Fail open, always.
            //
            // It runs on unscaled time so a defeated warp cannot hide from it, and unscaled time keeps
            // running while the pause menu holds Time.timeScale at zero. So the budget is not spent on a
            // paused game: nothing is advancing, nothing is overdue, and a player who walked away from
            // the menu should not come back to a blackout force-recovered behind a parked clock.
            if (Plugin.WatchdogDeadline > 0f)
            {
                if (BlackoutGuard.GamePaused()) Plugin.WatchdogDeadline += Time.unscaledDeltaTime;
                else if (Time.unscaledTime > Plugin.WatchdogDeadline) BlackoutSequence.ForceRecover();
            }

            // The guard runs from here, not only from inside the coroutine, so a dead coroutine is
            // still caught. The coroutine gets first refusal - it unwinds in the right order, with the
            // fade - and only if it has not acted within half a second does the watchdog force it.
            if (Drunkenness.BlackedOut || AnythingHeld())
            {
                var reason = BlackoutGuard.Check();
                BlackoutSequence.ReportAbort(reason);
                if (BlackoutSequence.AbortOverdue)
                {
                    BlackoutSequence.ForceRecover(BlackoutSequence.ReportedReason);
                }
            }

            if (Drunkenness.BlackedOut) return;

            // Safety net for a sequence that ended without unwinding - a scene load killing the
            // coroutine mid-warp, say. The watchdog covers the stalled case; this covers the vanished
            // one, where nothing is left running to trip the deadline.
            if (AnythingHeld()) BlackoutSequence.Restore();

            Drunkenness.TickGrace(Time.unscaledDeltaTime);

            if (!Plugin.PlayerIsInControl())
            {
                // Keep sobering up through a sleep or a menu, but leave the screen alone. Scaled dt, so
                // a 16x sleep clears it at 16x the way vanilla does. A paused menu has dt 0, which is
                // also correct - no time passes, so nothing wears off. Asleep in bed sobers faster,
                // the same multiplier a blackout uses, so a night actually clears a binge.
                Drunkenness.Tick(Time.deltaTime, GameState.sleeping ? Plugin.SoberingAsleep.Value : 1f);
                effects.Restore();
                if (moveScaleDirty) RestoreMoveScale();
                return;
            }

            Drunkenness.Tick(Time.deltaTime);

            FindPostProcessing();
            effects.Apply(Drunkenness.Intensity, Drunkenness.Peril);
            ApplyMoveWobble(Drunkenness.Intensity);

            // Not while the game is paused. The fall and both fades run on unscaled time, so a
            // blackout started here would play out behind the pause menu against a stopped world.
            if (BlackoutGuard.GamePaused()) return;

            if (ForcedBlackoutPressed())
            {
                Plugin.Log.LogWarning("Testing: blackout forced by BlackoutKey.");
                StartCoroutine(BlackoutSequence.Run(this));
            }
            else if (Drunkenness.ShouldBlackOut)
            {
                StartCoroutine(BlackoutSequence.Run(this));
            }
        }

        /// <summary>
        /// PlayerAlcohol holds the live PostProcessingProfile, which saves hunting for the behaviour
        /// component. It is destroyed and rebuilt across scene loads, so recheck periodically rather
        /// than caching it once and holding a dead reference.
        /// </summary>
        private void FindPostProcessing()
        {
            rescanTimer -= Time.deltaTime;
            if (playerAlcohol != null && rescanTimer > 0f) return;
            rescanTimer = 5f;

            if (playerAlcohol == null)
            {
                playerAlcohol = FindObjectOfType<PlayerAlcohol>();
                if (playerAlcohol == null) return;
            }
            effects.Attach(playerAlcohol.postProcessing);
        }

        private void ApplyMoveWobble(float t)
        {
            if (!Plugin.MoveWobbleEnabled.Value)
            {
                if (moveScaleDirty) RestoreMoveScale();
                return;
            }
            if (Refs.ovrController == null) return;

            if (t <= 0f)
            {
                if (moveScaleDirty) RestoreMoveScale();
                return;
            }

            // MoveScaleMultiplier feeds the same acceleration term as Acceleration and Damping, but
            // nothing in the game writes it. Acceleration and Damping are NOT safe to use here:
            // PlayerClimb overwrites both every frame on ratlines and restores its own sober values,
            // so anything written there disappears the first time you go up the shrouds.
            Refs.ovrController.SetMoveScaleMultiplier(1f + Plugin.MoveWobbleAmount.Value * t);
            moveScaleDirty = true;
        }

        private void RestoreMoveScale()
        {
            if (Refs.ovrController != null) Refs.ovrController.SetMoveScaleMultiplier(1f);
            moveScaleDirty = false;
        }

        /// <summary>
        /// Last resort for control THIS MOD took and did not manage to hand back.
        ///
        /// It deliberately does not infer that from a disabled CharacterController, because a bare
        /// disabled controller is not evidence of anything. GoPointerButton.StickyClick
        /// (GoPointerButton.cs:117-121) calls Refs.SetPlayerControl(false) and sets no GameState flag at
        /// all - MouseLook.ToggleMouseLook is one static bool (MouseLook.cs:112-115); only
        /// ToggleMouseLookAndCursor writes inCursorMenu (MouseLook.cs:117-120) - and that is the path the
        /// helm, every rope winch and the bilge pump take, held for as long as the player is steering or
        /// pumping. Sailwind Co-op's guest faint does the same for about four and a half seconds. Both
        /// would read as "unexplained" to an elimination test, and switching movement back on under
        /// either is a brand new way to walk someone off their own deck while drunk.
        ///
        /// So the net fires only while Plugin.ControlTaken says the hold is ours: this mod took control
        /// and still believes it owes it back. Nothing else can be caught by it.
        /// </summary>
        private void UnstickPlayer()
        {
            if (!Plugin.UnstickPlayer.Value || !Plugin.ControlTaken)
            {
                frozenFor = 0f;
                recoveringFor = 0f;
                unstuckLogged = false;
                return;
            }

            bool suspect;
            try
            {
                // Recovery hands control back itself (Recovery.cs:107-108), so stay out of its way -
                // but not forever. Recovery.DoRecoverPlayer waits on `while (!boatRecovered)`
                // (Recovery.cs:103-106), and when GameState.lastOwnedBoat and FindClosestBoat() both
                // come back null it never starts RecoverBoat at all (Recovery.cs:77-80), so that wait
                // never ends, GameState.recovering never clears and its SetPlayerControl(true) is never
                // reached. A healthy recovery is over in well under a minute, so after this dwell the
                // player gets their movement back regardless of who is supposedly holding it.
                bool recovering = GameState.recovering;
                recoveringFor = recovering ? recoveringFor + Time.unscaledDeltaTime : 0f;

                suspect = GameState.playing
                    && !Drunkenness.BlackedOut
                    && !GameState.sleeping
                    && (!recovering || recoveringFor > RecoveryPatienceSeconds)
                    && !GameState.currentlyLoading
                    && GameState.inBed == null
                    && (bool)Refs.charController
                    && !Refs.charController.enabled;
            }
            catch { frozenFor = 0f; return; }

            if (!suspect) { frozenFor = 0f; unstuckLogged = false; return; }

            frozenFor += Time.unscaledDeltaTime;
            if (frozenFor < Plugin.UnstickAfterSeconds.Value) return;

            if (!unstuckLogged)
            {
                Plugin.Log.LogError(
                    $"Movement has been off for {frozenFor:F1}s and this mod still owes it back. " +
                    "Switching it back on.");
                unstuckLogged = true;
            }
            try
            {
                if ((bool)Refs.ovrController) Refs.ovrController.enabled = true;
                Refs.charController.enabled = true;
                MouseLook.ToggleMouseLook(true);
            }
            catch (System.Exception e) { Plugin.Log.LogError("Unstick failed: " + e.Message); }
            frozenFor = 0f;
        }

        /// <summary>
        /// Testing key. Only reachable when the player is on their feet, since the checks above return
        /// first during menus, sleep, the shipyard or an existing blackout, so it cannot stack a second
        /// blackout on the first.
        /// </summary>
        private static bool ForcedBlackoutPressed()
        {
            try
            {
                return Plugin.BlackoutKey != null && Plugin.BlackoutKey.Value.IsDown();
            }
            catch
            {
                return false;
            }
        }

        private static bool AnythingHeld()
        {
            return Plugin.TimeWarpHeld || Plugin.ControlTaken || Plugin.GodModeHeld
                || Plugin.SleepFlagHeld || Plugin.EyesFlagHeld;
        }

        private void OnDestroy()
        {
            if (AnythingHeld()) BlackoutSequence.Restore();
            effects.Restore();
            if (moveScaleDirty) RestoreMoveScale();
        }
    }
}
