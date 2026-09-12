using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace ThreeSheets
{
    /// <summary>
    /// Detects whether another player is actually connected, which is the only thing that decides
    /// whether the blackout is allowed to touch Time.timeScale.
    ///
    /// Tests HasConnectedGuest rather than IsMultiplayer on purpose - Sailwind Co-op makes the same
    /// distinction for its own sleep handling, because hosting a lobby with nobody in it still runs the
    /// vanilla single-player flow. Sitting in an empty lobby should not cost you the time warp.
    ///
    /// Every failure path answers "yes, someone is connected". If the co-op mod is present but its
    /// internals have moved since this was written, the honest answer is that we do not know, and not
    /// knowing has to mean leaving the timescale alone.
    /// </summary>
    public static class Compat
    {
        private const string CoopGuid = "com.sailwindcoop.mod";

        private enum State { Unresolved, NoCoop, Resolved, Broken }

        private static State state = State.Unresolved;
        private static PropertyInfo hasConnectedGuest;
        private static PropertyInfo isMultiplayer;

        /// <summary>True when it is not safe to alter global time, so the blackout stays real-time.</summary>
        public static bool OtherPlayerConnected
        {
            get
            {
                if (state == State.Unresolved) Resolve();

                switch (state)
                {
                    case State.NoCoop:
                        return false;
                    case State.Broken:
                        return true;
                    default:
                        try
                        {
                            if (hasConnectedGuest != null)
                                return (bool)hasConnectedGuest.GetValue(null, null);
                            if (isMultiplayer != null)
                                return (bool)isMultiplayer.GetValue(null, null);
                            return true;
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning(
                                "Could not read co-op session state, assuming multiplayer: " + e.Message);
                            state = State.Broken;
                            return true;
                        }
                }
            }
        }

        private static void Resolve()
        {
            try
            {
                if (!Chainloader.PluginInfos.ContainsKey(CoopGuid))
                {
                    state = State.NoCoop;
                    Plugin.Log.LogInfo("Sailwind Co-op not installed. Blackouts will use the time warp.");
                    return;
                }

                var info = Chainloader.PluginInfos[CoopGuid];
                var pluginType = (info.Instance != null) ? info.Instance.GetType() : null;
                if (pluginType == null)
                {
                    state = State.Broken;
                    Plugin.Log.LogWarning("Sailwind Co-op is present but not loaded yet. Time warp disabled.");
                    return;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                hasConnectedGuest = pluginType.GetProperty("HasConnectedGuest", flags);
                isMultiplayer = pluginType.GetProperty("IsMultiplayer", flags);

                if (hasConnectedGuest == null && isMultiplayer == null)
                {
                    state = State.Broken;
                    Plugin.Log.LogWarning(
                        "Sailwind Co-op is installed but its session state could not be found. " +
                        "Blackouts will stay real-time to be safe.");
                    return;
                }

                state = State.Resolved;
                Plugin.Log.LogInfo("Sailwind Co-op detected. Time warp will be skipped while a crewmate is aboard.");
            }
            catch (Exception e)
            {
                state = State.Broken;
                Plugin.Log.LogWarning("Co-op detection failed, assuming multiplayer: " + e.Message);
            }
        }
    }
}
