using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Why a blackout ended before it had been slept off. Public because ForceRecover and the runner's
    /// escalation both pass one across the mod, and an internal type on a public member does not compile.
    /// </summary>
    public enum AbortReason
    {
        None,
        NotPlaying,
        Loading,
        RecoveryRunning,
        Sunk,
        Flooding,
        LeftTheBoat,
        Swimming,
        ObjectDied,
        VanillaWoke,
        ClockTaken,
    }

    /// <summary>
    /// How wet the hull under the player is.
    ///
    /// BoatDamage lives on the PARENT of GameState.currentBoat, not on currentBoat itself. That is
    /// vanilla's own chain, used at LookUI.cs:486 and Mug.cs:205-208, so it is the one to follow.
    /// </summary>
    internal static class BoatWatch
    {
        private static Transform cachedBoat;
        private static BoatDamage cachedDamage;

        /// <summary>
        /// The damage model for the hull the player is standing on, or null when they are not on one.
        /// GameState.currentBoat is null ashore and while swimming, so this self-disables off a boat.
        /// GetComponent runs only when the player changes boat; the steady state is one Unity null
        /// check and one reference compare.
        /// </summary>
        internal static BoatDamage Current()
        {
            try
            {
                Transform boat = GameState.currentBoat;
                if (!(bool)boat) { cachedBoat = null; cachedDamage = null; return null; }

                // Keyed on the boat alone, so a miss is cached as well as a hit. Testing the cached
                // component instead would re-run both component searches every frame forever on a hull
                // that has no BoatDamage above it, since a cached null never looks like a hit. Vanilla
                // never destroys a live hull's BoatDamage - Recovery.cs:126-128 only writes its fields -
                // so the boat transform changing is the only thing that can invalidate this.
                if (boat != cachedBoat)
                {
                    cachedBoat = boat;
                    cachedDamage = (boat.parent != null) ? boat.parent.GetComponent<BoatDamage>() : null;
                    // Modded hulls may nest differently. Vanilla's chain first, then a walk up.
                    if (!(bool)cachedDamage) cachedDamage = boat.GetComponentInParent<BoatDamage>();
                }
                return cachedDamage;
            }
            catch { return null; }
        }

        /// <summary>The wake line, clamped in code whatever the config file says.</summary>
        internal static float Level()
        {
            return Mathf.Clamp(Plugin.FloodWakeLevel.Value, 0.02f, 0.90f);
        }

        /// <summary>
        /// True when the hull under the player is past the wake line or already gone. Used by the entry
        /// gate and the time-warp gate. Anything unexpected reads as "not sinking", which is v0.2.0
        /// behavior, so a bad read can never wake you early or refuse a blackout wrongly.
        /// </summary>
        internal static bool FloodingOrSunk()
        {
            try
            {
                var d = Current();
                if (!(bool)d) return false;
                if (d.sunk) return true;
                if (!Plugin.WakeWhenFlooding.Value) return false;
                return d.waterLevel >= Level();
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// A blackout is a lease on the world it was taken in. This re-checks that world every frame and
    /// says, in one value, whether the lease still holds.
    ///
    /// Every test is a plain field read that is correct within a frame, so nothing here depends on
    /// running before or after any vanilla Update. Everything fails toward "keep going", because a guard
    /// that guesses wrong in the other direction cuts a blackout short for no reason, and a guard that
    /// throws must never be the thing that leaves a player frozen.
    /// </summary>
    internal static class BlackoutGuard
    {
        private static bool armed;
        private static bool startedOnBoat;
        private static Transform startBoat;
        private static Transform startControllerParent;
        private static Transform startObserverParent;

        /// <summary>Snapshot the world at the top of Run(), before anything is taken.</summary>
        internal static void Arm()
        {
            try
            {
                startBoat = GameState.currentBoat;
                startedOnBoat = (bool)startBoat;
                var cc = Refs.charController;
                startControllerParent = (bool)cc ? cc.transform.parent : null;
                var mirror = Refs.observerMirror;
                startObserverParent = (bool)mirror ? mirror.transform.parent : null;
                armed = true;
            }
            catch
            {
                // A snapshot we could not take is a guard we do not arm. v0.2.0 behavior, not a throw.
                armed = false;
            }
        }

        internal static void Disarm()
        {
            armed = false;
            startedOnBoat = false;
            startBoat = null;
            startControllerParent = null;
            startObserverParent = null;
        }

        /// <summary>
        /// First reason the blackout should end, or None. Every value read is a plain field that is
        /// correct within a frame, so ordering against vanilla's LateUpdate does not matter.
        /// </summary>
        internal static AbortReason Check()
        {
            if (!armed) return AbortReason.None;
            try
            {
                if (!GameState.playing) return AbortReason.NotPlaying;

                // GameState.currentlyLoading is the real load path (StartMenu.cs:599 and :619), and
                // changingStartRegion swaps the world out from under everything.
                //
                // GameState.loadingScenes is deliberately NOT here. It is not a scene change, it is the
                // ordinary island streaming counter: IslandHorizon.LoadIslandScene increments it every
                // time the player comes within islandLoadDistance, 1800m (IslandHorizon.cs:15, :85,
                // :104), and RegisterLoadingFinished decrements it once the additive load completes
                // (IslandHorizon.cs:113-114). Nothing else in the game writes it. Under a 16x warp the
                // boat keeps sailing, so crossing that radius is routine, and ending a blackout for it
                // would break the mechanic anywhere within a few miles of land.
                if (GameState.currentlyLoading || GameState.changingStartRegion) return AbortReason.Loading;

                // Someone else owns the sleep state now. Recovery.cs:31 calls Sleep.FallAsleep and
                // Recovery.cs:91-94 is the ONLY thing that puts the clock back, so we stand down and
                // write none of the four globals rather than strand it.
                if (GameState.recovering) return AbortReason.RecoveryRunning;

                var d = BoatWatch.Current();
                if ((bool)d)
                {
                    // Never configurable. Once sunk latches (BoatDamage.cs:248-255) the hull capsule is
                    // off, the embark trigger has jumped 100m and the cargo is already written off.
                    if (d.sunk) return AbortReason.Sunk;
                    if (Plugin.WakeWhenFlooding.Value && d.waterLevel >= BoatWatch.Level())
                        return AbortReason.Flooding;
                }

                var cc = Refs.charController;
                if (!(bool)cc || !(bool)Refs.ovrController || Camera.main == null)
                    return AbortReason.ObjectDied;

                // Needs no cached object at all, so it fires even when every reference we hold is dead.
                if (PlayerSwimming.swimming || PlayerSwimming.observerSwimming)
                    return AbortReason.Swimming;

                if (startedOnBoat)
                {
                    Transform now = GameState.currentBoat;
                    if (!(bool)now || now != startBoat) return AbortReason.LeftTheBoat;
                }

                // Null FIRST, identity second: Unity's == treats a destroyed object as null, so two
                // references to the same destroyed transform compare equal and identity alone misses it.
                if (startControllerParent != null)
                {
                    if (!(bool)startControllerParent || cc.transform.parent != startControllerParent)
                        return AbortReason.LeftTheBoat;
                }
                if (startObserverParent != null)
                {
                    var mirror = Refs.observerMirror;
                    Transform obs = (bool)mirror ? mirror.transform.parent : null;
                    if (!(bool)startObserverParent || obs != startObserverParent)
                        return AbortReason.LeftTheBoat;
                }

                // Vanilla woke us out from under the sequence (Sleep.cs:189-217 clears sleeping,
                // eyesFullyClosed, resets timeScale and calls SetPlayerControl(true)). End, do not fight.
                if (Plugin.SleepFlagHeld && !GameState.sleeping) return AbortReason.VanillaWoke;

                // Someone took the clock back. One strike, not three. The > 0.01 and !inCursorMenu terms
                // exist because StartMenu.GameToSettings (StartMenu.cs:402-418) parks timeScale at 0 on
                // pause and faithfully restores 16x on close.
                if (Plugin.TimeWarpHeld && !GameState.inCursorMenu
                    && Time.timeScale > 0.01f && Time.timeScale < 15.5f) return AbortReason.ClockTaken;

                return AbortReason.None;
            }
            catch
            {
                // A guard that throws must not end a blackout on its own. If the throw is structural
                // (a renamed field, so it throws on JIT outside this catch) it lands in the coroutine,
                // which is exactly what DrunkRunner's watchdog exists for.
                return AbortReason.None;
            }
        }

        /// <summary>
        /// True while the game is sitting in the pause menu with its clock parked.
        ///
        /// Both terms together, because that pair is the pause menu's own signature: GameToSettings sets
        /// GameState.inCursorMenu and writes Time.timeScale = 0 (StartMenu.cs:410-416), and SettingsToGame
        /// puts the saved timeScale back (StartMenu.cs:424). Requiring both means a flag left set by some
        /// other mod can never on its own switch off a timeout that exists to free the player - the
        /// clock has to actually be stopped, and a stopped clock is not a state anyone can be stuck in.
        /// </summary>
        internal static bool GamePaused()
        {
            try { return GameState.inCursorMenu && Time.timeScale <= 0.01f; }
            catch { return false; }
        }

        /// <summary>
        /// Entry gate. Vanilla refuses to put you in a bunk on a doomed hull (GPButtonBed.cs:21,
        /// "(!damage || !damage.sunk)"), and this is the same precedent one notch earlier.
        /// </summary>
        internal static bool RefuseToStart()
        {
            try
            {
                if (BoatWatch.FloodingOrSunk()) return true;
                if (PlayerSwimming.swimming) return true;
                if (GameState.onRatlines) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
