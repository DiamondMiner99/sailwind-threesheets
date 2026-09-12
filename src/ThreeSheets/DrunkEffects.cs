using UnityEngine;
using UnityEngine.PostProcessing;

namespace ThreeSheets
{
    /// <summary>
    /// The visual side.
    ///
    /// Vanilla PlayerAlcohol already ramps bloom and post-exposure off PlayerNeeds.alcohol every frame,
    /// so this deliberately does not touch those two models - it runs after PlayerAlcohol and adds the
    /// models it never used. The profile is a live ScriptableObject shared with the rest of the game, so
    /// every model touched here is snapshotted on first write and put back when the player sobers up.
    ///
    /// Depth of field is doing the interesting work. The focus plane sits out at sailing distance and only
    /// the aperture moves, so the horizon stays readable while anything in your hands - compass, sextant,
    /// chart, clock - goes soft. Drunk in Sailwind should mean losing your position, not tripping over.
    /// </summary>
    public class DrunkEffects
    {
        private PostProcessingProfile profile;
        private bool captured;
        private bool applied;

        private DepthOfFieldModel.Settings dofOriginal;
        private ChromaticAberrationModel.Settings caOriginal;
        private VignetteModel.Settings vignetteOriginal;
        private GrainModel.Settings grainOriginal;
        private bool dofWasEnabled, caWasEnabled, vignetteWasEnabled, grainWasEnabled;

        public void Attach(PostProcessingProfile p)
        {
            if (p == null || ReferenceEquals(p, profile)) return;
            // Different profile than the one we snapshotted: let go of the old one cleanly first.
            if (captured) Restore();
            profile = p;
            captured = false;
            applied = false;
        }

        private void Capture()
        {
            if (captured || profile == null) return;
            dofOriginal = profile.depthOfField.settings;
            caOriginal = profile.chromaticAberration.settings;
            vignetteOriginal = profile.vignette.settings;
            grainOriginal = profile.grain.settings;
            dofWasEnabled = profile.depthOfField.enabled;
            caWasEnabled = profile.chromaticAberration.enabled;
            vignetteWasEnabled = profile.vignette.enabled;
            grainWasEnabled = profile.grain.enabled;
            captured = true;
            Plugin.Log.LogInfo("Captured baseline post-processing settings.");
        }

        public void Restore()
        {
            if (!captured || profile == null)
            {
                applied = false;
                return;
            }
            // Nothing of ours is on the profile, so do not rewrite four models every frame while sober.
            if (!applied) return;
            profile.depthOfField.settings = dofOriginal;
            profile.chromaticAberration.settings = caOriginal;
            profile.vignette.settings = vignetteOriginal;
            profile.grain.settings = grainOriginal;
            profile.depthOfField.enabled = dofWasEnabled;
            profile.chromaticAberration.enabled = caWasEnabled;
            profile.vignette.enabled = vignetteWasEnabled;
            profile.grain.enabled = grainWasEnabled;
            applied = false;
        }

        public void Apply(float intensity, float peril)
        {
            if (profile == null) return;

            if (intensity <= 0f && peril <= 0f)
            {
                if (applied) Restore();
                return;
            }

            Capture();
            applied = true;

            float t = Mathf.Clamp01(intensity);

            if (Plugin.BlurEnabled.Value)
            {
                var dof = profile.depthOfField.settings;
                // Focus hunts a little, the way your eyes do when you cannot hold a fixation.
                float hunt = (Mathf.PerlinNoise(Time.unscaledTime * 0.31f, 41.7f) - 0.5f) * 2f;
                dof.focusDistance = Mathf.Max(0.5f, Plugin.BlurFocusDistance.Value + hunt * 6f * t);
                dof.aperture = Mathf.Lerp(32f, Plugin.BlurAperture.Value, t);
                dof.focalLength = Mathf.Lerp(45f, 85f, t);
                dof.useCameraFov = false;
                // Small kernel on purpose: bokeh size is the expensive part and this runs every frame.
                dof.kernelSize = DepthOfFieldModel.KernelSize.Small;
                profile.depthOfField.settings = dof;
                profile.depthOfField.enabled = true;
            }

            if (Plugin.ChromaticEnabled.Value)
            {
                var ca = profile.chromaticAberration.settings;
                ca.intensity = Mathf.Lerp(caOriginal.intensity, Plugin.ChromaticIntensity.Value, t);
                profile.chromaticAberration.settings = ca;
                profile.chromaticAberration.enabled = true;
            }

            if (Plugin.GrainEnabled.Value && t > 0f)
            {
                var grain = profile.grain.settings;
                grain.intensity = Mathf.Lerp(grainOriginal.intensity, Plugin.GrainIntensity.Value, t);
                grain.size = Mathf.Lerp(1f, 1.6f, t);
                grain.colored = false;
                profile.grain.settings = grain;
                profile.grain.enabled = true;
            }

            // Tunnel vision. This is the blackout warning, so it is driven by peril rather than intensity:
            // it only closes in once you have committed to more than you can hold, and it reaches full
            // just as you go down. A player who watches the edges creep in and puts the bottle down
            // stays on their feet.
            if (Plugin.TunnelVisionEnabled.Value)
            {
                // Drunk alone closes it part way; nearing a blackout closes the rest, which keeps the
                // warning readable on top of the everyday tunnel.
                float tunnel = Mathf.Max(
                    Mathf.Clamp01(intensity) * Mathf.Clamp01(Plugin.TunnelWhileDrunk.Value),
                    Mathf.Clamp01(peril));
                var v = profile.vignette.settings;
                v.mode = VignetteModel.Mode.Classic;
                // A slow pulse so it reads as your own vision going rather than a static overlay.
                float pulse = 1f + 0.06f * Mathf.Sin(Time.unscaledTime * 2.1f) * tunnel;
                v.intensity = Mathf.Lerp(vignetteOriginal.intensity, Plugin.TunnelMaxIntensity.Value * pulse, tunnel);
                v.smoothness = Mathf.Lerp(Mathf.Max(vignetteOriginal.smoothness, 0.2f), 0.45f, tunnel);
                v.roundness = 1f;
                v.rounded = true;
                v.color = Color.black;
                profile.vignette.settings = v;
                profile.vignette.enabled = tunnel > 0f || vignetteWasEnabled;
            }
        }
    }
}
