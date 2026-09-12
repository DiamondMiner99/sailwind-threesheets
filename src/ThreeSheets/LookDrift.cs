using HarmonyLib;
using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Slow wander on the look direction, so aiming becomes something you have to keep correcting
    /// instead of something you set and forget. Noise is zero-mean, so it wanders and comes back
    /// rather than spinning you.
    ///
    /// This also gets the stumble for free: walking is camera-relative, so a yaw that drifts under you
    /// makes you walk a wavy line across the deck without touching the movement code at all.
    ///
    /// Sailwind runs two MouseLook components and they write rotation in two different ways, which
    /// decides how the drift has to be applied to each:
    ///   MouseX          - transform.Rotate, relative. Apply the frame-to-frame DELTA, or it compounds
    ///                     and the drift never comes back.
    ///   MouseY/XAndY    - writes localEulerAngles absolutely from its own accumulator. Apply the
    ///                     ABSOLUTE offset; vanilla overwrites the base every frame so nothing builds up.
    /// </summary>
    [HarmonyPatch(typeof(MouseLook), "Update")]
    public static class LookDrift
    {
        private static float lastYaw;

        // Sailwind has TWO active MouseX components, one on the physics body ("OVRPlayerController
        // (controller)") and one on the visual body ("OVRPlayerController (observer)") that the camera
        // hangs from, and mouse input turns both by the same amount every frame. The yaw step is
        // therefore worked out once per frame and handed to every MouseX instance. The first version
        // kept one running lastYaw, so whichever instance ran first consumed the whole step and the
        // other got zero; that was the physics body, and PlayerControllerMirror then copies the visual
        // body's rotation back over it, so no side-to-side drift ever reached the screen.
        private static int yawFrame = -1;
        private static float yawStep;

        public static void ResetAccumulator()
        {
            lastYaw = 0f;
            yawFrame = -1;
            yawStep = 0f;
        }

        [HarmonyPostfix]
        public static void Postfix(MouseLook __instance)
        {
            if (!Plugin.Enabled.Value || !Plugin.LookDriftEnabled.Value) return;

            float t = Drunkenness.Intensity;
            if (t <= 0f)
            {
                lastYaw = 0f;
                return;
            }

            // Mirror vanilla's own gate. Without this the view drifts under menus, the shipyard camera
            // and the sleep fade, where the player has no way to correct it.
            if (!Plugin.PlayerIsInControl()) return;
            if (!MouseLook.MouseLookIsEnabled() || GameState.inCursorMenu) return;

            float amp = Plugin.LookDriftDegrees.Value * t;
            float speed = Plugin.LookDriftSpeed.Value;
            float time = Time.unscaledTime * speed;

            // Two uncorrelated noise streams, sampled well apart so yaw and pitch do not move together.
            float yaw = (Mathf.PerlinNoise(time, 0f) - 0.5f) * 2f * amp;
            float pitch = (Mathf.PerlinNoise(0f, time * 0.83f + 137f) - 0.5f) * 2f * amp * 0.6f;

            if (__instance.axes == MouseLook.RotationAxes.MouseX)
            {
                if (Time.frameCount != yawFrame)
                {
                    yawFrame = Time.frameCount;
                    yawStep = yaw - lastYaw;
                    lastYaw = yaw;
                }
                __instance.transform.Rotate(0f, yawStep, 0f);
            }
            else
            {
                var angles = __instance.transform.localEulerAngles;
                __instance.transform.localEulerAngles = new Vector3(angles.x + pitch, angles.y, angles.z);
            }
        }
    }
}
