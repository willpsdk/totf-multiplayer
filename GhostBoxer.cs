using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using MelonLoader;
using RootMotion.Dynamics;
using RootMotion.FinalIK;
using TMPro;
using TotF;

namespace ToFMultiplayer
{
    public class GhostBoxer : MonoBehaviour
    {
        public GameObject BoxerInstance { get; private set; }

        private BoutController.Corner _corner = BoutController.Corner.Blue;

        internal bool _initialized;

        // ── Enemy-puppet takeover ────────────────────────────────
        private EnemyController _enemy;
        private PuppetMaster _pm;
        private FullBodyBipedIK _fbbik;
        private LookAtIK _lik;
        private PuppetMaster.UpdateDelegate _driveDelegate;
        private float _baseHeadHeight = 1.65f; // model head height above root, captured at takeover
        private float _lastYaw;

        // ── Fallback direct-bone references (non-EnemyController prefabs only)
        private Transform _headBone;
        private Transform _leftHandBone;
        private Transform _rightHandBone;
        private bool _useBoneFallback;

        // ── Damage forwarding state
        private float _lastGhostTrauma;
        private float _lastGhostPain;
        private float _lastGhostDizzy;

        private static readonly FieldInfo TraumaField = typeof(BoxerController).GetField("traumaDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo PainField = typeof(BoxerController).GetField("painDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo IsBlockingField = typeof(BoxerController).GetField("isBlocking",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo PmField = typeof(EnemyController).GetField("pm",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo IsGettingUpField = typeof(EnemyController).GetField("isGettingUp",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo IsKneelingField = typeof(EnemyController).GetField("isKneeling",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo DizzyRegenField = typeof(BoxerController).GetField("dizzyRegen",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private float _dizzyRegen = 108f;   // BoxerController default; re-read at init

        // ── Snapshot interpolation ───────────────────────────────
        //
        // Incoming packets get timestamped and stored in a ring buffer, and the puppet
        // renders at (now - BUFFER_DELAY), interpolating between the two nearest
        // snapshots. Kills the jitter from irregular packet arrival without adding lag
        // you'd notice.

        // 90ms because Steam P2P likes to deliver packets in little bursts, and 60ms
        // wasn't enough buffer for that — it'd underrun, freeze the pose, then snap it
        // forward. That's the "character jitters sometimes" people were seeing.
        private const float BUFFER_DELAY = 0.090f; // seconds behind network time
        private const int BUFFER_SIZE = 64;         // ring buffer capacity
        private const float MAX_SNAP_DIST = 2.0f;   // teleport if > 2m away (respawn etc.)

        private struct Snapshot
        {
            public float time;
            public Vector3 headPos, leftPos, rightPos;
            public Quaternion headRot, leftRot, rightRot;
        }

        private readonly Snapshot[] _buf = new Snapshot[BUFFER_SIZE];
        private int _bufHead = 0;
        private int _bufCount = 0;
        private bool _hasAnySnapshot = false;

        // Smoothed targets — updated once per frame from the interpolated snapshot
        private Vector3 _smoothHeadPos, _smoothLeftPos, _smoothRightPos;
        private Quaternion _smoothHeadRot, _smoothLeftRot, _smoothRightRot;
        private bool _smoothInitialized;

        // Velocity tracking for adaptive smoothing (punches get less lag)
        private Vector3 _prevLeftPos, _prevRightPos;

        // ── Nameplate ────────────────────────────────────────────

        private Transform _plateRoot;
        private TextMeshProUGUI _plateText;

        /// <summary>Shows the opponent's name (and rating) floating above the ghost's
        /// head, billboarded to the local player's view.</summary>
        public void SetNameplate(string text)
        {
            try
            {
                if (_plateRoot == null)
                {
                    var rootGO = new GameObject("GhostNameplate");
                    rootGO.transform.SetParent(transform, false);
                    _plateRoot = rootGO.transform;

                    var canvasGO = new GameObject("Canvas");
                    canvasGO.transform.SetParent(rootGO.transform, false);
                    var canvas = canvasGO.AddComponent<Canvas>();
                    canvas.renderMode = RenderMode.WorldSpace;
                    var crt = canvasGO.GetComponent<RectTransform>();
                    crt.sizeDelta = new Vector2(560f, 84f);
                    crt.localScale = Vector3.one * 0.0014f;

                    var bgGO = new GameObject("BG");
                    bgGO.transform.SetParent(canvasGO.transform, false);
                    var bgRT = bgGO.AddComponent<RectTransform>();
                    bgRT.anchoredPosition = Vector2.zero;
                    bgRT.sizeDelta = new Vector2(560f, 84f);
                    var bg = bgGO.AddComponent<Image>();
                    bg.color = new Color(0f, 0f, 0f, 0.45f);
                    bg.raycastTarget = false;

                    var txtGO = new GameObject("Name");
                    txtGO.transform.SetParent(canvasGO.transform, false);
                    var txtRT = txtGO.AddComponent<RectTransform>();
                    txtRT.anchoredPosition = Vector2.zero;
                    txtRT.sizeDelta = new Vector2(540f, 80f);
                    _plateText = txtGO.AddComponent<TextMeshProUGUI>();
                    _plateText.fontSize = 44;
                    _plateText.alignment = TextAlignmentOptions.Center;
                    _plateText.color = Color.white;
                    _plateText.enableAutoSizing = true;
                    _plateText.fontSizeMax = 44f;
                    _plateText.fontSizeMin = 20f;
                    _plateText.raycastTarget = false;
                }
                _plateText.text = text;
            }
            catch (Exception e) { MelonLogger.Warning($"[Ghost] SetNameplate: {e.Message}"); }
        }

        private void UpdateNameplate()
        {
            if (_plateRoot == null) return;
            try
            {
                Vector3 headPos = _enemy != null && _enemy.head != null
                    ? _enemy.head.position
                    : transform.position + Vector3.up * _baseHeadHeight;
                _plateRoot.position = headPos + Vector3.up * 0.38f;

                var cam = Camera.main;
                if (cam != null)
                    _plateRoot.rotation = Quaternion.LookRotation(_plateRoot.position - cam.transform.position);
            }
            catch { }
        }

        // ── Public API called by NetworkManager ──────────────────

        public void UpdateFromNetworkPacket(PlayerStatePacket packet)
        {
            if (!_initialized) return;

            int idx = (_bufHead + 1) % BUFFER_SIZE;
            _buf[idx] = new Snapshot
            {
                time = Time.realtimeSinceStartup,
                headPos = packet.headPos,
                headRot = packet.headRot,
                leftPos = packet.leftHandPos,
                leftRot = packet.leftHandRot,
                rightPos = packet.rightHandPos,
                rightRot = packet.rightHandRot,
            };
            _bufHead = idx;
            _bufCount = Mathf.Min(_bufCount + 1, BUFFER_SIZE);
            _hasAnySnapshot = true;
        }

        /// <summary>One-line health readout for the debug HUD: is pose data flowing, and how fresh is it.</summary>
        public string GetDebugStatus()
        {
            if (!_initialized) return "not initialized";
            if (!_hasAnySnapshot) return "no pose data yet";
            float ageMs = (Time.realtimeSinceStartup - _buf[_bufHead].time) * 1000f;
            string down = _enemy != null && _enemy.isDown ? "  DOWN" : "";
            return $"driving  last packet {ageMs:F0}ms{down}";
        }

        /// <summary>Remote player got back up, so stand the ghost up through the game's
        /// actual animation path — calling GetUp() directly just clears flags and skips
        /// the animation entirely, leaving a ragdolled body crumpled on the floor forever.
        /// StartGettingUp() un-ragdolls it, turns the animator back on, and plays the real
        /// get-up clip, which calls GetUp() itself when it's done.</summary>
        public void OnRemoteGetUp()
        {
            try
            {
                if (_enemy != null && _enemy.isDown && !GetBool(IsGettingUpField))
                {
                    _enemy.knockdownTimer = 0f;
                    _enemy.StartGettingUp();
                    MelonLogger.Msg("[Ghost] Remote got up — playing ghost get-up animation");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Ghost] OnRemoteGetUp: {e.Message}"); }
        }

        // ── Per-frame updates ─────────────────────────────────────

        private void Update()
        {
            // Normally BoxerController.Update regenerates dizzy at 108/s, but that's dead
            // here since EnemyController is disabled. Without this, every hit stacks
            // dizzy forever, and right after a knockdown the game clamps dizzy to
            // dizzyMax minus 1 — meaning the ghost is one point away from getting
            // instantly knocked down again on the very next punch. So we just do the
            // same decay ourselves.
            if (_enemy != null && _enemy.dizzyLevel > 0f)
                _enemy.dizzyLevel = Mathf.Max(0f, _enemy.dizzyLevel - _dizzyRegen * Time.deltaTime);

            // Same story with the knockdown timer — EnemyController would normally count
            // it down and stand the boxer up, but it's disabled. This is just a safety
            // net in case the remote's GET_UP packet gets lost somewhere: once the timer
            // hits zero we call StartGettingUp() ourselves.
            if (_enemy != null && _enemy.isDown && !_enemy.stayDown)
            {
                _enemy.knockdownTimer -= Time.deltaTime;
                if (_enemy.knockdownTimer <= 0f && !GetBool(IsGettingUpField) && !GetBool(IsKneelingField))
                {
                    try { _enemy.StartGettingUp(); } catch { }
                }
            }
        }

        private void LateUpdate()
        {
            UpdateNameplate();

            // Enemy-puppet path is driven from PuppetMaster.OnRead (DrivePuppet).
            if (_enemy != null) return;
            if (!_initialized || !_hasAnySnapshot || !_useBoneFallback) return;

            ComputeSmoothedPose();
            if (_headBone != null) { _headBone.position = _smoothHeadPos; _headBone.rotation = _smoothHeadRot; }
            if (_leftHandBone != null) { _leftHandBone.position = _smoothLeftPos; _leftHandBone.rotation = _smoothLeftRot; }
            if (_rightHandBone != null) { _rightHandBone.position = _smoothRightPos; _rightHandBone.rotation = _smoothRightRot; }
        }

        /// <summary>
        /// Runs inside PuppetMaster's update cycle, right where the AI's SolveIK used to —
        /// after the Animator pose is read, before muscles map onto it. Poses the
        /// animation target from network data via FullBodyBipedIK, and the physical
        /// puppet just follows along.
        /// </summary>
        private void DrivePuppet()
        {
            try
            {
                if (!_initialized || !_hasAnySnapshot || _enemy == null || _fbbik == null) return;

                // While knocked down let the KO/get-up animations own the body.
                if (_enemy.isDown)
                {
                    _enemy.RunBoxVelocityUpdates();
                    return;
                }

                ComputeSmoothedPose();

                // Root: stand under the remote head, face along the remote gaze yaw.
                Vector3 fwd = _smoothHeadRot * Vector3.forward;
                fwd.y = 0f;
                float yaw = fwd.sqrMagnitude > 0.001f ? Quaternion.LookRotation(fwd).eulerAngles.y : _lastYaw;
                _lastYaw = yaw;
                transform.position = new Vector3(_smoothHeadPos.x, transform.position.y, _smoothHeadPos.z);
                transform.rotation = Quaternion.Euler(0f, yaw, 0f);

                // Crouch/duck: offset the body effector by how far the remote head is below
                // the model's natural standing head height.
                float duck = Mathf.Clamp(_smoothHeadPos.y - (transform.position.y + _baseHeadHeight), -0.7f, 0.15f);
                try
                {
                    _fbbik.solver.bodyEffector.positionOffset += new Vector3(0f, duck * 0.7f, 0f);
                }
                catch { /* body effector optional — hands still track */ }

                // Hands are position-only IK, same as the game's own AI (SolveIK never
                // touches rotationWeight). We tried driving wrist rotation too and it
                // twisted the forearms badly in real testing — VR controller rotations
                // just don't map onto this rig's wrist joints. Animations own the wrists.
                var left = _fbbik.solver.leftHandEffector;
                left.position = _smoothLeftPos;
                left.positionWeight = 1f;
                left.rotationWeight = 0f;

                var right = _fbbik.solver.rightHandEffector;
                right.position = _smoothRightPos;
                right.positionWeight = 1f;
                right.rotationWeight = 0f;

                _fbbik.GetIKSolver().Update();

                if (_lik != null)
                {
                    _lik.solver.IKPosition = _smoothHeadPos + _smoothHeadRot * Vector3.forward;
                    _lik.solver.IKPositionWeight = 1f;
                    _lik.GetIKSolver().Update();
                }

                // The AI's SolveIK used to do this velocity bookkeeping, and we still need
                // it for punch force calculations to come out right against the ghost.
                _enemy.RunBoxVelocityUpdates();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Ghost] DrivePuppet error: {e.Message}");
            }
        }

        private void ComputeSmoothedPose()
        {
            float renderTime = Time.realtimeSinceStartup - BUFFER_DELAY;
            GetInterpolatedSnapshot(renderTime,
                out Vector3 headPos, out Quaternion headRot,
                out Vector3 leftPos, out Quaternion leftRot,
                out Vector3 rightPos, out Quaternion rightRot);

            // First real pose: snap straight to it so the model doesn't fly in from origin.
            if (!_smoothInitialized)
            {
                _smoothHeadPos = headPos; _smoothHeadRot = headRot;
                _smoothLeftPos = leftPos; _smoothLeftRot = leftRot;
                _smoothRightPos = rightPos; _smoothRightRot = rightRot;
                _prevLeftPos = leftPos; _prevRightPos = rightPos;
                _smoothInitialized = true;
            }

            float dt = Time.deltaTime > 0 ? Time.deltaTime : 0.016f;
            float leftVel = Vector3.Distance(leftPos, _prevLeftPos) / dt;
            float rightVel = Vector3.Distance(rightPos, _prevRightPos) / dt;
            _prevLeftPos = leftPos;
            _prevRightPos = rightPos;

            // Adaptive smoothing: fast punches get less smoothing so they stay snappy;
            // idle hands get maximum smoothness. A hard punch travels ~4-6 m/s.
            float leftSmooth = GetAdaptiveSmooth(leftVel, dt);
            float rightSmooth = GetAdaptiveSmooth(rightVel, dt);
            float headSmooth = 18f * dt;

            _smoothHeadPos = Vector3.Lerp(_smoothHeadPos, headPos, Mathf.Clamp01(headSmooth));
            _smoothHeadRot = Quaternion.Slerp(_smoothHeadRot, headRot, Mathf.Clamp01(headSmooth));
            _smoothLeftPos = Vector3.Lerp(_smoothLeftPos, leftPos, Mathf.Clamp01(leftSmooth));
            _smoothLeftRot = Quaternion.Slerp(_smoothLeftRot, leftRot, Mathf.Clamp01(leftSmooth));
            _smoothRightPos = Vector3.Lerp(_smoothRightPos, rightPos, Mathf.Clamp01(rightSmooth));
            _smoothRightRot = Quaternion.Slerp(_smoothRightRot, rightRot, Mathf.Clamp01(rightSmooth));
        }

        /// <summary>
        /// Returns interpolated pose for the given render time by finding the two
        /// snapshots that bracket it. If render time is behind all snapshots, returns
        /// the oldest; if ahead (buffer underrun), the newest.
        /// </summary>
        private void GetInterpolatedSnapshot(float renderTime,
            out Vector3 headPos, out Quaternion headRot,
            out Vector3 leftPos, out Quaternion leftRot,
            out Vector3 rightPos, out Quaternion rightRot)
        {
            if (_bufCount == 0)
            {
                headPos = leftPos = rightPos = Vector3.zero;
                headRot = leftRot = rightRot = Quaternion.identity;
                return;
            }

            if (_bufCount == 1)
            {
                ref Snapshot s = ref _buf[_bufHead];
                headPos = s.headPos; headRot = s.headRot;
                leftPos = s.leftPos; leftRot = s.leftRot;
                rightPos = s.rightPos; rightRot = s.rightRot;
                return;
            }

            int newer = _bufHead;
            int older = (_bufHead - 1 + BUFFER_SIZE) % BUFFER_SIZE;
            int checked_ = 0;

            while (checked_ < _bufCount - 1)
            {
                if (_buf[older].time <= renderTime && _buf[newer].time >= renderTime)
                    break;

                newer = older;
                older = (older - 1 + BUFFER_SIZE) % BUFFER_SIZE;
                checked_++;
            }

            ref Snapshot a = ref _buf[older];
            ref Snapshot b = ref _buf[newer];

            float span = b.time - a.time;
            float t = (span > 0.0001f) ? Mathf.Clamp01((renderTime - a.time) / span) : 1f;

            bool teleportHead = Vector3.Distance(a.headPos, b.headPos) > MAX_SNAP_DIST;
            bool teleportLeft = Vector3.Distance(a.leftPos, b.leftPos) > MAX_SNAP_DIST;
            bool teleportRight = Vector3.Distance(a.rightPos, b.rightPos) > MAX_SNAP_DIST;

            headPos = teleportHead ? b.headPos : Vector3.Lerp(a.headPos, b.headPos, t);
            leftPos = teleportLeft ? b.leftPos : Vector3.Lerp(a.leftPos, b.leftPos, t);
            rightPos = teleportRight ? b.rightPos : Vector3.Lerp(a.rightPos, b.rightPos, t);

            headRot = Quaternion.Slerp(a.headRot, b.headRot, t);
            leftRot = Quaternion.Slerp(a.leftRot, b.leftRot, t);
            rightRot = Quaternion.Slerp(a.rightRot, b.rightRot, t);
        }

        private static float GetAdaptiveSmooth(float velocityMps, float dt)
        {
            float speed = Mathf.Lerp(20f, 80f, Mathf.Clamp01(velocityMps / 6f));
            return speed * dt;
        }

        // ── Spawning / takeover ───────────────────────────────────

        /// <summary>
        /// Takes over whatever boxer BoutController put in the blue corner. Every machine
        /// spawns an AI there (player is always red, enemy always blue — hardcoded by the
        /// game), so this works the same way on both host and guest.
        /// </summary>
        public static GhostBoxer SpawnGhostBoxer(BoutController.Corner desiredCorner)
        {
            MelonLogger.Msg($"[Ghost] Taking over blue-corner boxer as network ghost (corner: {desiredCorner})");
            MelonLogger.Msg($"[Ghost] BoutRules.boxerResourceName = '{BoutRules.boxerResourceName ?? "(null)"}'");

            try
            {
                // ── Priority 1: the boxer BoutController already spawned at index 1
                if (BoutController.instance != null)
                {
                    var existingBoxers = BoutController.allBoxers;
                    if (existingBoxers != null && existingBoxers.Length > 1 && existingBoxers[1] != null)
                    {
                        var existingBlue = existingBoxers[1];
                        MelonLogger.Msg($"[Ghost] ✓ Using BoutController's blue boxer: {existingBlue.gameObject.name}");

                        var ghost = existingBlue.gameObject.GetComponent<GhostBoxer>()
                                 ?? existingBlue.gameObject.AddComponent<GhostBoxer>();
                        ghost._corner = BoutController.Corner.Blue;
                        ghost.Initialize();
                        if (!ghost._initialized) return null;
                        return ghost;
                    }
                }

                // ── Priority 2: Resources.Load (boxers[1] missing — e.g. rules had no boxer name)
                string name = !string.IsNullOrEmpty(BoutRules.boxerResourceName)
                    ? BoutRules.boxerResourceName
                    : "Hojo Mizushima";

                string[] pathsToTry =
                {
                    $"TotF/{name}",
                    name,
                    "TotF/Hojo Mizushima",
                    "Hojo Mizushima",
                };
                GameObject prefab = null;
                foreach (var path in pathsToTry)
                {
                    prefab = Resources.Load<GameObject>(path);
                    if (prefab != null) { MelonLogger.Msg($"[Ghost] ✓ Found prefab at: {path}"); break; }
                }

                if (prefab == null)
                {
                    // We don't fall back to cloning whatever's in the scene here on
                    // purpose — the only other boxer around is the player, and instantiating
                    // that prefab clobbers PlayerController.instance (its Awake/OnDestroy
                    // manage that singleton), which breaks the whole match.
                    MelonLogger.Error("[Ghost] ✗ No blue boxer and no loadable prefab — cannot create ghost");
                    return null;
                }

                GameObject ghostGO = Instantiate(prefab);
                ghostGO.name = "Ghost_" + name;
                var spawned = ghostGO.GetComponentInChildren<BoxerController>();
                if (spawned == null)
                {
                    MelonLogger.Error("[Ghost] ✗ Spawned prefab has no BoxerController");
                    Destroy(ghostGO);
                    return null;
                }

                var gb = spawned.gameObject.AddComponent<GhostBoxer>();
                gb._corner = BoutController.Corner.Blue;
                gb.Initialize();
                if (!gb._initialized) { Destroy(ghostGO); return null; }
                MelonLogger.Msg($"[Ghost] ✓ Ghost boxer spawned from prefab");
                return gb;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Ghost] SpawnGhostBoxer error: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        // ── Init ──────────────────────────────────────────────────

        private void Initialize()
        {
            BoxerInstance = gameObject;

            _enemy = GetComponentInChildren<EnemyController>(true) ?? GetComponent<EnemyController>();

            if (_enemy != null)
            {
                TakeOverEnemyPuppet();
            }
            else
            {
                // Unknown rig (no EnemyController): freeze its animation and drive bones directly.
                MelonLogger.Warning("[Ghost] No EnemyController on ghost — using direct bone fallback");
                foreach (var anim in GetComponentsInChildren<Animator>(true))
                    anim.enabled = false;

                FindBonesForFallback();
                _useBoneFallback = true;

                if (_headBone == null && _leftHandBone == null && _rightHandBone == null)
                {
                    MelonLogger.Error("[Ghost] ✗ Bone fallback failed — no head or hand bones found. Ghost will not track remote player.");
                    _initialized = false;
                    return;
                }
            }

            DisableAIDecisionMaking();
            NeutraliseOffense();

            _lastGhostTrauma = GetFloat(TraumaField);
            _lastGhostPain = GetFloat(PainField);
            _lastGhostDizzy = _enemy != null ? _enemy.dizzyLevel : 0f;

            // Boxer variants can tune dizzyRegen — read the real value off this rig.
            try
            {
                if (_enemy != null && DizzyRegenField != null)
                    _dizzyRegen = (float)DizzyRegenField.GetValue(_enemy);
            }
            catch { }

            _initialized = true;
            MelonLogger.Msg("[Ghost] ✓ Ghost initialized");
        }

        /// <summary>
        /// This is the fix that actually stops the AI from blocking. EnemyController
        /// registers SolveIK on PuppetMaster.OnRead, and that delegate keeps firing even
        /// after you disable the component — that's what kept raising its fists to guard.
        /// So we swap it out for our own network-driven pose delegate instead.
        /// </summary>
        private void TakeOverEnemyPuppet()
        {
            _fbbik = _enemy.ik ?? _enemy.GetComponent<FullBodyBipedIK>();
            _lik = _enemy.lik ?? _enemy.GetComponent<LookAtIK>();

            try
            {
                _pm = PmField?.GetValue(_enemy) as PuppetMaster;
                if (_pm == null)
                    _pm = _enemy.transform.parent != null
                        ? _enemy.transform.parent.GetComponentInChildren<PuppetMaster>()
                        : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Ghost] PuppetMaster lookup failed: {e.Message}");
            }

            if (_pm != null)
            {
                try
                {
                    _pm.OnRead = (PuppetMaster.UpdateDelegate)Delegate.Remove(
                        _pm.OnRead, new PuppetMaster.UpdateDelegate(_enemy.SolveIK));
                    _driveDelegate = new PuppetMaster.UpdateDelegate(DrivePuppet);
                    _pm.OnRead = (PuppetMaster.UpdateDelegate)Delegate.Combine(_pm.OnRead, _driveDelegate);
                    MelonLogger.Msg("[Ghost] ✓ Replaced AI SolveIK with network pose driver on PuppetMaster.OnRead");
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"[Ghost] ✗ Could not rewire PuppetMaster.OnRead: {e.Message}");
                }
            }
            else
            {
                MelonLogger.Error("[Ghost] ✗ PuppetMaster not found — ghost pose will not track the remote player");
            }

            try
            {
                _baseHeadHeight = _enemy.head != null
                    ? _enemy.head.position.y - transform.position.y
                    : 1.65f;
            }
            catch { }

            // Registers blocked punches properly when the remote's guard intercepts a fist.
            try { IsBlockingField?.SetValue(_enemy, true); } catch { }

            // Park the animator in its idle stance; with EnemyController disabled nothing
            // will ever push it into punch/walk states, but KO/get-up/celebrate still play.
            try { _enemy.bodyAnimation?.SetBool("Ready", true); } catch { }
        }

        /// <summary>Disables every AI decision-making script, keeping the puppet body alive.</summary>
        private void DisableAIDecisionMaking()
        {
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb == this) continue;
                string t = mb.GetType().Name;
                if (t.Contains("Enemy") || t.Contains("AI") || t.Contains("Behavior") || t.Contains("NavMesh"))
                {
                    mb.enabled = false;
                }
            }
            MelonLogger.Msg("[Ghost] ✓ AI decision scripts disabled");
        }

        /// <summary>
        /// Disables the ghost's offensive Hitbox COMPONENTS so it can never deal direct
        /// local damage (the remote's punches arrive as packets instead). Colliders stay
        /// enabled: blockboxes let the remote's raised guard physically block, and fist
        /// colliders give natural glove-on-glove contact.
        /// </summary>
        public void NeutraliseOffense()
        {
            try
            {
                int hitboxCount = 0;
                foreach (var hb in gameObject.GetComponentsInChildren<TotF.Hitbox>(true))
                {
                    hb.enabled = false;
                    hitboxCount++;
                }
                MelonLogger.Msg($"[Ghost] ✓ NeutraliseOffense: disabled {hitboxCount} Hitbox component(s); hurtboxes and blockboxes live");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Ghost] ✗ NeutraliseOffense error: {e.Message}");
            }
        }

        // Kept for callers that re-neutralise after round transitions.
        public void NeutraliseColliders() => NeutraliseOffense();

        // ── Helpers ───────────────────────────────────────────────

        private static readonly string[][] BoneNameSets =
        {
            new[] { "Head", "head", "Armature|Head", "Skeleton|Head" },
            new[] { "LeftHand", "Left Hand", "LeftFist", "Armature|LeftHand" },
            new[] { "RightHand", "Right Hand", "RightFist", "Armature|RightHand" }
        };

        private void FindBonesForFallback()
        {
            _headBone = FindBone(transform, BoneNameSets[0]);
            _leftHandBone = FindBone(transform, BoneNameSets[1]);
            _rightHandBone = FindBone(transform, BoneNameSets[2]);
        }

        private static Transform FindBone(Transform root, string[] names)
        {
            foreach (string n in names)
                if (root.name == n) return root;

            foreach (Transform child in root)
            {
                var r = FindBone(child, names);
                if (r != null) return r;
            }
            return null;
        }

        private bool GetBool(FieldInfo field)
        {
            if (field == null || _enemy == null) return false;
            try { return (bool)field.GetValue(_enemy); }
            catch { return false; }
        }

        private float GetFloat(FieldInfo field)
        {
            var bc = (BoxerController)_enemy ?? GetComponentInChildren<BoxerController>();
            if (bc == null || field == null) return 0f;
            try { return (float)field.GetValue(bc); }
            catch { return 0f; }
        }

        public void RegisterAsCornerBoxer(BoutController.Corner corner, bool replaceBoxerSlot)
        {
            try
            {
                _corner = corner;

                if (BoutController.instance == null)
                {
                    MelonLogger.Error("[Ghost] ✗ BoutController.instance is null — call after scene is fully loaded");
                    return;
                }

                Transform cornerStart = corner == BoutController.Corner.Blue
                    ? BoutController.instance.blueStart
                    : BoutController.instance.redStart;
                if (cornerStart != null)
                {
                    transform.position = cornerStart.position;
                    transform.rotation = cornerStart.rotation;
                }

                var boxersField = typeof(BoutController).GetField("<boxers>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (boxersField == null)
                {
                    MelonLogger.Error("[Ghost] ✗ Could not find <boxers>k__BackingField via reflection");
                    return;
                }

                BoxerController[] boxers = (BoxerController[])boxersField.GetValue(BoutController.instance);
                if (boxers == null || boxers.Length < 2)
                {
                    MelonLogger.Error($"[Ghost] ✗ boxers array is null or too short (length: {(boxers?.Length ?? 0)})");
                    return;
                }

                BoxerController ghostBoxerController = GetComponentInChildren<BoxerController>() ?? _enemy;
                if (ghostBoxerController == null)
                {
                    MelonLogger.Error("[Ghost] ✗ Ghost has no BoxerController component");
                    return;
                }

                ghostBoxerController.corner = corner;

                if (replaceBoxerSlot && corner == BoutController.Corner.Blue)
                {
                    var current = boxers[1];
                    if (current != null && current.gameObject != null && current.gameObject != gameObject
                        && current != ghostBoxerController)
                    {
                        current.gameObject.SetActive(false);
                        MelonLogger.Msg($"[Ghost] ✓ Disabled separate AI boxer at index 1: {current.gameObject.name}");
                    }

                    boxers[1] = ghostBoxerController;
                    boxersField.SetValue(BoutController.instance, boxers);
                }

                MelonLogger.Msg($"[Ghost] ✓ Registered as {corner} corner boxer");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Ghost] ✗ RegisterAsCornerBoxer error: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Wires the ghost's damage pipeline to the network:
        ///  - every hit that lands locally on the ghost forwards its trauma/pain/dizzy
        ///    DELTAS (plus the raw damage + pain threshold for scoring/haptics) so the
        ///    remote player's machine applies the identical hit to their real player;
        ///  - a local knockdown of the ghost (the puncher's machine is the authority for
        ///    knockdowns it caused) forwards a KNOCKDOWN with the floor time.
        /// The ghost's own damage fields keep accumulating so its knockdown thresholds
        /// track the remote player's true condition.
        /// </summary>
        public void HookGhostDamageForwarding()
        {
            try
            {
                var ghostBoxerController = GetComponentInChildren<BoxerController>() ?? (BoxerController)_enemy;
                if (ghostBoxerController == null)
                {
                    MelonLogger.Warning("[Ghost] ⚠ HookGhostDamageForwarding: no BoxerController found on ghost");
                    return;
                }

                ghostBoxerController.OnTakeDamage = (BoxerController.TakeDamageEvent)Delegate.Combine(
                    ghostBoxerController.OnTakeDamage,
                    new BoxerController.TakeDamageEvent((float damage, float painThreshold) =>
                    {
                        try
                        {
                            var nm = NetworkManager.Instance;
                            if (nm == null || !nm.IsConnected) return;

                            float trauma = GetFloat(TraumaField);
                            float pain = GetFloat(PainField);
                            float dizzy = ghostBoxerController.dizzyLevel;
                            float deltaTrauma = Mathf.Max(0f, trauma - _lastGhostTrauma);
                            float deltaPain = Mathf.Max(0f, pain - _lastGhostPain);
                            float deltaDizzy = Mathf.Max(0f, dizzy - _lastGhostDizzy);
                            _lastGhostTrauma = trauma;
                            _lastGhostPain = pain;
                            _lastGhostDizzy = dizzy;

                            var seq = MultiplayerPlugin.Instance?.GetNextPacketSeq() ?? 0;
                            var packet = PlayerStatePacket.CreateDamageEvent(
                                deltaTrauma, deltaPain, deltaDizzy, damage, painThreshold, seq);
                            nm.SendPlayerState(packet);
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[Ghost] Damage forward error: {ex.Message}");
                        }
                    }));

                ghostBoxerController.OnKnockdown = (BoxerController.KnockedDownEvent)Delegate.Combine(
                    ghostBoxerController.OnKnockdown,
                    new BoxerController.KnockedDownEvent(() =>
                    {
                        try
                        {
                            var nm = NetworkManager.Instance;
                            if (nm == null || !nm.IsConnected) return;

                            var seq = MultiplayerPlugin.Instance?.GetNextPacketSeq() ?? 0;
                            var packet = PlayerStatePacket.CreateKnockdown(
                                (int)_corner, ghostBoxerController.knockdownTimer, seq);
                            nm.SendPlayerState(packet);
                            MelonLogger.Msg("[Ghost] ✓ Knockdown on ghost — forwarded to remote");
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[Ghost] Knockdown forward error: {ex.Message}");
                        }
                    }));

                MelonLogger.Msg("[Ghost] ✓ Ghost damage forwarding hooked");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Ghost] ✗ HookGhostDamageForwarding error: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            // Unhook our delegate so a destroyed ghost can't leave a dangling callback.
            try
            {
                if (_pm != null && _driveDelegate != null)
                    _pm.OnRead = (PuppetMaster.UpdateDelegate)Delegate.Remove(_pm.OnRead, _driveDelegate);
            }
            catch { }
            MelonLogger.Msg("[Ghost] Ghost boxer destroyed");
        }
    }
}
