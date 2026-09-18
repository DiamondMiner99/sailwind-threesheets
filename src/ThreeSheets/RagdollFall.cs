using UnityEngine;

namespace ThreeSheets
{
    /// <summary>
    /// A real, physics-driven fall - no body required.
    ///
    /// Nothing needs to be rendered for you to fall convincingly in first person. What the camera needs
    /// is something physical to follow, so this drops a single rigidbody where your head is, lets Unity
    /// take it down, and pins the camera to it. You hit the deck because a collider hit the deck, you
    /// slide because the deck is heeling, you tumble down the companionway if that is where you were
    /// standing. None of which a canned animation can do.
    ///
    /// The proxy is set up like a dropped item, because a dropped item is exactly this problem already
    /// solved by the game: layer 2, continuous collision, resting on the deck of a moving ship.
    ///
    /// The two-frame trick is the game's own. Aboard a boat the CharacterController lives in physics
    /// space (~Y200 underway) while the deck renders in visual space, and Sailwind runs items in both at
    /// once - ShipItem parented to boatModel, ItemRigidbody parented to walkCol - because, as co-op's
    /// ItemSyncManager puts it, "both use the SAME localPosition/localRotation values". So the proxy
    /// falls in physics space, and its localPosition is read straight back out in visual space to place
    /// the camera. Neither frame is looked up by name: the CharacterController's parent IS the physics
    /// frame and observerMirror's parent IS the visual frame, since the mirror is defined by copying
    /// controller.localPosition.
    /// </summary>
    public class RagdollFall : MonoBehaviour
    {
        private const int DroppedItemLayer = 2;

        private Transform physicsParent;
        private Transform visualParent;
        private Transform cam;
        private GameObject proxy;
        private Rigidbody body;

        private Quaternion startLocalRot;
        private Quaternion camRestLocalRot;

        // The camera's OWN local pose, which is what Stop() puts back. Kept separate from the spawn
        // point below: conflating the two is what dropped the eye another 0.3m on every physical
        // blackout, and what welded the view to a wreck when the frame moved.
        private Vector3 camRestLocalPos;
        private Quaternion camRestLocalRotL;
        private bool poseSaved;

        /// <summary>Frame-local spawn point for the proxy, including the drop to chest height.</summary>
        private Vector3 proxySpawnLocalPos;

        private float elapsed;
        private bool running;
        private bool frozen;

        public bool Running => running;

        /// <summary>True once the body has stopped moving, so the fade can land with the impact.</summary>
        public bool Settled { get; private set; }

        public bool Begin()
        {
            Stop();

            var mainCam = Camera.main;
            var cc = Refs.charController;
            if (mainCam == null || cc == null)
            {
                Plugin.Log.LogWarning("No camera or character controller, cannot fall physically.");
                return false;
            }

            cam = mainCam.transform;
            physicsParent = cc.transform.parent;
            visualParent = (Refs.observerMirror != null) ? Refs.observerMirror.transform.parent : null;

            // On land both frames are the same object, so a missing mirror parent is not fatal.
            if (visualParent == null) visualParent = physicsParent;
            if (physicsParent == null) physicsParent = visualParent;

            if (physicsParent == null || visualParent == null)
            {
                Plugin.Log.LogWarning("No parent frame to fall in, falling back to the view collapse.");
                return false;
            }

            // The camera's own local pose, the way CollapseAnimator saves it. Nothing in Sailwind writes
            // CenterEyeAnchor's local transform except BoatCamera.SwitchOff (BoatCamera.cs:95), so this
            // is the only restore that survives the player and the boat separating.
            camRestLocalPos = cam.localPosition;
            camRestLocalRotL = cam.localRotation;

            // Start the proxy exactly where the camera already is, expressed in frame-local coordinates.
            // Those numbers are valid in BOTH frames, which is the whole reason this works.
            proxySpawnLocalPos = InverseTransformPoint(visualParent, cam.position);
            camRestLocalRot = Quaternion.Inverse(visualParent.rotation) * cam.rotation;

            // Spawn slightly below the eye, at about chest height. The capsule is 1.15 tall, and
            // starting it centred on the camera puts its lower cap uncomfortably close to the deck.
            // This offset goes ONLY into the spawn point, never into what Stop() restores from.
            proxySpawnLocalPos += Vector3.down * 0.3f;

            // Refuse to spawn inside anything. A capsule created overlapping a collider gets ejected by
            // depenetration, which is violent and is exactly how a body ends up through a hull - crate
            // depenetration has already sunk a moored brig in this game once. Below decks, in a bunk, or
            // jammed against a bulkhead, the scripted collapse is the correct answer instead.
            Vector3 worldSpawn = TransformPoint(physicsParent, proxySpawnLocalPos);
            if (IsObstructed(worldSpawn))
            {
                Plugin.Log.LogInfo("No room to fall here, using the scripted collapse instead.");
                return false;
            }

            // Armed only once every refusal above has been passed. Set any earlier and an attempt that
            // bailed out - no room to fall, so the scripted collapse runs instead and Stop() is never
            // called - leaves a stale snapshot behind, which the NEXT Begin()'s leading Stop() would
            // then write onto a camera whose local pose has legitimately moved since (a crouch, or a
            // BoatCamera.SwitchOn/SwitchOff reparent at BoatCamera.cs:95).
            poseSaved = true;

            proxy = new GameObject("ThreeSheetsFallProxy");
            proxy.layer = DroppedItemLayer;
            proxy.transform.SetParent(physicsParent, worldPositionStays: false);
            proxy.transform.localPosition = proxySpawnLocalPos;
            proxy.transform.localRotation = Quaternion.identity;
            startLocalRot = proxy.transform.localRotation;

            // A capsule rather than a sphere: a sphere just rolls, a capsule topples and then lies down,
            // which is what going over looks like from the inside.
            var col = proxy.AddComponent<CapsuleCollider>();
            col.radius = 0.28f;
            col.height = 1.15f;
            col.direction = 1;

            body = proxy.AddComponent<Rigidbody>();
            // Deliberately NOT bodyweight. The proxy is parented into the boat's physics hierarchy, so
            // whatever it weighs it shoves the boat when it lands - and in co-op that shove happens on
            // the guest's machine only, against a boat the host owns, which is a brand new source of
            // guest/host divergence. Gravity does not care about mass, so a light proxy falls on exactly
            // the same arc, and against a multi-tonne hull its own bounce is unchanged. All that is lost
            // is a nudge to the ship that was never wanted.
            body.mass = Plugin.RagdollMass.Value;
            body.drag = 0.3f;
            body.angularDrag = 1.6f;
            body.useGravity = true;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            // Spinning fast enough to be unwatchable is the main way a camera ragdoll goes wrong.
            body.maxAngularVelocity = Plugin.RagdollMaxSpin.Value;
            // A 2kg body pressed by a heavy moving deck penetrates more easily than a heavy one would,
            // and dropping the mass to spare the boat is what made that worse. Buy the resistance back
            // in solver iterations rather than in mass.
            body.solverIterations = 14;
            body.solverVelocityIterations = 6;

            // The shove that starts you going over. Sideways and slightly forward, with matching spin,
            // direction picked per blackout so it is not the same fall every time.
            float side = (Random.value < 0.5f) ? -1f : 1f;
            Vector3 push = visualParent.InverseTransformDirection(cam.right) * side * Plugin.RagdollShove.Value
                           + visualParent.InverseTransformDirection(cam.forward) * Random.Range(0.2f, 0.7f);
            body.velocity = push;
            body.angularVelocity = new Vector3(
                Random.Range(-0.8f, 0.8f),
                Random.Range(-0.8f, 0.8f),
                side * Random.Range(2.2f, 3.4f));

            elapsed = 0f;
            frozen = false;
            Settled = false;
            running = true;
            return true;
        }

        /// <summary>
        /// Stops the body dead but leaves the camera where it landed. Called once the screen is black,
        /// so the proxy does not keep sliding around a heeling deck for the whole time warp.
        /// </summary>
        public void Freeze()
        {
            if (body != null && !frozen)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
                frozen = true;

                // Stop pinning the camera the moment the body lands. In v0.2.0 Freeze left this running,
                // so LateUpdate kept writing cam.position from frames cached at Begin() for the whole
                // blackout - up to 75 real seconds, through a disembark, through a sinking. The camera
                // now sits where it landed, parented as it always was, and moves with its parent.
                running = false;
            }
        }

        /// <summary>Puts the camera back and clears the proxy. Called behind a black screen.</summary>
        public void Stop()
        {
            running = false;

            // Restore the camera's OWN local pose. v0.2.0 wrote a world position computed through
            // visualParent, a frame that may be hundreds of meters down a wreck by now, which welded the
            // view there permanently. This also drops the baked-in boat roll: camRestLocalRot captured
            // the boat's attitude at Begin, so a ship that heeled or capsized left the horizon tilted.
            if (poseSaved)
            {
                if (cam == null && Camera.main != null) cam = Camera.main.transform;
                if (cam != null)
                {
                    cam.localPosition = camRestLocalPos;
                    cam.localRotation = camRestLocalRotL;
                }
            }
            poseSaved = false;

            if (proxy != null) Destroy(proxy);
            proxy = null;
            body = null;
            cam = null;
        }

        private void LateUpdate()
        {
            if (!running) return;

            if (cam == null || proxy == null || visualParent == null)
            {
                // A scene change, a stream-out or a co-op boat removal pulled the world out from under
                // the fall. Stop pinning, but KEEP poseSaved: a local-transform restore is still valid
                // when the parent is dead, and v0.2.0 gave that up here, so Stop() restored nothing at
                // all and the camera was abandoned at the last world pose it was given.
                Plugin.Log.LogWarning("The fall lost its frame; the camera will be put back from its own local pose.");
                running = false;
                return;
            }

            // The frames were cached in Begin(). If the player has been reparented since - vanilla's
            // swim-disembark (PlayerEmbarkerNew.cs:70-89) is exactly this - the pin is now writing the
            // camera through a frame the body is no longer in. Stop rather than follow the wreck down.
            var cc = Refs.charController;
            if ((bool)cc && cc.transform.parent != physicsParent)
            {
                Plugin.Log.LogWarning("The player was moved out of the frame the fall started in; releasing the camera.");
                Freeze();
                Settled = true;
                return;
            }

            elapsed += Time.unscaledDeltaTime;

            // Read the proxy's pose out of the physics frame and apply it in the visual frame. The local
            // numbers are shared between the two, so this needs no conversion, only a different parent.
            Vector3 localPos = proxy.transform.localPosition;
            Quaternion localDelta = proxy.transform.localRotation * Quaternion.Inverse(startLocalRot);

            Quaternion upright = visualParent.rotation * camRestLocalRot;
            Quaternion tumbled = visualParent.rotation * (localDelta * camRestLocalRot);

            cam.position = TransformPoint(visualParent, localPos);
            // Blend back toward upright so a violent spin stays watchable rather than nauseating.
            cam.rotation = Quaternion.Slerp(upright, tumbled, Mathf.Clamp01(Plugin.RagdollTumble.Value));

            // The leash. Whatever physics does, the camera does not follow a body that has left the
            // world - through the deck, ejected by a collision, or over the side. Freeze where it went
            // wrong and let the sequence carry on: a blackout that ends slightly oddly beats a camera
            // sinking through a hull into open water. Note it measures displacement INSIDE the frame, so
            // a whole frame descending reads as zero stray; the reparent test above is what catches that.
            float strayed = (localPos - proxySpawnLocalPos).magnitude;
            if (strayed > Plugin.RagdollLeash.Value)
            {
                Plugin.Log.LogWarning(
                    $"Fall strayed {strayed:F1}m from where it started, past the {Plugin.RagdollLeash.Value:F1}m " +
                    "leash. Freezing it - it has probably gone through something or over the side.");
                Freeze();
                Settled = true;
                running = false;
                return;
            }

            if (!Settled && body != null)
            {
                bool stopped = body.velocity.sqrMagnitude < 0.06f && body.angularVelocity.sqrMagnitude < 0.25f;
                // Give it a moment before believing it has settled, or it reports settled on frame one
                // before gravity has done anything.
                if ((stopped && elapsed > 0.55f) || elapsed > Plugin.FallWatchSeconds.Value)
                {
                    Settled = true;
                }
            }
        }

        /// <summary>
        /// Is there solid geometry where the body would appear? Triggers are ignored, since walking
        /// through an embark trigger or a door volume is not a reason to refuse to fall over.
        /// </summary>
        private static bool IsObstructed(Vector3 worldPos)
        {
            try
            {
                var hits = Physics.OverlapSphere(worldPos, 0.32f, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i] != null && !hits[i].isTrigger) return true;
                }
                return false;
            }
            catch (System.Exception e)
            {
                // If the test itself fails, assume the worst and take the safe path.
                Plugin.Log.LogWarning("Could not test the spawn point: " + e.Message);
                return true;
            }
        }

        private static Vector3 TransformPoint(Transform t, Vector3 local)
        {
            return (t != null) ? t.TransformPoint(local) : local;
        }

        private static Vector3 InverseTransformPoint(Transform t, Vector3 world)
        {
            return (t != null) ? t.InverseTransformPoint(world) : world;
        }

        private void OnDestroy()
        {
            Stop();
        }
    }
}
