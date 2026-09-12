using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// Stops the view spinning while ConfigurationManager is open.
    ///
    /// ConfigurationManager frees Unity's cursor when its window opens, but Sailwind's mouse look does
    /// not care about Unity's cursor. MouseLook.Update gates on Sailwind's own static cursorEnabled
    /// flag and reads the raw mouse axes, so with the menu up the camera turns every time you reach for
    /// a slider. Sailwind's own menus avoid this through MouseLook.ToggleMouseLookAndCursor(false),
    /// which sets that flag and GameState.inCursorMenu together, so that is what this calls.
    ///
    /// ConfigurationManager is found by GUID and read by reflection, so it stays an optional extra:
    /// without it installed this component does nothing at all. It only ever undoes a cursor state it
    /// set itself, so opening F1 from inside one of Sailwind's own cursor menus leaves that menu alone.
    /// </summary>
    public class ConfigMenuCursor : MonoBehaviour
    {
        private const string CmGuid = "com.bepis.bepinex.configurationmanager";

        /// <summary>True while the ConfigurationManager window is up, so effects can keep running for tuning.</summary>
        internal static bool MenuOpen;

        private bool resolved;
        private object cm;
        private PropertyInfo displayingWindow;

        private bool wasOpen;
        private bool weFreedCursor;

        private void Update()
        {
            if (!Resolve()) return;

            bool open = IsOpen();
            MenuOpen = open;
            if (open == wasOpen) return;
            wasOpen = open;

            if (open) OnOpened();
            else OnClosed();
        }

        private void OnOpened()
        {
            if (!Plugin.Enabled.Value || !Plugin.FreeCursorInConfigMenu.Value) return;
            if (!GameState.playing) return;             // main menu already has a free cursor
            if (GameState.inCursorMenu) return;         // already in one of Sailwind's own cursor menus
            // Mouse look is already off during a blackout, and entering a cursor menu then would hide
            // the vanilla sleep bar, which is also how the sleep audio mix gets stranded muffled.
            if (Drunkenness.BlackedOut) return;

            try
            {
                MouseLook.ToggleMouseLookAndCursor(false);
                weFreedCursor = true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Could not free the cursor for the config menu: " + e.Message);
            }
        }

        private void OnClosed()
        {
            if (!weFreedCursor) return;
            weFreedCursor = false;

            // Left the world while the menu was open. Nothing of ours to put back.
            if (!GameState.playing) return;

            try
            {
                MouseLook.ToggleMouseLookAndCursor(true);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Could not restore mouse look after the config menu: " + e.Message);
            }
        }

        private bool Resolve()
        {
            if (resolved) return cm != null && displayingWindow != null;
            resolved = true;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(CmGuid, out var info) || info.Instance == null)
                    return false;
                cm = info.Instance;
                displayingWindow = cm.GetType().GetProperty("DisplayingWindow",
                    BindingFlags.Public | BindingFlags.Instance);
                if (displayingWindow == null)
                {
                    Plugin.Log.LogWarning("ConfigurationManager found but DisplayingWindow was not; " +
                                          "cursor handling for the config menu is off.");
                    return false;
                }
                Plugin.Log.LogInfo("ConfigurationManager detected. The view will hold still while F1 is open.");
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("ConfigurationManager detection failed: " + e.Message);
                cm = null;
                return false;
            }
        }

        private bool IsOpen()
        {
            try
            {
                return (bool)displayingWindow.GetValue(cm, null);
            }
            catch
            {
                // Stop asking rather than throwing every frame.
                displayingWindow = null;
                return false;
            }
        }

        private void OnDestroy()
        {
            if (weFreedCursor) OnClosed();
        }
    }
}
