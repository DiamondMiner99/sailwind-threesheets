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

        private void Update()
        {
            if (!Plugin.Enabled.Value)
            {
                // Turning the mod off mid-blackout must not leave the player frozen at 16x.
                if (AnythingHeld()) BlackoutSequence.ForceRecover();
                if (moveScaleDirty) RestoreMoveScale();
                effects.Restore();
                return;
            }

            // The blackout watchdog. If the sequence throws, or a scene load kills the coroutine
            // mid-fade, the player would otherwise be left frozen behind a black screen with no way
            // out. Fail open, always.
            if (Plugin.WatchdogDeadline > 0f && Time.unscaledTime > Plugin.WatchdogDeadline)
            {
                BlackoutSequence.ForceRecover();
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
