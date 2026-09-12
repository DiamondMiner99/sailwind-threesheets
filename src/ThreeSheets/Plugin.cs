using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ThreeSheets
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.diamondminer99.threesheets";
        public const string PluginName = "Three Sheets to the Wind (Drunk Mod)";
        // BepInEx 5 parses this as a strict System.Version. No SemVer suffixes, or the plugin
        // silently fails to load with no error.
        public const string PluginVersion = "0.1.0";

        public static ManualLogSource Log;

        // Sections are numbered so ConfigurationManager, which sorts them alphabetically, lists them
        // in the order a player thinks about them: drink, pass out, what it looks like, how you fall.
        private const string SecGeneral = "1. General";
        private const string SecDrinking = "2. Drinking";
        private const string SecBlackout = "3. Blackout";
        private const string SecEffects = "4. Drunk Effects";
        private const string SecFalling = "5. Falling";
        private const string SecTesting = "6. Testing";

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> FreeCursorInConfigMenu;

        // Drinking
        public static ConfigEntry<float> AbsorbRate;
        public static ConfigEntry<float> DecayRate;

        // Blackout
        public static ConfigEntry<bool> BlackoutEnabled;
        public static ConfigEntry<float> BlackoutThreshold;
        public static ConfigEntry<float> WarnFraction;
        public static ConfigEntry<bool> TimeWarpEnabled;
        public static ConfigEntry<float> WakeAtBac;
        public static ConfigEntry<float> MinOutHours;
        public static ConfigEntry<float> MaxOutHours;
        public static ConfigEntry<float> SoberingWhileOut;
        public static ConfigEntry<float> BlackoutSeconds;
        public static ConfigEntry<float> SleepQuality;
        public static ConfigEntry<float> HangoverWaterCost;
        public static ConfigEntry<float> BlackoutGraceSeconds;
        public static ConfigEntry<float> HangoverFloor;

        // Drunk effects
        public static ConfigEntry<float> EffectsStart;
        public static ConfigEntry<float> EffectsFull;
        public static ConfigEntry<bool> BlurEnabled;
        public static ConfigEntry<float> BlurFocusDistance;
        public static ConfigEntry<float> BlurAperture;
        public static ConfigEntry<bool> ChromaticEnabled;
        public static ConfigEntry<float> ChromaticIntensity;
        public static ConfigEntry<bool> GrainEnabled;
        public static ConfigEntry<float> GrainIntensity;
        public static ConfigEntry<bool> TunnelVisionEnabled;
        public static ConfigEntry<float> TunnelWhileDrunk;
        public static ConfigEntry<float> TunnelMaxIntensity;
        public static ConfigEntry<bool> LookDriftEnabled;
        public static ConfigEntry<float> LookDriftDegrees;
        public static ConfigEntry<float> LookDriftSpeed;
        public static ConfigEntry<float> LookDriftLean;
        public static ConfigEntry<bool> MoveWobbleEnabled;
        public static ConfigEntry<float> MoveWobbleAmount;
        public static ConfigEntry<bool> WobbleEnabled;
        public static ConfigEntry<float> WobbleRoll;
        public static ConfigEntry<float> WobbleFov;
        public static ConfigEntry<float> WobbleSpeed;

        // Falling
        public static ConfigEntry<bool> FallEnabled;
        public static ConfigEntry<bool> FallPhysical;
        public static ConfigEntry<float> RagdollTumble;
        public static ConfigEntry<float> RagdollShove;
        public static ConfigEntry<float> FallWatchSeconds;
        public static ConfigEntry<float> CollapseSeconds;
        public static ConfigEntry<float> CollapseDrop;
        public static ConfigEntry<float> CollapseRoll;
        public static ConfigEntry<float> RagdollMass;
        public static ConfigEntry<float> RagdollLeash;
        public static ConfigEntry<float> RagdollMaxSpin;

        // Testing. Both default OFF in code so a release ships with them off without anyone having to
        // remember; they get switched on in a local config file for tuning sessions.
        public static ConfigEntry<float> HoldDrunkAt;
        public static ConfigEntry<KeyboardShortcut> BlackoutKey;

        /// <summary>Unscaled-time deadline for the blackout watchdog. Zero when no blackout is running.</summary>
        public static float WatchdogDeadline;

        // What a running blackout has taken and still owes back. Held on the plugin rather than inside
        // the coroutine so the watchdog can undo all of it even if the coroutine is dead.
        public static bool ControlTaken;
        public static bool TimeWarpHeld;
        public static float TimeScaleWas = 1f;
        public static float FixedStepWas = 0.02222f;
        public static bool GodModeHeld;
        public static bool GodModeWas;
        public static bool SleepFlagHeld;
        public static bool SleepFlagWas;
        public static bool EyesFlagHeld;
        public static bool EyesFlagWas;

        private Harmony harmony;

        // Running order within a section. ConfigurationManager lists higher numbers first.
        private int order = 1000;

        private void Awake()
        {
            Log = Logger;

            // ---- 1. General ----
            order = 1000;
            Enabled = Toggle(SecGeneral, "Enabled", true,
                "Master switch. Off disables the whole mod.");
            FreeCursorInConfigMenu = Toggle(SecGeneral, "FreeCursorInConfigMenu", true,
                "Stops the view turning while the F1 config menu is open. Turn this off if the cursor " +
                "ever gets stuck.");

            // ---- 2. Drinking ----
            order = 1000;
            AbsorbRate = Slider(SecDrinking, "AbsorbRate", 0.06f, 0.01f, 0.5f,
                "How fast what you have drunk reaches your blood, per second. Lower means a longer " +
                "delay between drinking and feeling it. Around 0.25 makes blackouts quick to test.");
            DecayRate = Slider(SecDrinking, "DecayRate", 12f, 0f, 60f,
                "How fast you sober up, per second of game time. 12 is the vanilla rate. 0 holds your " +
                "drunkenness steady, which is useful for tuning the effects.");

            // ---- 3. Blackout ----
            order = 1000;
            BlackoutEnabled = Toggle(SecBlackout, "Enabled", true,
                "Pass out when your blood alcohol reaches Threshold.");
            BlackoutThreshold = Slider(SecBlackout, "Threshold", 150f, 20f, 400f,
                "Blood alcohol that puts you down. A sip of rum is 18, wine 12, beer 6. Drop it to 40 " +
                "to test.");
            WarnFraction = Slider(SecBlackout, "WarnFraction", 0.7f, 0f, 1f,
                "Where the tunnel vision starts closing in, as a share of Threshold. Measured against " +
                "everything you have drunk, including what has not reached your blood yet.");
            TimeWarpEnabled = Toggle(SecBlackout, "TimeWarp", true,
                "Single player only. Run time at 16x while you are out, like sleeping. Skipped when " +
                "another player is connected.");
            WakeAtBac = Slider(SecBlackout, "WakeAtBloodAlcohol", 40f, 0f, 150f,
                "You wake once your blood alcohol has fallen to this. A sip of rum is 18.");
            MinOutHours = Slider(SecBlackout, "MinOutHours", 2f, 0.5f, 6f,
                "Fewest game hours a blackout lasts.");
            MaxOutHours = Slider(SecBlackout, "MaxOutHours", 8f, 1f, 12f,
                "Most game hours a blackout lasts. You wake still drunk if it reaches this. A warped " +
                "blackout also ends after 75 real seconds.");
            SoberingWhileOut = Slider(SecBlackout, "SoberingWhileOut", 3f, 1f, 8f,
                "How much faster alcohol wears off while you are out. 1 is the normal rate.");
            BlackoutSeconds = Slider(SecBlackout, "OutSeconds", 20f, 3f, 120f,
                "Real seconds spent unconscious when the time warp is off or another player is " +
                "connected. Your ship keeps sailing.");
            SleepQuality = Slider(SecBlackout, "SleepQuality", 0.45f, 0f, 1f,
                "Rest gained while out, as a share of normal sleep (8 rest per game hour). Rises " +
                "toward normal as you sober up.");
            HangoverWaterCost = Slider(SecBlackout, "HangoverWaterCost", 15f, 0f, 80f,
                "Extra hydration lost on waking, on top of the normal thirst for the hours you were out.");
            BlackoutGraceSeconds = Slider(SecBlackout, "GraceSeconds", 60f, 0f, 600f,
                "Real seconds after waking during which you cannot black out again.",
                advanced: true);
            HangoverFloor = Slider(SecBlackout, "HangoverFloor", 15f, 0f, 50f,
                "The hangover will not push a need below this. Stops a blackout chaining into a " +
                "vanilla thirst pass-out, which moves you to port.",
                advanced: true);

            // ---- 4. Drunk Effects ----
            order = 1000;
            EffectsStart = Slider(SecEffects, "EffectsStart", 20f, 0f, 150f,
                "Blood alcohol at which the drunk effects begin.");
            EffectsFull = Slider(SecEffects, "EffectsFull", 100f, 10f, 200f,
                "Blood alcohol at which they reach full strength. Keep it above EffectsStart.");
            BlurEnabled = Toggle(SecEffects, "InstrumentBlur", true,
                "Shallow focus. Things in your hands blur, the horizon stays sharp.");
            BlurFocusDistance = Slider(SecEffects, "BlurFocusDistance", 60f, 1f, 80f,
                "Distance in meters where focus sits. Anything much nearer blurs.");
            BlurAperture = Slider(SecEffects, "BlurAperture", 0.9f, 0.1f, 10f,
                "Aperture at full drunk. Smaller is blurrier.");
            ChromaticEnabled = Toggle(SecEffects, "ChromaticAberration", true,
                "Color fringing.");
            ChromaticIntensity = Slider(SecEffects, "ChromaticIntensity", 1f, 0f, 1f,
                "Strength of the color fringing at full drunk.");
            GrainEnabled = Toggle(SecEffects, "Grain", true,
                "Film grain.");
            GrainIntensity = Slider(SecEffects, "GrainIntensity", 1f, 0f, 1f,
                "Strength of the grain at full drunk.");
            TunnelVisionEnabled = Toggle(SecEffects, "TunnelVision", true,
                "Vignette that closes in while you are drunk and closes further as you near a " +
                "blackout. This is the warning that you are about to pass out.");
            TunnelWhileDrunk = Slider(SecEffects, "TunnelWhileDrunk", 0.7f, 0f, 1f,
                "How closed the tunnel is from being drunk alone, as a share of TunnelMaxIntensity. " +
                "Keep it under 100% so the blackout warning still has room to show.");
            TunnelMaxIntensity = Slider(SecEffects, "TunnelMaxIntensity", 1.6f, 0f, 2.5f,
                "How far the tunnel closes just before you pass out. Values above 1 keep closing; " +
                "around 2 leaves a small window in the middle of the screen.");
            LookDriftEnabled = Toggle(SecEffects, "LookDrift", true,
                "Slow wander on the view that you have to keep correcting. Also makes you walk a " +
                "wavy line.");
            LookDriftDegrees = Slider(SecEffects, "LookDriftDegrees", 18f, 0f, 45f,
                "Peak drift in degrees at full drunk.");
            LookDriftSpeed = Slider(SecEffects, "LookDriftSpeed", 0.233f, 0.05f, 2f,
                "How quickly the drift wanders.");
            LookDriftLean = Slider(SecEffects, "LookDriftLean", 1f, 0f, 1f,
                "Side-to-side lean along with the drift, as a share of the vertical drift.");
            MoveWobbleEnabled = Toggle(SecEffects, "MoveWobble", true,
                "Move a bit further than you meant to when walking.");
            MoveWobbleAmount = Slider(SecEffects, "MoveWobbleAmount", 0.6f, 0f, 1f,
                "Extra movement at full drunk. 0.6 is 60 percent further than you meant to go.");
            WobbleEnabled = Toggle(SecEffects, "ScreenWobble", true,
                "The horizon rocks and the field of view breathes in and out.");
            WobbleRoll = Slider(SecEffects, "WobbleRoll", 3f, 0f, 20f,
                "Degrees the horizon rocks either way at full drunk.");
            WobbleFov = Slider(SecEffects, "WobbleFov", 4f, 0f, 15f,
                "Degrees the field of view breathes in and out at full drunk.");
            WobbleSpeed = Slider(SecEffects, "WobbleSpeed", 0.4f, 0.05f, 2f,
                "Rocks per second.");

            // ---- 5. Falling ----
            order = 1000;
            FallEnabled = Toggle(SecFalling, "Enabled", true,
                "Fall over when you black out. Off is a straight fade to black. This switch covers " +
                "both kinds of fall.");
            FallPhysical = Toggle(SecFalling, "Physical", true,
                "Physics fall: a body is dropped at your head and the camera follows it, so you land " +
                "on the deck and slide if the boat is heeling. Off uses the scripted fall, which is " +
                "also used below decks or anywhere there is no room to fall.");
            RagdollTumble = Slider(SecFalling, "Tumble", 0.8f, 0f, 1f,
                "How much of the body's spin the camera takes. Lower this if the fall makes you queasy.");
            RagdollShove = Slider(SecFalling, "Shove", 1.5f, 0f, 5f,
                "Sideways speed at the start of the fall, in meters per second.");
            FallWatchSeconds = Slider(SecFalling, "WatchSeconds", 2.4f, 0.8f, 5f,
                "Longest the physics fall is shown before the screen goes fully black. Ends sooner " +
                "once the body stops moving.");
            CollapseSeconds = Slider(SecFalling, "ScriptedSeconds", 1.6f, 0.5f, 4f,
                "Scripted fall only: how long it takes.");
            CollapseDrop = Slider(SecFalling, "ScriptedDrop", 1.35f, 0.5f, 1.8f,
                "Scripted fall only: meters the view drops.");
            CollapseRoll = Slider(SecFalling, "ScriptedRoll", 72f, 0f, 120f,
                "Scripted fall only: degrees the horizon rolls.");
            RagdollMass = Slider(SecFalling, "Mass", 2f, 0.5f, 20f,
                "Mass of the falling body in kg. Kept low so landing does not shove the boat. Gravity " +
                "ignores mass, so the fall looks the same either way.",
                advanced: true);
            RagdollLeash = Slider(SecFalling, "Leash", 3.5f, 1f, 10f,
                "Meters the falling body may travel from where you were standing before the camera " +
                "stops following it.",
                advanced: true);
            RagdollMaxSpin = Slider(SecFalling, "MaxSpin", 6f, 1f, 15f,
                "Ceiling on how fast the falling body can spin, in radians per second.",
                advanced: true);

            // ---- 6. Testing ----
            order = 1000;
            HoldDrunkAt = Slider(SecTesting, "HoldDrunkAt", 0f, 0f, 200f,
                "Testing only, leave at 0 for normal play. Holds you at least this drunk from spawn so " +
                "the effects can be tuned without drinking. Visual only: it never triggers a blackout " +
                "and does not drain your rest. 100 is every effect at full strength with the default " +
                "EffectsFull.");
            BlackoutKey = Config.Bind(SecTesting, "BlackoutKey", KeyboardShortcut.Empty,
                new ConfigDescription(
                    "Testing only, leave unbound for normal play. Press to black out immediately, " +
                    "running the full sequence. Ignores the grace period and the Blackout/Enabled switch.",
                    null, Attributes(false)));

            harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            var runner = new GameObject("ThreeSheetsRunner");
            runner.AddComponent<DrunkRunner>();
            runner.AddComponent<CollapseAnimator>();
            runner.AddComponent<RagdollFall>();
            runner.AddComponent<ConfigMenuCursor>();
            runner.AddComponent<ScreenWobble>();
            DontDestroyOnLoad(runner);

            Log.LogInfo(PluginName + " " + PluginVersion + " loaded.");
        }

        /// <summary>
        /// A float setting with a range, which ConfigurationManager draws as a slider (as a percentage
        /// when the range is 0 to 1) and which BepInEx clamps on load, so a typo in the file cannot
        /// hand the model a negative absorb rate.
        /// </summary>
        private ConfigEntry<float> Slider(string section, string key, float dflt, float min, float max,
            string description, bool advanced = false)
        {
            return Config.Bind(section, key, dflt, new ConfigDescription(description,
                new AcceptableValueRange<float>(min, max), Attributes(advanced)));
        }

        private ConfigEntry<bool> Toggle(string section, string key, bool dflt, string description,
            bool advanced = false)
        {
            return Config.Bind(section, key, dflt, new ConfigDescription(description, null, Attributes(advanced)));
        }

        /// <summary>
        /// Keeps settings in declaration order in the menu, and hides the safety rails behind
        /// ConfigurationManager's "Show advanced" box. Ignored harmlessly when it is not installed.
        /// </summary>
        private ConfigurationManagerAttributes Attributes(bool advanced)
        {
            var a = new ConfigurationManagerAttributes { Order = order-- };
            if (advanced) a.IsAdvanced = true;
            return a;
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
        }

        /// <summary>
        /// True only when the player is on their feet with control of themselves. Every effect checks
        /// this, so nothing wobbles the camera or steals input during menus, the shipyard, a sleep, or
        /// a vanilla recovery.
        /// </summary>
        public static bool PlayerIsInControl()
        {
            if (!GameState.playing) return false;
            if (GameState.recovering) return false;
            if (GameState.sleeping) return false;
            if (GameState.currentShipyard != null) return false;
            if (Drunkenness.BlackedOut) return false;
            return true;
        }
    }
}
