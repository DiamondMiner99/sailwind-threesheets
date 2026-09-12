using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Collapsing to the deck.
    ///
    /// Sailwind has no local player body - the only avatar classes in the game are the Oculus VR SDK's,
    /// and Refs has no body reference - so there is nothing to ragdoll and nothing to watch fall. What
    /// there is instead is the view: it tips, the deck rushes up, you land hard and settle. Read from
    /// the inside, going down like that is most of what a ragdoll would have sold anyway.
    ///
    /// Runs in LateUpdate so it wins against anything writing the camera in Update. Both of the usual
    /// writers are already quiet during a blackout - OVRPlayerController is disabled by
    /// Refs.SetPlayerControl and MouseLook early-returns once mouse look is toggled off - so this is
    /// belt and braces rather than a fight.
    ///
    /// Everything is written as a LOCAL offset, so the fall stays attached to whatever the camera is
    /// parented to. Collapse on the deck of a moving ship and you go down with the ship, not through it.
    /// </summary>
    public class CollapseAnimator : MonoBehaviour
    {
        private Transform cam;
        private Vector3 restPosition;
        private Quaternion restRotation;

        private float elapsed;
        private float duration = 1.6f;
        private bool running;
        private bool held;

        // Which way you go down, picked per blackout so it is not the same fall every time.
        private float side = 1f;
        private float yawLurch;

        public bool Running => running;

        /// <summary>Fraction of the fall completed. Reaches 1 when the body has settled.</summary>
        public float Progress => (duration <= 0f) ? 1f : Mathf.Clamp01(elapsed / duration);

        public bool Begin(float seconds)
        {
            Stop();

            var mainCam = Camera.main;
            if (mainCam == null)
            {
                Plugin.Log.LogWarning("No main camera, skipping the collapse.");
                return false;
            }

            cam = mainCam.transform;
            restPosition = cam.localPosition;
            restRotation = cam.localRotation;
            held = true;

            duration = Mathf.Max(0.3f, seconds);
            elapsed = 0f;
            side = (Random.value < 0.5f) ? -1f : 1f;
            yawLurch = Random.Range(8f, 22f) * side;
            running = true;
            return true;
        }

        /// <summary>Puts the camera back where it was. Called while the screen is black, so the snap is unseen.</summary>
        public void Stop()
        {
            running = false;
            if (held && cam != null)
            {
                cam.localPosition = restPosition;
                cam.localRotation = restRotation;
            }
            held = false;
            cam = null;
        }

        private void LateUpdate()
        {
            if (!running) return;
            if (cam == null)
            {
                // Scene change pulled the camera out from under us. Nothing to restore to.
                running = false;
                held = false;
                return;
            }

            // Unscaled, so the fall plays at a watchable speed even though the time warp starts at 16x.
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            // Legs go at 70% of the way through; the rest is the settle after hitting the deck.
            const float impactAt = 0.7f;
            float fall = Mathf.Clamp01(t / impactAt);
            // Squared, so it accelerates into the deck instead of drifting down at a constant rate.
            float drop = fall * fall;

            float settle = 0f;
            if (t > impactAt)
            {
                // One damped bounce, over quickly. A long wobble reads as floating, not as landing.
                float after = (t - impactAt) / (1f - impactAt);
                settle = Mathf.Sin(after * Mathf.PI * 1.6f) * Mathf.Exp(-after * 5f);
            }

            float height = Plugin.CollapseDrop.Value;
            var offset = new Vector3(
                side * 0.32f * drop,
                -height * drop + settle * 0.06f,
                0.12f * drop);

            // Roll is what sells it. You do not sink to the floor upright, you go over sideways.
            float roll = side * Plugin.CollapseRoll.Value * drop + settle * 9f * side;
            float pitch = 16f * drop - settle * 6f;
            float yaw = yawLurch * drop;

            cam.localPosition = restPosition + offset;
            cam.localRotation = restRotation * Quaternion.Euler(pitch, yaw, roll);

            if (t >= 1f) running = false;
        }

        private void OnDestroy()
        {
            Stop();
        }
    }
}
