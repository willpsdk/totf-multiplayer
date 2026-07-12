using MelonLoader;
using TotF;
using UnityEngine;

namespace ToFMultiplayer
{
    /// <summary>
    /// Moves the guest's corner UI (PlayerCorner: stool, round menu, position marker) and
    /// QuitTrigger over to the blue corner — by relocating the scene's actual objects,
    /// not cloning them. Two reasons that matters:
    ///  - PlayerController always uses PlayerCorner.find() (the tagged scene object) for
    ///    SendPlayerToCorner / StartBreakMovement, so the game keeps teleporting the player
    ///    to wherever that object sits. Move the object and every game-driven corner
    ///    return just goes to blue automatically — that's what actually keeps the guest
    ///    in the blue corner for the whole bout.
    ///  - Cloning left the vanilla round menu (with a live ContinueTrigger) sitting at the
    ///    red corner where the ghost stands, and the guest could trigger it by accident.
    /// </summary>
    public static class BlueCornerUI
    {
        private static PlayerCorner _vanillaCorner;
        private static QuitTrigger _vanillaQuitTrigger;
        private static bool _movedToBlue;

        public static PlayerCorner GetCornerUI(BoutController.Corner corner)
        {
            EnsureVanillaRefs();
            EnsurePlacement(corner);
            return _vanillaCorner;
        }

        public static QuitTrigger GetQuitTrigger(BoutController.Corner corner)
        {
            EnsureVanillaRefs();
            EnsurePlacement(corner);
            return _vanillaQuitTrigger;
        }

        /// <summary>Applies the corner placement for this player. Safe to call repeatedly.</summary>
        public static void EnsurePlacement(BoutController.Corner corner)
        {
            if (corner != BoutController.Corner.Blue || _movedToBlue) return;

            var bc = BoutController.instance;
            if (bc == null || bc.redStart == null || bc.blueStart == null)
            {
                MelonLogger.Warning("[BlueCornerUI] redStart/blueStart unavailable — cannot move corner UI to blue yet");
                return;
            }

            EnsureVanillaRefs();

            try
            {
                if (_vanillaCorner != null)
                    MirrorTransform(_vanillaCorner.transform, bc.redStart, bc.blueStart);
                if (_vanillaQuitTrigger != null)
                    MirrorTransform(_vanillaQuitTrigger.transform, bc.redStart, bc.blueStart);

                _movedToBlue = true;
                MelonLogger.Msg("[BlueCornerUI] ✓ Relocated PlayerCorner + QuitTrigger to the blue corner");
            }
            catch (System.Exception e)
            {
                MelonLogger.Error($"[BlueCornerUI] EnsurePlacement error: {e.Message}");
            }
        }

        public static void Reset()
        {
            // Scene reloads recreate the corner objects at their original spots;
            // just drop the cached references.
            _vanillaCorner = null;
            _vanillaQuitTrigger = null;
            _movedToBlue = false;
        }

        // ─────────────────────────────────────────────────────

        private static void EnsureVanillaRefs()
        {
            if (_vanillaCorner == null)
                _vanillaCorner = PlayerCorner.find();

            if (_vanillaQuitTrigger == null)
                _vanillaQuitTrigger = Object.FindObjectOfType<QuitTrigger>();
        }

        // Re-expresses the target's pose relative to redStart in blueStart's frame, so the
        // corner UI keeps its exact offset/orientation but at the opposite corner.
        private static void MirrorTransform(Transform target, Transform fromAnchor, Transform toAnchor)
        {
            Vector3 localPos = fromAnchor.InverseTransformPoint(target.position);
            Quaternion localRot = Quaternion.Inverse(fromAnchor.rotation) * target.rotation;

            target.position = toAnchor.TransformPoint(localPos);
            target.rotation = toAnchor.rotation * localRot;
        }
    }
}
