using System;
using UnityEngine;
using UnityEngine.XR;
using MelonLoader;

namespace ToFMultiplayer
{
    /// <summary>
    /// A VR laser pointer for menus that are awkward to poke with the fist (the Server Browser
    /// list and the Enter-Code keypad). It points a beam from a controller and lets the player
    /// click UI with the trigger.
    ///
    /// How it drives clicks: the game already converts a 3D ray into UI clicks via
    /// <see cref="VRInputModule"/> + <c>VRGraphicRaycaster</c> — the fist mechanism just feeds it
    /// <see cref="VRInputModule.CustomControllerRay"/> and <see cref="VRInputModule.CustomControllerButtonDown"/>
    /// (see UIPointerController). This component feeds those same two values, but from the
    /// controller's transform + the XR trigger button, so the exact same raycast/click pipeline
    /// runs. No Harmony, no game changes.
    ///
    /// <see cref="PointerActive"/> is read by the multiplayer buttons' click handlers so a laser
    /// click is accepted even though no fist ever touched a pointer (which is what normally arms
    /// MenuManager.lastHand).
    /// </summary>
    public class LaserPointer : MonoBehaviour
    {
        public static LaserPointer Instance { get; private set; }

        /// <summary>True while the laser is the active input for the current screen.</summary>
        public static bool PointerActive { get; private set; }

        private const float MaxDistance = 25f;
        private const float TriggerThreshold = 0.6f;

        private LineRenderer _line;
        private Transform _dot;
        private Collider[] _targets = Array.Empty<Collider>();
        private bool _active;

        public static LaserPointer GetOrCreate()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("MP_LaserPointer");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Instance = go.AddComponent<LaserPointer>();
            return Instance;
        }

        private void Awake()
        {
            try
            {
                _line = gameObject.AddComponent<LineRenderer>();
                _line.useWorldSpace = true;
                _line.positionCount = 2;
                _line.widthMultiplier = 0.006f;
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (shader != null) _line.material = new Material(shader);
                _line.startColor = _line.endColor = new Color(0.25f, 0.8f, 1f, 0.9f);
                _line.enabled = false;

                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "MP_LaserDot";
                var sc = sphere.GetComponent<Collider>();
                if (sc != null) UnityEngine.Object.Destroy(sc); // never collide with anything
                _dot = sphere.transform;
                _dot.SetParent(transform, false);
                _dot.localScale = Vector3.one * 0.022f;
                var mr = sphere.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    var s = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
                    if (s != null) mr.material = new Material(s);
                    mr.material.color = new Color(0.25f, 0.8f, 1f, 1f);
                }
                _dot.gameObject.SetActive(false);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Laser] Awake error: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>Colliders the beam should visually stop on (the menu's canvas collider).</summary>
        public void SetTargets(params Collider[] colliders)
        {
            _targets = colliders ?? Array.Empty<Collider>();
        }

        public void SetActive(bool on)
        {
            _active = on;
            PointerActive = on;
            if (_line != null) _line.enabled = on;
            if (_dot != null) _dot.gameObject.SetActive(false);
            if (!on)
            {
                // Never leave a stuck button-down behind for the fist mechanism.
                try { VRInputModule.CustomControllerButtonDown = false; } catch { }
            }
        }

        private void Update()
        {
            if (!_active) return;

            var vr = VRManager.instance;
            if (vr == null) return;

            // Aim with whichever controller is available; if a trigger is pressed, that hand wins.
            bool rightPressed = ReadTrigger(XRNode.RightHand);
            bool leftPressed = ReadTrigger(XRNode.LeftHand);

            Transform hand = vr.rightController;
            bool pressed = rightPressed;
            if (leftPressed && !rightPressed)
            {
                hand = vr.leftController;
                pressed = true;
            }
            if (hand == null) hand = vr.rightController != null ? vr.rightController : vr.leftController;
            if (hand == null) return;

            Vector3 origin = hand.position;
            Vector3 dir = hand.forward;
            var ray = new Ray(origin, dir);

            // Feed the game's input pipeline (same statics the fist uses).
            if (VRInputModule.Instance != null)
            {
                VRInputModule.CustomControllerRay = ray;
                VRInputModule.CustomControllerButtonDown = pressed;
            }

            // Visual: stop the beam on the menu collider if hit, else draw full length.
            Vector3 end = origin + dir * MaxDistance;
            float best = float.PositiveInfinity;
            bool hitAny = false;
            foreach (var c in _targets)
            {
                if (c == null) continue;
                if (c.Raycast(ray, out RaycastHit hit, MaxDistance) && hit.distance < best)
                {
                    best = hit.distance;
                    end = hit.point;
                    hitAny = true;
                }
            }

            if (_line != null)
            {
                _line.SetPosition(0, origin);
                _line.SetPosition(1, end);
            }
            if (_dot != null)
            {
                _dot.gameObject.SetActive(hitAny);
                if (hitAny) _dot.position = end;
            }
        }

        private static bool ReadTrigger(XRNode node)
        {
            try
            {
                var dev = InputDevices.GetDeviceAtXRNode(node);
                if (!dev.isValid) return false;
                if (dev.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed))
                    return pressed;
                if (dev.TryGetFeatureValue(CommonUsages.trigger, out float amount))
                    return amount > TriggerThreshold;
            }
            catch { }
            return false;
        }
    }
}
