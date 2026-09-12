using System.Collections;
using System.Text;
using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// The horizon rocking, the lean, and the view breathing in and out.
    ///
    /// A true wavy screen, the rippling warp from other games, is not possible here. Sailwind runs Post
    /// Processing Stack v1, which has no distortion effect, and adding one means a custom shader, which
    /// has to be compiled by the Unity editor and shipped in an asset bundle. So this does the part that
    /// needs no shader: roll and field of view.
    ///
    /// All of it is applied at render time only: on in Application.onBeforeRender, which fires after
    /// every Update and LateUpdate and before the first camera draws, and off again at the end of the
    /// frame once every camera has drawn. Nothing in the game ever sees it - not aiming, not item
    /// placement, not the spyglass or the settings slider that also write the FOV - and nothing in the
    /// game can level it out.
    ///
    /// The roll is applied to the main camera's view MATRIX, not to its transform. Rotating the
    /// transform rolled everything parented under CenterEyeAnchor, which includes the UI camera and the
    /// outline cameras, so the HUD and the menus tilted along with the world. Overriding
    /// worldToCameraMatrix tilts only what that one camera draws and leaves every other camera to work
    /// out its own view as usual, so the world leans and the interface stays level.
    /// </summary>
    public class ScreenWobble : MonoBehaviour
    {
        /// <summary>Current rock from the wobble, in degrees.</summary>
        internal static float Roll;
        /// <summary>Current lean from the drift, in degrees.</summary>
        internal static float Lean;
        /// <summary>Current field-of-view breathing, in degrees.</summary>
        internal static float FovOffset;

        private Camera mainCam;

        private Camera rolledCam;
        private bool rollPending;

        private Camera fovCam;
        private float savedFov;
        private float appliedFov;
        private bool fovPending;

        private bool loggedChain;

        /// <summary>The wobble is on when the player is on their feet and not looking through the orbit camera.</summary>
        internal static bool Active(out float strength)
        {
            strength = 0f;
            if (!Plugin.Enabled.Value || !Plugin.WobbleEnabled.Value) return false;
            if (!Plugin.PlayerIsInControl()) return false;
            if (BoatCamera.on) return false;
            if (InBlockingMenu()) return false;
            strength = Drunkenness.Intensity;
            return strength > 0f;
        }

        /// <summary>
        /// The drift's lean: its own noise stream, but the drift's size and speed, so it wanders with the
        /// up-and-down drift without being locked to it (a lean that nods in step with the pitch reads as
        /// mechanical).
        /// </summary>
        internal static bool LeanActive(out float strength)
        {
            strength = 0f;
            if (!Plugin.Enabled.Value || !Plugin.LookDriftEnabled.Value) return false;
            if (Plugin.LookDriftLean.Value <= 0f) return false;
            if (!Plugin.PlayerIsInControl()) return false;
            if (BoatCamera.on) return false;
            if (InBlockingMenu()) return false;
            strength = Drunkenness.Intensity;
            return strength > 0f;
        }

        /// <summary>
        /// A menu is up and it is not the tuning window. Swaying the world under a paused menu makes the
        /// menu look like it is sliding around, so everything stands down. ConfigurationManager is the
        /// exception: its whole point here is watching these numbers change while you drag them.
        /// </summary>
        private static bool InBlockingMenu()
        {
            return GameState.inCursorMenu && !ConfigMenuCursor.MenuOpen;
        }

        private Coroutine endOfFrame;

        private void OnEnable()
        {
            Application.onBeforeRender += OnBeforeRender;
            endOfFrame = StartCoroutine(EndOfFrameLoop());
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OnBeforeRender;
            if (endOfFrame != null) StopCoroutine(endOfFrame);
            Unapply();
        }

        /// <summary>WaitForEndOfFrame resumes after every camera has drawn, which is when it comes off.</summary>
        private IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                Unapply();
            }
        }

        private void LateUpdate()
        {
            // Anything still applied from a frame the camera did not render. Cheap, and it means a
            // skipped frame can never strand a tilt or a zoom on the real transform.
            Unapply();

            mainCam = Camera.main;

            float lt;
            if (LeanActive(out lt))
            {
                float driftTime = Time.unscaledTime * Plugin.LookDriftSpeed.Value;
                float noise = (Mathf.PerlinNoise(driftTime * 0.91f + 311f, 57f) - 0.5f) * 2f;
                // Same peak LookDrift uses for pitch (60% of LookDriftDegrees), times the lean share.
                Lean = noise * Plugin.LookDriftDegrees.Value * 0.6f * Plugin.LookDriftLean.Value * lt;
            }
            else
            {
                Lean = 0f;
            }

            float t;
            bool on = Active(out t);
            // Two incommensurate rates layered, so it wanders rather than ticking like a metronome.
            float time = Time.unscaledTime * Mathf.Max(0.01f, Plugin.WobbleSpeed.Value) * Mathf.PI * 2f;
            Roll = on
                ? (Mathf.Sin(time) * 0.7f + Mathf.Sin(time * 0.37f + 1.9f) * 0.3f) * Plugin.WobbleRoll.Value * t
                : 0f;
            FovOffset = on
                ? (Mathf.Sin(time * 0.73f + 1.3f) * 0.75f + Mathf.Sin(time * 0.29f) * 0.25f) * Plugin.WobbleFov.Value * t
                : 0f;

            if (!loggedChain && mainCam != null && (Mathf.Abs(Lean) > 0.01f || Mathf.Abs(Roll) > 0.01f))
            {
                loggedChain = true;
                LogCameraChain(mainCam);
            }
        }

        private void OnBeforeRender()
        {
            var c = mainCam;
            if (c == null) return;
            Unapply();

            float roll = Roll + Lean;
            if (Mathf.Abs(roll) > 0.001f)
            {
                rolledCam = c;
                // View space is what the camera sees, so rolling it about its own z tilts the picture
                // without moving the camera, without touching anything parented to it, and without
                // changing where the camera actually points.
                c.worldToCameraMatrix = Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, -roll)) * c.worldToCameraMatrix;
                rollPending = true;
            }

            if (Mathf.Abs(FovOffset) > 0.001f)
            {
                fovCam = c;
                savedFov = c.fieldOfView;
                appliedFov = Mathf.Clamp(savedFov + FovOffset, 5f, 150f);
                c.fieldOfView = appliedFov;
                fovPending = true;
            }
        }

        /// <summary>Takes back exactly what was applied, and only if nothing has changed it since.</summary>
        private void Unapply()
        {
            if (rollPending)
            {
                // Back to the matrix Unity works out from the transform on its own.
                if (rolledCam != null) rolledCam.ResetWorldToCameraMatrix();
                rollPending = false;
            }
            if (fovPending)
            {
                if (fovCam != null && Mathf.Approximately(fovCam.fieldOfView, appliedFov))
                    fovCam.fieldOfView = savedFov;
                fovPending = false;
            }
        }

        /// <summary>
        /// One line in the log describing where the camera sits and which of its parents carry a
        /// MouseLook. The first version of the lean never reached the screen and reading the code did
        /// not say why; if the view ever misbehaves again this is the fact needed to explain it.
        /// </summary>
        private static void LogCameraChain(Camera c)
        {
            try
            {
                var sb = new StringBuilder("Camera chain (camera first): ");
                for (Transform t = c.transform; t != null; t = t.parent)
                {
                    sb.Append(t.name);
                    var look = t.GetComponent<MouseLook>();
                    if (look != null) sb.Append("[MouseLook ").Append(look.axes).Append(look.enabled ? "" : " off").Append(']');
                    if (t.parent != null) sb.Append(" < ");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Could not describe the camera chain: " + e.Message);
            }
        }
    }
}
