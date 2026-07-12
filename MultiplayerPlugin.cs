using MelonLoader;
using Steamworks;
using System;
using System.Collections;
using System.Reflection;
using TMPro;
using TotF;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(ToFMultiplayer.MultiplayerPlugin), "Thrill of the Fight Multiplayer", "1.0.0", "willpsdk")]
[assembly: MelonGame("Ian Fitz", "The Thrill of the Fight")]

namespace ToFMultiplayer
{
    public class MultiplayerPlugin : MelonMod
    {
        public static MultiplayerPlugin Instance { get; private set; }

        private static GameObject networkManagerHost;

        // ── Bout state
        internal bool multiplayerBoutActive = false;
        private bool isWaitingForPlayer = false;
        private GhostBoxer ghostBoxer;
        private uint packetSequenceNumber = 0;
        private bool boutListenerRegistered = false;
        private bool boutEndSent = false;

        // ── VR tracking
        private Transform _headTransform;
        private Transform _leftTransform;
        private Transform _rightTransform;
        private bool _trackingReady = false;

        // ── Ready-up state
        private bool localReadiedUp = false;
        private bool remoteReadiedUp = false;
        private bool readyUpPhaseActive = false;

        // ── Auditorium setup guard
        private bool _setupAuditoriumRunning = false;

        // ── Corner assignment
        private bool _cornerAssigned = false;
        private BoutController.Corner _assignedCorner = BoutController.Corner.Red;
        private object _cornerApplyCoroutine;

        // ── Break skip state
        private bool localBreakSkipVoted = false;
        private bool remoteBreakSkipVoted = false;
        private bool breakSkipPhaseActive = false;

        // ── Send throttle — avoid spamming packets at full framerate
        private float _sendInterval = 1f / 72f; // 72 Hz — matches typical VR refresh
        private float _lastSendTime = 0f;

        // ── UI
        private MultiplayerReadyUpTrigger readyUpTrigger;
        private bool _showMenu = false;
        private Rect _windowRect = new Rect(20, 20, 400, 560);
        private GUIStyle _titleStyle, _statusStyle, _labelStyle, _waitingStyle, _readyStyle;
        private GUIStyle _hudStyle, _hudHeaderStyle;
        private bool _stylesInitialized = false;
        private string _joinCodeInput = "";
        private Vector2 _lobbyScrollPos = Vector2.zero;
        private bool _showLobbyBrowser = false;
        private MultiplayerMenuManager _multiplayerMenuManager;

        // ── Debug mode (toggled from the F4 menu)
        // Turns on the diagnostics HUD and lets a host start alone — the ghost just
        // mirrors your own movements over network loopback, so you can test the puppet
        // pipeline solo without needing a second person.
        public static bool DebugMode = false;
        private float _fpsSmoothed = 72f;

        // The Queue-for-Match loop sets this while we're hosting a matchmaking lobby —
        // when someone connects, we start automatically instead of waiting for the host
        // to hit Start Lobby.
        public static bool AutoStartWhenReady = false;

        /// <summary>True when we're hosting in debug mode with no real opponent — the
        /// ghost is driven by mirrored local tracking instead of network packets.</summary>
        internal bool SoloDebugActive
        {
            get
            {
                var nm = NetworkManager.Instance;
                return DebugMode && nm != null && nm.IsHosting && !nm.IsConnected;
            }
        }

        // ─────────────────────────────────────────────────────────

        public override void OnApplicationStart()
        {
            Instance = this;
            MelonLogger.Msg("[Multiplayer] Thrill of the Fight Multiplayer Mod v1.0");
            MelonLogger.Msg("[Multiplayer] Press F4 to open/close the multiplayer menu");
            EnsureNetworkManagerExists();
            EnsureLobbyBrowserExists();
            MelonLogger.Msg("[Multiplayer] ✓ Initialized successfully!");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[Multiplayer] Scene loaded: {sceneName} (index: {buildIndex})");

            var nm = NetworkManager.Instance;
            bool connected = nm?.IsConnected ?? false;
            bool hosting = nm?.IsHosting ?? false;
            bool bothReady = nm?.BothPlayersReady ?? false;
            MelonLogger.Msg($"[Multiplayer] — IsConnected: {connected}, IsHosting: {hosting}, BothPlayersReady: {bothReady}");

            // Home menu lives in the "title" scene — the game bounces back here via
            // LevelLoader.LoadScene("title") after bouts and calibration. (Used to check
            // for "MainMenu" instead, which never matched, so the button never got made.)
            if (sceneName == "title" || sceneName == "MainMenu")
            {
                MelonLogger.Msg($"[Multiplayer] Home-menu scene '{sceneName}' detected, setting up multiplayer menu button...");
                MelonCoroutines.Start(SetupMainMenuButton());

                // Coming back from a multiplayer match — open the multiplayer menu instead
                // of dumping the player on the singleplayer one.
                if (_returnToMultiplayerMenu)
                {
                    _returnToMultiplayerMenu = false;
                    MelonCoroutines.Start(ReopenMultiplayerMenuAfterLoad());
                }
            }

            if (nm != null && nm.IsHosting &&
                (sceneName.Contains("Bout") || sceneName.Contains("Arena") || sceneName.Contains("Fight")))
            {
                MelonLogger.Error("[Multiplayer] ✗ Cannot start singleplayer fight while hosting!");
                SafeLoadScene("title");
                return;
            }

            if (!(sceneName.Contains("Bout") || sceneName.Contains("Arena") ||
                  sceneName.Contains("Fight") || sceneName == "Auditorium"))
            {
                FullBoutReset();
                MelonLogger.Msg("[Multiplayer] Reset bout state (scene changed)");
            }

            if (sceneName == "Auditorium")
            {
                _cornerAssigned = false;
                _assignedCorner = BoutController.Corner.Red;
                _localPlacementSettled = false;
                BlueCornerUI.Reset();
            }

            // Someone started a singleplayer fight while a lobby was still hanging around
            // (same scene as multiplayer). Leave it fully vanilla and just quietly close
            // the lobby so they're not secretly still hosting behind a solo match.
            if (sceneName == "Auditorium" && (connected || hosting) && !_multiplayerMatchPending)
            {
                MelonLogger.Msg("[Multiplayer] Auditorium loaded WITHOUT a multiplayer match pending — treating as singleplayer, leaving lobby");
                try
                {
                    if (hosting) nm.EndLobby();
                    else nm.Disconnect();
                }
                catch (Exception e) { MelonLogger.Warning($"[Multiplayer] Lobby teardown: {e.Message}"); }
                return;
            }

            if (sceneName == "Auditorium" && _multiplayerMatchPending && !multiplayerBoutActive)
            {
                _multiplayerMatchPending = false;   // one load per match start, then it's used up
                MelonLogger.Msg("[Multiplayer] Auditorium scene detected, setting up multiplayer...");
                ResetReadyPhaseState();
                nm?.ResetReadyState();
                nm?.ResetBreakSkipState();

                MelonCoroutines.Start(HideContinueButtonImmediate());

                // Solo debug hosts skip the waiting phase — the ghost is their own mirror,
                // so go straight to the ready-up setup as if an opponent were present.
                if (bothReady || SoloDebugActive)
                    MelonCoroutines.Start(SetupAuditorium(bothPlayersAlreadyConnected: true));
                else
                    MelonCoroutines.Start(SetupAuditorium(bothPlayersAlreadyConnected: false));
            }
        }

        private bool _localWasDown = false;

        // Set when a multiplayer fight starts; on the next return to the title scene the
        // multiplayer menu reopens automatically instead of the singleplayer fight menu.
        private static bool _returnToMultiplayerMenu;

        // Only the real multiplayer start paths set this (host's Start Lobby, or the
        // guest's START_MATCH -> CountdownThenLoadMatch) before loading the Auditorium.
        // Without it, just being in a lobby while starting a normal singleplayer fight
        // (same scene!) would hijack that fight into multiplayer setup.
        private static bool _multiplayerMatchPending;

        // The guest gets moved from red to blue shortly after the scene loads. We hold
        // off on pose streaming until that's done, so the opponent's ghost just appears
        // at the blue corner instead of visibly teleporting across the ring.
        private bool _localPlacementSettled;

        private IEnumerator ReopenMultiplayerMenuAfterLoad()
        {
            // SetupMainMenuButton (re)creates the menu manager — wait for it, then give
            // the home menu a beat to finish its own opening before taking over.
            float t = 0f;
            while (MultiplayerMenuManager.Instance == null && t < 10f)
            {
                yield return new WaitForSeconds(0.25f);
                t += 0.25f;
            }
            yield return new WaitForSeconds(0.5f);

            try
            {
                if (MultiplayerMenuManager.Instance != null)
                {
                    MultiplayerMenuManager.Instance.OpenMenu();
                    MelonLogger.Msg("[Multiplayer] ✓ Returned to multiplayer menu after match");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Multiplayer] ReopenMultiplayerMenuAfterLoad: {e.Message}");
            }
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F4))
                _showMenu = !_showMenu;

            if (Time.unscaledDeltaTime > 0f)
                _fpsSmoothed = Mathf.Lerp(_fpsSmoothed, 1f / Time.unscaledDeltaTime, 0.05f);

            NetworkManager.Instance?.ReceivePackets();

            if (_cornerAssigned && PlayerController.instance != null)
            {
                if (PlayerController.instance.corner != _assignedCorner)
                    PlayerController.instance.corner = _assignedCorner;
            }

            var netMgr = NetworkManager.Instance;
            bool connected = netMgr != null && netMgr.IsConnected;

            // Tell the remote when our player finishes a knockdown and stands back up,
            // so their ghost (which represents us) stands up in sync.
            if (connected && multiplayerBoutActive)
            {
                try
                {
                    var localBoxer = BoutController.instance != null ? BoutController.allBoxers[0] : null;
                    if (localBoxer != null)
                    {
                        if (_localWasDown && !localBoxer.isDown)
                            netMgr.SendGetUp();
                        _localWasDown = localBoxer.isDown;
                    }
                }
                catch { /* bout tearing down */ }
            }

            // Stream our pose any time the ghost exists — ready-up, rounds, breaks, all of
            // it — so the opponent is always live, not just mid-round. No remote in solo
            // debug, so we just loop the same packets back into our own ghost.
            bool soloDebug = SoloDebugActive;
            if ((!connected && !soloDebug) || !_trackingReady || ghostBoxer == null)
                return;

            // As the guest, hold off streaming pose until we're actually placed at the
            // blue corner — otherwise the opponent watches our ghost walk across the ring.
            // Once the bout's active we stop caring (placement's done by then anyway, or
            // we lost the assignment packet and streaming matters more than the visual).
            if (!soloDebug && netMgr != null && !netMgr.IsHost
                && !_localPlacementSettled && !multiplayerBoutActive)
                return;

            // Throttle sends to 72 Hz instead of full framerate.
            float now = Time.realtimeSinceStartup;
            if (now - _lastSendTime < _sendInterval) return;
            _lastSendTime = now;

            try
            {
                var packet = PlayerStatePacket.CreatePositionUpdate(
                    _headTransform.position,
                    _headTransform.rotation,
                    _leftTransform.position,
                    _leftTransform.rotation,
                    _rightTransform.position,
                    _rightTransform.rotation,
                    false, Vector3.zero, 0,
                    packetSequenceNumber++);

                if (connected)
                    netMgr.SendPlayerState(packet);
                else
                    LoopbackToGhost(packet);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Multiplayer] OnUpdate error: {e.Message}");
            }
        }

        /// <summary>
        /// Solo debug: feeds your own tracking back into the ghost, flipped 180° around
        /// the ring center so it stands across from you copying your movements like a
        /// mirror. Goes through the exact same snapshot/IK path a real opponent would.
        /// </summary>
        private void LoopbackToGhost(PlayerStatePacket packet)
        {
            if (ghostBoxer == null) return;

            Vector3 center;
            var bc = BoutController.instance;
            if (bc != null && bc.redStart != null && bc.blueStart != null)
                center = (bc.redStart.position + bc.blueStart.position) * 0.5f;
            else
                center = packet.headPos + Vector3.ProjectOnPlane(packet.headRot * Vector3.forward, Vector3.up).normalized;

            Quaternion flip = Quaternion.AngleAxis(180f, Vector3.up);

            // A plain 180° rotation keeps the pose rigid — no chirality flip — so this
            // stays correct: jab with your left, the ghost jabs with its left too.
            packet.headPos = center + flip * (packet.headPos - center);
            packet.headRot = flip * packet.headRot;
            packet.leftHandPos = center + flip * (packet.leftHandPos - center);
            packet.leftHandRot = flip * packet.leftHandRot;
            packet.rightHandPos = center + flip * (packet.rightHandPos - center);
            packet.rightHandRot = flip * packet.rightHandRot;

            ghostBoxer.UpdateFromNetworkPacket(packet);
        }

        public override void OnGUI()
        {
            if (!_stylesInitialized && Event.current.type == EventType.Layout) InitStyles();
            if (!_stylesInitialized) return;

            if (_showMenu)
                _windowRect = GUI.Window(0x70F0, _windowRect, DrawMenuWindow, "");

            // The desktop mirror stays clean during normal play — all real UI lives
            // in VR (menu screens, corner button). The status overlays only draw on
            // the PC screen in debug mode, where they're diagnostics.
            if (!DebugMode) return;

            DrawDebugHud();

            if (isWaitingForPlayer)
            {
                GUI.Label(new Rect(0, Screen.height / 2f - 50, Screen.width, 100),
                          "Waiting For Opponent To Join...", _waitingStyle);
            }

            if (readyUpPhaseActive || breakSkipPhaseActive)
                DrawReadyOverlay();
        }

        // ── Diagnostics HUD (debug mode)

        private void DrawDebugHud()
        {
            var nm = NetworkManager.Instance;

            string status;
            Color statusColor;
            if (SoloDebugActive && ghostBoxer != null) { status = "SOLO DEBUG (ghost mirrors you)"; statusColor = Color.magenta; }
            else if (SoloDebugActive) { status = "SOLO DEBUG (hosting, no opponent)"; statusColor = Color.magenta; }
            else if (nm != null && nm.IsConnected) { status = nm.IsHost ? "CONNECTED (host)" : "CONNECTED (guest)"; statusColor = Color.green; }
            else if (nm != null && nm.IsHosting) { status = "HOSTING (waiting)"; statusColor = Color.yellow; }
            else { status = "OFFLINE"; statusColor = Color.gray; }

            string ping = nm != null && nm.IsConnected
                ? (nm.PingMs >= 0f ? $"{nm.PingMs:F0} ms" : "measuring...")
                : "—";
            string traffic = nm != null ? $"{nm.PacketsSentPerSec}/s out   {nm.PacketsRecvPerSec}/s in" : "—";
            string ghost = ghostBoxer != null ? ghostBoxer.GetDebugStatus() : "none";

            const float w = 300f, h = 168f;
            var box = new Rect(Screen.width - w - 12f, 12f, w, h);
            GUI.Box(box, GUIContent.none);

            float y = box.y + 6f;
            _hudHeaderStyle.normal.textColor = statusColor;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 20f), $"◆ {status}", _hudHeaderStyle); y += 24f;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 18f), $"FPS      {_fpsSmoothed:F0}", _hudStyle); y += 19f;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 18f), $"Ping     {ping}", _hudStyle); y += 19f;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 18f), $"Traffic  {traffic}", _hudStyle); y += 19f;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 18f), $"Ghost    {ghost}", _hudStyle); y += 19f;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 18f), $"Bout     {(multiplayerBoutActive ? "active" : "idle")}   Corner {_assignedCorner}", _hudStyle); y += 19f;
            GUI.Label(new Rect(box.x + 10f, y, w - 20f, 18f), $"Scene    {SceneManager.GetActiveScene().name}", _hudStyle);
        }

        // ── Overlays

        private void DrawReadyOverlay()
        {
            bool localDone = breakSkipPhaseActive ? localBreakSkipVoted : localReadiedUp;
            bool remoteDone = breakSkipPhaseActive ? remoteBreakSkipVoted : remoteReadiedUp;

            string label = breakSkipPhaseActive ? "Skip Break" : "Ready Up";
            string youText = localDone ? $"✓ {label.ToUpper()}" : $"Hold to {label}";
            string oppText = remoteDone ? $"✓ {label.ToUpper()}" : "Waiting...";

            // Scale UI dynamically for VR fallback overlay if needed
            GUI.Label(new Rect(0, Screen.height * 0.08f, Screen.width * 0.5f, 60),
                      $"YOU: {youText}", _readyStyle);
            GUI.Label(new Rect(Screen.width * 0.5f, Screen.height * 0.08f, Screen.width * 0.5f, 60),
                      $"OPPONENT: {oppText}", _readyStyle);
        }

        // ── Auditorium setup

        private IEnumerator SetupAuditorium(bool bothPlayersAlreadyConnected)
        {
            if (_setupAuditoriumRunning && bothPlayersAlreadyConnected)
            {
                MelonLogger.Msg("[Multiplayer] Waiting for previous SetupAuditorium pass to finish...");
                yield return new WaitUntil(() => !_setupAuditoriumRunning);
            }

            _setupAuditoriumRunning = true;
            yield return new WaitForSeconds(1f);

            var nm = NetworkManager.Instance;
            if (nm == null) { MelonLogger.Error("[Multiplayer] ✗ NetworkManager null"); _setupAuditoriumRunning = false; yield break; }

            // The client has no idea which corner it's in until the host's corner
            // assignment packet shows up, and that can land a beat after we reach Phase 2
            // depending on ping. Without this wait the client would build its ready-up
            // menu and quit trigger still assuming the default Red corner.
            if (bothPlayersAlreadyConnected && !nm.IsHost && !_cornerAssigned)
            {
                MelonLogger.Msg("[Multiplayer] Waiting for corner assignment from host...");
                float waited = 0f;
                const float cornerWaitTimeout = 5f;
                while (!_cornerAssigned && waited < cornerWaitTimeout)
                {
                    yield return new WaitForSeconds(0.1f);
                    waited += 0.1f;
                }

                if (_cornerAssigned)
                    MelonLogger.Msg($"[Multiplayer] ✓ Corner assignment received: {_assignedCorner}");
                else
                    MelonLogger.Warning("[Multiplayer] ⚠ Corner assignment did not arrive within 5s — " +
                        "proceeding with default Red corner UI (will be wrong if the host actually placed you in Blue)");
            }

            try
            {
                if (!bothPlayersAlreadyConnected)
                {
                    // Don't spawn the ghost yet — that used to happen here and left the AI
                    // boxer just standing there uncontrolled while the host waited alone.
                    // It's created later, once someone actually joins.
                    MelonLogger.Msg("[Multiplayer] PHASE 1: Waiting period setup — no ghost spawned yet");
                    HideContinueButton();
                    isWaitingForPlayer = true; // show "Waiting For Opponent" overlay
                    if (!_trackingReady) MelonCoroutines.Start(FindTrackingWithRetry());
                    multiplayerBoutActive = false;
                    MelonLogger.Msg("[Multiplayer] ✓ Waiting period ready — host is in corner");
                }
                else
                {
                    MelonLogger.Msg(SoloDebugActive
                        ? "[Multiplayer] PHASE 2 (SOLO DEBUG): ghost mirrors local player — enabling Ready Up"
                        : "[Multiplayer] PHASE 2: Both players connected — enabling Ready Up");

                    if (!SoloDebugActive && (!nm.IsConnected || !nm.BothPlayersReady))
                    {
                        MelonLogger.Warning("[Multiplayer] ⚠ Lost connection before Ready Up phase");
                        _setupAuditoriumRunning = false;
                        yield break;
                    }

                    // Second player's here now, so spawn the ghost
                    if (ghostBoxer == null) SpawnAndRegisterGhost(nm);
                    if (!_trackingReady) MelonCoroutines.Start(FindTrackingWithRetry());

                    if (nm.IsHost)
                    {
                        _assignedCorner = BoutController.Corner.Red;
                        _cornerAssigned = true;
                        EnsureCornerAssignmentApplied();
                        nm.SendCornerAssignment(BoutController.Corner.Blue);
                    }

                    RegisterBoutListeners();

                    EnableReadyUpButton(MultiplayerReadyUpTrigger.TriggerMode.ReadyUp);
                    readyUpPhaseActive = true;
                    isWaitingForPlayer = false;

                    MelonLogger.Msg("[Multiplayer] ✓ Ready Up button enabled");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ SetupAuditorium error: {e.Message}\n{e.StackTrace}");
            }

            _setupAuditoriumRunning = false;
        }

        // ── Ghost boxer helpers

        private void SpawnAndRegisterGhost(NetworkManager nm)
        {
            MelonLogger.Msg("[Multiplayer] Spawning ghost boxer...");

            // Slot 0 is always local player (red) and slot 1 is always the AI (blue) —
            // that's hardcoded by the game on every machine. So the ghost always takes
            // the blue slot, guest included (used to only do this for the host, which is
            // why the guest was fighting the actual AI). Position comes from network
            // packets, so it shows up wherever the remote player really is.
            var gb = GhostBoxer.SpawnGhostBoxer(BoutController.Corner.Blue);
            if (gb == null) { MelonLogger.Error("[Multiplayer] ✗ Ghost spawn failed"); return; }

            ghostBoxer = gb;
            nm.SetGhostBoxer(gb);
            gb.RegisterAsCornerBoxer(BoutController.Corner.Blue, replaceBoxerSlot: true);

            gb.HookGhostDamageForwarding();

            // Nameplate above the ghost so you can see who you're actually fighting.
            try
            {
                string plate;
                if (SoloDebugActive) plate = "MIRROR (DEBUG)";
                else
                {
                    string name = SteamFriends.GetFriendPersonaName(nm.RemotePlayerID);
                    plate = nm.OpponentElo > 0f ? $"{name}   ·   {nm.OpponentElo:F0}" : name;
                }
                gb.SetNameplate(plate);
            }
            catch (Exception e) { MelonLogger.Warning($"[Multiplayer] Nameplate: {e.Message}"); }

            MelonLogger.Msg("[Multiplayer] ✓ Ghost boxer ready");

            MelonCoroutines.Start(NeutraliseGhostCollidersDelayed(gb));
        }

        private System.Collections.IEnumerator NeutraliseGhostCollidersDelayed(GhostBoxer gb)
        {
            yield return null;
            yield return null;
            yield return null;

            if (gb == null) yield break;
            MelonLogger.Msg("[Multiplayer] Running delayed collider neutralisation...");
            gb.NeutraliseColliders();
        }

        private IEnumerator FindTrackingWithRetry()
        {
            float elapsed = 0f;
            const float timeout = 30f;
            const float interval = 0.5f;

            while (!_trackingReady && elapsed < timeout)
            {
                var pc = PlayerController.instance;
                if (pc != null)
                {
                    Transform head = pc.hmd?.transform;
                    Transform left = pc.LeftHandTarget?.transform;
                    Transform right = pc.RightHandTarget?.transform;

                    if (head != null && left != null && right != null)
                    {
                        _headTransform = head;
                        _leftTransform = left;
                        _rightTransform = right;
                        _trackingReady = true;
                        MelonLogger.Msg("[Multiplayer] ✓ VR tracking acquired via PlayerController — position sync enabled");
                        MelonLogger.Msg($"[Multiplayer]   Head : {_headTransform.gameObject.name}");
                        MelonLogger.Msg($"[Multiplayer]   Left : {_leftTransform.gameObject.name}");
                        MelonLogger.Msg($"[Multiplayer]   Right: {_rightTransform.gameObject.name}");
                        yield break;
                    }
                }
                yield return new WaitForSeconds(interval);
                elapsed += interval;
            }

            if (!_trackingReady)
                MelonLogger.Warning("[Multiplayer] ⚠ VR tracking not found after 30s — position sync disabled");
        }

        // ── BoutController event subscription

        private void RegisterBoutListeners()
        {
            if (boutListenerRegistered) return;

            var nm = NetworkManager.Instance;
            if (!nm.IsHost) { MelonLogger.Msg("[Multiplayer] Guest: skipping bout listener (host drives it)"); return; }

            if (BoutController.instance == null)
            {
                MelonLogger.Warning("[Multiplayer] ⚠ BoutController.instance null — can't register listeners");
                return;
            }

            try
            {
                BoutController.instance.OnTimerUpdate +=
                    (bool startCounting, bool isRound, float duration, int onRound, int numberOfRounds) =>
                    {
                        if (startCounting && isRound)
                        {
                            MelonLogger.Msg($"[Multiplayer] Host: ROUND {onRound} started — notifying guest");
                            NetworkManager.Instance?.SendRoundStart(onRound);
                            ResetBreakSkipState();
                        }
                        else if (startCounting && !isRound)
                        {
                            MelonLogger.Msg($"[Multiplayer] Host: BREAK started ({duration:F0}s) — notifying guest");
                            NetworkManager.Instance?.SendBreakStart(duration);
                            MelonCoroutines.Start(EnableBreakSkipButtonDelayed());
                        }
                        else if (!startCounting && isRound)
                        {
                            bool boutJustEnded = BoutController.instance != null
                                                 && !BoutController.IsFightHappening();

                            if (boutJustEnded && !boutEndSent)
                            {
                                boutEndSent = true;
                                MelonLogger.Msg($"[Multiplayer] Host: BOUT ENDED on round {onRound} — sending BOUT_END to guest");
                                MelonCoroutines.Start(SendBoutEndPacket());
                            }
                            else if (!boutJustEnded)
                            {
                                MelonLogger.Msg($"[Multiplayer] Host: ROUND {onRound} ended — notifying guest");
                                NetworkManager.Instance?.SendRoundEnd(onRound);
                            }
                        }
                    };

                boutListenerRegistered = true;
                MelonLogger.Msg("[Multiplayer] ✓ BoutController.OnTimerUpdate listener registered (host)");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ RegisterBoutListeners error: {e.Message}");
            }
        }

        private bool _damageHooksApplied = false;
        private float _lastLocalTrauma;
        private float _lastLocalPain;
        private float _lastLocalDizzy;

        // Logged loudly if missing so game-update breakage is obvious instead of silent.
        // (dizzyLevel is a public PROPERTY on BoxerController, not a field — read directly.)
        private static readonly FieldInfo TraumaField = typeof(BoxerController).GetField("traumaDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo PainField = typeof(BoxerController).GetField("painDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        internal void HookLocalPlayerDamageEvents()
        {
            if (_damageHooksApplied) return;

            if (TraumaField == null) MelonLogger.Error("[Multiplayer] ✗ 'traumaDamage' field not found on BoxerController — damage sync broken. Game may have updated.");
            if (PainField == null) MelonLogger.Error("[Multiplayer] ✗ 'painDamage' field not found on BoxerController — damage sync broken.");
            if (TraumaField == null || PainField == null) return; // can't hook usefully without these

            try
            {
                var localBoxer = BoutController.allBoxers[0];
                if (localBoxer == null)
                {
                    MelonLogger.Warning("[Multiplayer] ⚠ boxers[0] is null — damage hooks skipped");
                    return;
                }

                // Just a safety net — the ghost has no live hitboxes so this shouldn't
                // normally fire, but if the local player somehow takes real damage,
                // forward it. Suppressed while we're applying remote damage so it can't echo.
                localBoxer.OnTakeDamage = (BoxerController.TakeDamageEvent)Delegate.Combine(
                    localBoxer.OnTakeDamage,
                    new BoxerController.TakeDamageEvent((float damage, float painThreshold) =>
                    {
                        try
                        {
                            var nm = NetworkManager.Instance;
                            if (nm == null || !nm.IsConnected || nm.ApplyingRemoteDamage) return;
                            float trauma = GetFloatField(localBoxer, TraumaField);
                            float pain = GetFloatField(localBoxer, PainField);
                            float dizzy = localBoxer.dizzyLevel;
                            float deltaTrauma = Mathf.Max(0f, trauma - _lastLocalTrauma);
                            float deltaPain = Mathf.Max(0f, pain - _lastLocalPain);
                            float deltaDizzy = Mathf.Max(0f, dizzy - _lastLocalDizzy);
                            _lastLocalTrauma = trauma;
                            _lastLocalPain = pain;
                            _lastLocalDizzy = dizzy;
                            var seq = GetNextPacketSeq();
                            var packet = PlayerStatePacket.CreateDamageEvent(
                                deltaTrauma, deltaPain, deltaDizzy, damage, painThreshold, seq);
                            nm.SendPlayerState(packet);
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[Multiplayer] OnTakeDamage send error: {ex.Message}");
                        }
                    }));

                // Knockdowns of the local player normally come FROM the remote side (the
                // puncher's machine sees it happen on their ghost first), so skip sending
                // this if we're just applying a knockdown packet we already got.
                localBoxer.OnKnockdown = (BoxerController.KnockedDownEvent)Delegate.Combine(
                    localBoxer.OnKnockdown,
                    new BoxerController.KnockedDownEvent(() =>
                    {
                        try
                        {
                            var nm = NetworkManager.Instance;
                            if (nm == null || !nm.IsConnected || nm.ApplyingRemoteKnockdown) return;
                            var seq = GetNextPacketSeq();
                            var packet = PlayerStatePacket.CreateKnockdown(
                                (int)BoutController.Corner.Red, localBoxer.knockdownTimer, seq);
                            nm.SendPlayerState(packet);
                            MelonLogger.Msg("[Multiplayer] Sent KNOCKDOWN packet to remote");
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[Multiplayer] OnKnockdown send error: {ex.Message}");
                        }
                    }));

                _damageHooksApplied = true;
                _lastLocalTrauma = GetFloatField(localBoxer, TraumaField);
                _lastLocalPain = GetFloatField(localBoxer, PainField);
                _lastLocalDizzy = localBoxer.dizzyLevel;
                MelonLogger.Msg("[Multiplayer] ✓ Hooked local player damage/knockdown events");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ Failed to hook damage events: {e.Message}");
            }
        }

        private static float GetFloatField(BoxerController boxer, FieldInfo field)
        {
            if (boxer == null || field == null) return 0f;
            try { return (float)field.GetValue(boxer); }
            catch { return 0f; }
        }

        public uint GetNextPacketSeq() => packetSequenceNumber++;

        private IEnumerator EnableBreakSkipButtonDelayed()
        {
            yield return new WaitForSeconds(2.5f);

            ResetBreakSkipState();
            NetworkManager.Instance?.ResetBreakSkipState();
            EnableReadyUpButton(MultiplayerReadyUpTrigger.TriggerMode.BreakSkip);
            breakSkipPhaseActive = true;
            MelonLogger.Msg("[Multiplayer] ✓ Break-skip button enabled");
        }

        private IEnumerator SendBoutEndPacket()
        {
            yield return null;
            DoSendBoutEndPacket();
        }

        private void DoSendBoutEndPacket()
        {
            try
            {
                var nm = NetworkManager.Instance;
                if (nm == null || !nm.IsConnected) return;

                int winner = (int)BoutResults.winner;
                int winCond = (int)BoutResults.winCondition;
                int wentToRound = BoutResults.wentToRound;
                int redScored = BoutResults.redScoredCount;
                int blueScored = BoutResults.blueScoredCount;
                int drawScored = BoutResults.drawScoredCount;

                int celebrateIndex = UnityEngine.Random.Range(0, 4);

                nm.SendBoutEnd(winner, winCond, wentToRound,
                               redScored, blueScored, drawScored,
                               celebrateIndex);

                // Host frame: Red = host = us.
                EloManager.OnMatchEnd(
                    won: winner == (int)BoutResults.Winner.Red,
                    draw: winner == (int)BoutResults.Winner.Draw);

                // The bout is over on this side too — without this the host's rematch
                // reload would skip Auditorium setup (it requires !multiplayerBoutActive).
                multiplayerBoutActive = false;
                breakSkipPhaseActive = false;
                readyUpPhaseActive = false;

                MelonLogger.Msg($"[Multiplayer] Host: BOUT_END sent — winner={winner} cond={winCond} wentToRound={wentToRound} celebrate={celebrateIndex}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ SendBoutEndPacket error: {e.Message}");
            }
        }

        public static void OnRemoteBoutEnd(PlayerStatePacket packet)
        {
            if (Instance == null) return;
            MelonCoroutines.Start(Instance.ReplicateBoutEndCeremony(packet));
        }

        public static void OnRemoteRetire()
        {
            if (Instance == null) return;
            MelonLogger.Msg("[Multiplayer] Remote retired — calling BoutController.Retire() locally");
            EloManager.OnMatchEnd(won: true);   // opponent quit mid-bout = our win
            Instance.multiplayerBoutActive = false;
            try { BoutController.Retire(); }
            catch (Exception e) { MelonLogger.Error($"[Multiplayer] ✗ OnRemoteRetire: {e.Message}"); }
        }

        private IEnumerator OnLocalRetire()
        {
            MelonLogger.Msg("[Multiplayer] Local player retiring — notifying remote...");
            EloManager.OnMatchEnd(won: false);  // quitting mid-bout counts as a loss
            NetworkManager.Instance?.SendRetire();

            yield return new WaitForSeconds(0.4f);

            multiplayerBoutActive = false;
            MelonLogger.Msg("[Multiplayer] Calling BoutController.Retire() locally after notify");
            try { BoutController.Retire(); }
            catch (Exception e) { MelonLogger.Error($"[Multiplayer] ✗ OnLocalRetire: {e.Message}"); }
        }

        private bool _quitTriggerHooked = false;

        private void HookQuitTrigger()
        {
            if (_quitTriggerHooked) return;

            try
            {
                var qt = BlueCornerUI.GetQuitTrigger(_assignedCorner);
                if (qt == null)
                {
                    MelonLogger.Warning("[Multiplayer] ⚠ QuitTrigger not found — exit sync disabled");
                    return;
                }

                qt.enabled = false;

                var synced = qt.gameObject.AddComponent<SyncedQuitTrigger>();
                synced.ProgressBar = qt.ProgressBar;
                synced.Plugin = this;

                _quitTriggerHooked = true;
                MelonLogger.Msg("[Multiplayer] ✓ QuitTrigger replaced with SyncedQuitTrigger");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ HookQuitTrigger: {e.Message}");
            }
        }

        private IEnumerator ReplicateBoutEndCeremony(PlayerStatePacket packet)
        {
            int hostWinner = (int)packet.traumaDamage;
            int winCondition = (int)packet.painDamage;
            int wentToRound = (int)packet.roundData;
            int hostRedScored = (int)packet.dizzyLevel;
            int hostBlueScored = (int)packet.headPos.x;
            int drawScored = (int)packet.headPos.y;
            int celebrateIndex = (int)packet.headPos.z;

            // The packet is in the HOST's frame (Red = host, Blue = guest). On this
            // machine the LOCAL player occupies the Red slot and the ghost (= host) the
            // Blue slot, so flip Red<->Blue or the wrong boxer celebrates and the win
            // screen credits the wrong name.
            int winner = hostWinner == (int)BoutResults.Winner.Red ? (int)BoutResults.Winner.Blue
                       : hostWinner == (int)BoutResults.Winner.Blue ? (int)BoutResults.Winner.Red
                       : hostWinner;
            int redScored = hostBlueScored;
            int blueScored = hostRedScored;

            MelonLogger.Msg($"[Multiplayer] Guest: Replicating bout end — hostWinner={hostWinner} → localWinner={winner}, cond={winCondition}, wentToRound={wentToRound}");

            // Local frame: Red = the local player = us.
            EloManager.OnMatchEnd(
                won: winner == (int)BoutResults.Winner.Red,
                draw: winner == (int)BoutResults.Winner.Draw);

            multiplayerBoutActive = false;
            breakSkipPhaseActive = false;
            readyUpPhaseActive = false;
            if (readyUpTrigger != null) readyUpTrigger.enabled = false;

            yield return null;

            bool setupOk = ApplyBoutEndSetup(winner, winCondition, wentToRound,
                                             redScored, blueScored, drawScored, celebrateIndex);
            if (!setupOk) yield break;

            MelonLogger.Msg("[Multiplayer] Guest: Waiting 5s to mirror host PostMatchSetup timing...");
            yield return new WaitForSeconds(5f);

            InvokePostMatchSetupAction();
        }

        private bool ApplyBoutEndSetup(int winner, int winCondition, int wentToRound,
                                       int redScored, int blueScored, int drawScored,
                                       int celebrateIndex)
        {
            try
            {
                BoutResults.winner = (BoutResults.Winner)winner;
                BoutResults.winCondition = (BoutResults.WinCondition)winCondition;
                BoutResults.wentToRound = wentToRound;
                BoutResults.redScoredCount = redScored;
                BoutResults.blueScoredCount = blueScored;
                BoutResults.drawScoredCount = drawScored;
                BoutResults.showScore = true;
                BoutResults.justFinishedBout = false;
                MelonLogger.Msg("[Multiplayer] Guest: BoutResults written");

                int winnerSlot = (winner == 1) ? 0 : 1;
                var winnerBoxer = BoutController.allBoxers[winnerSlot];
                if (winnerBoxer != null)
                {
                    try
                    {
                        var animField = typeof(BoxerController).GetField("animator",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var anim = animField?.GetValue(winnerBoxer) as Animator;
                        if (anim != null)
                        {
                            int safeOther = (celebrateIndex + 1) % 4;
                            anim.SetInteger("Post Celebrate Index", safeOther);
                            MelonLogger.Msg($"[Multiplayer] Guest: Pre-seeded celebrate index (target={celebrateIndex}, guard={safeOther})");
                        }
                    }
                    catch (Exception animEx)
                    {
                        MelonLogger.Warning($"[Multiplayer] Guest: Couldn't pre-seed animator: {animEx.Message}");
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ ApplyBoutEndSetup error: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// PlayerController.PostMatchMovement always sends the player to the red-corner
        /// winner spot (red is hardcoded as "the local player's corner" on every machine)
        /// and puts the exit menu right there. For the guest, who fought from blue, that
        /// means the results/exit UI shows up across the ring. This watcher waits for the
        /// exit menu to spawn — decision, KO, TKO, retire, doesn't matter which — and
        /// moves both the player and the menu over to blue.
        /// </summary>
        // Bumped on every fight start so an old watcher left over from a previous match
        // (MelonCoroutines survive scene loads) knows it's stale and bails out.
        private int _postMatchGen;

        private System.Collections.IEnumerator WatchPostMatchPlacement()
        {
            int gen = ++_postMatchGen;

            var pc = PlayerController.instance;
            if (pc == null || pc.exitMenu == null) yield break;
            string cloneName = pc.exitMenu.name + "(Clone)";

            GameObject menu = null;
            while (menu == null)
            {
                yield return new WaitForSeconds(0.5f);
                if (gen != _postMatchGen) yield break;              // superseded by a rematch
                if (BoutController.instance == null) yield break;   // scene unloaded
                menu = GameObject.Find(cloneName);
            }

            if (_assignedCorner == BoutController.Corner.Blue)
            {
                try
                {
                    var ring = RingController.instance;
                    if (ring != null && ring.blueCornerWinnerPosition != null)
                    {
                        PlayAreaController.SendPlayerToPosition(
                            ring.blueCornerWinnerPosition.position,
                            ring.blueCornerWinnerPosition.rotation.eulerAngles,
                            PlayAreaController.PlayAreaEdge.Center,
                            PlayAreaController.PlayAreaEdge.Left);

                        menu.transform.position = PlayAreaController.GetPosition(
                            PlayAreaController.PlayAreaEdge.Front, PlayAreaController.PlayAreaEdge.Right);
                        var player = PlayerController.instance;
                        if (player != null)
                            menu.transform.rotation = Quaternion.LookRotation(
                                player.transform.position - menu.transform.position);

                        MelonLogger.Msg("[Multiplayer] ✓ Post-match placement corrected to BLUE side");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[Multiplayer] WatchPostMatchPlacement: {e.Message}");
                }
            }

            SpawnRematchButton(menu);
        }

        // ── Rematch flow ──────────────────────────────────────────
        //
        // Both players hold the floating REMATCH button on the results screen and vote
        // via REMATCH_VOTE packets. Once both are in, ratings get re-exchanged (they
        // changed when the last bout was scored) and the host just re-runs the normal
        // START_MATCH pipeline — same countdown, same Auditorium reload — so we're
        // reusing the already-tested path instead of building a new one.

        private GameObject _rematchGO;
        private TextMeshProUGUI _rematchLabel;
        private bool localRematchVoted;
        private bool remoteRematchVoted;

        private void SpawnRematchButton(GameObject exitMenu)
        {
            var nm = NetworkManager.Instance;
            if (nm == null || !nm.IsConnected || SoloDebugActive) return;
            if (_rematchGO != null) return;

            try
            {
                var root = new GameObject("MP_RematchButton");
                root.transform.position = exitMenu.transform.position - exitMenu.transform.right * 1.1f;
                root.transform.rotation = exitMenu.transform.rotation;

                var canvasGO = new GameObject("Canvas");
                canvasGO.transform.SetParent(root.transform, false);
                var canvas = canvasGO.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var crt = canvasGO.GetComponent<RectTransform>();
                crt.sizeDelta = new Vector2(420f, 260f);
                crt.localScale = Vector3.one * 0.002f;

                var bg = new GameObject("BG");
                bg.transform.SetParent(canvasGO.transform, false);
                var bgRT = bg.AddComponent<RectTransform>();
                bgRT.anchoredPosition = Vector2.zero;
                bgRT.sizeDelta = new Vector2(420f, 260f);
                var bgImg = bg.AddComponent<Image>();
                bgImg.color = new Color(0f, 0f, 0f, 0.55f);
                bgImg.raycastTarget = false;

                var titleGO = new GameObject("Title");
                titleGO.transform.SetParent(canvasGO.transform, false);
                var tRT = titleGO.AddComponent<RectTransform>();
                tRT.anchoredPosition = new Vector2(0f, 62f);
                tRT.sizeDelta = new Vector2(400f, 90f);
                var title = titleGO.AddComponent<TextMeshProUGUI>();
                title.text = "REMATCH";
                title.fontSize = 64;
                title.fontStyle = FontStyles.Bold;
                title.alignment = TextAlignmentOptions.Center;
                title.color = new Color(1f, 0.85f, 0.4f);
                title.raycastTarget = false;

                var subGO = new GameObject("Sub");
                subGO.transform.SetParent(canvasGO.transform, false);
                var sRT = subGO.AddComponent<RectTransform>();
                sRT.anchoredPosition = new Vector2(0f, -48f);
                sRT.sizeDelta = new Vector2(400f, 120f);
                _rematchLabel = subGO.AddComponent<TextMeshProUGUI>();
                _rematchLabel.fontSize = 30;
                _rematchLabel.alignment = TextAlignmentOptions.Center;
                _rematchLabel.color = new Color(1f, 1f, 1f, 0.85f);
                _rematchLabel.raycastTarget = false;
                _rematchLabel.text = remoteRematchVoted
                    ? "Opponent wants a rematch!\nHold a fist here to accept"
                    : "Hold a fist here\nto challenge again";

                var col = root.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.size = new Vector3(0.85f, 0.55f, 0.30f);

                var trig = root.AddComponent<MultiplayerReadyUpTrigger>();
                trig.Mode = MultiplayerReadyUpTrigger.TriggerMode.Rematch;

                _rematchGO = root;
                MelonLogger.Msg("[Multiplayer] ✓ Rematch button spawned next to exit menu");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Multiplayer] SpawnRematchButton: {e.Message}");
            }
        }

        public void OnLocalRematchVote()
        {
            if (localRematchVoted) return;
            localRematchVoted = true;
            MelonLogger.Msg("[Multiplayer] ✓ LOCAL player voted for a rematch");
            NetworkManager.Instance?.SendRematchVote();
            if (_rematchLabel != null)
                _rematchLabel.text = remoteRematchVoted ? "Starting rematch..." : "Waiting for opponent...";
            CheckBothWantRematch();
        }

        public static void OnRemoteRematchVote()
        {
            if (Instance == null) return;
            Instance.remoteRematchVoted = true;
            MelonLogger.Msg("[Multiplayer] ✓ REMOTE player voted for a rematch");
            if (Instance._rematchLabel != null && !Instance.localRematchVoted)
                Instance._rematchLabel.text = "Opponent wants a rematch!\nHold a fist here to accept";
            Instance.CheckBothWantRematch();
        }

        private void CheckBothWantRematch()
        {
            if (!localRematchVoted || !remoteRematchVoted) return;

            MelonLogger.Msg("[Multiplayer] ═══ BOTH WANT A REMATCH — RESTARTING ═══");
            if (_rematchLabel != null) _rematchLabel.text = "Starting rematch...";

            localRematchVoted = false;
            remoteRematchVoted = false;

            var nm = NetworkManager.Instance;
            nm?.RefreshEloExchange();   // ratings changed when the last bout was scored

            // Host re-runs the standard start pipeline; the guest's reload is driven by
            // the resulting START_MATCH packet exactly like a first match.
            if (nm != null && nm.IsHost)
                StartMatchAsHost();
        }

        private void InvokePostMatchSetupAction()
        {
            try
            {
                var bc = BoutController.instance;
                if (bc == null)
                {
                    MelonLogger.Error("[Multiplayer] Guest: BoutController.instance null — can't run ceremony");
                    return;
                }

                var postMatchMethod = typeof(BoutController).GetMethod("PostMatchSetupAction",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (postMatchMethod != null)
                {
                    postMatchMethod.Invoke(bc, null);
                    MelonLogger.Msg("[Multiplayer] Guest: ✓ PostMatchSetupAction invoked — ceremony running");
                }
                else
                {
                    MelonLogger.Error("[Multiplayer] Guest: PostMatchSetupAction not found via reflection");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ InvokePostMatchSetupAction error: {e.Message}\n{e.StackTrace}");
            }
        }

        // ── Continue button helpers

        private IEnumerator HideContinueButtonImmediate()
        {
            yield return null;

            float elapsed = 0f;
            while (elapsed < 2f)
            {
                var pc = BlueCornerUI.GetCornerUI(_assignedCorner);
                if (pc?.roundMenu != null)
                {
                    ContinueButtonSuppressor.SetSuppressed(pc.roundMenu, true);
                    var ct = pc.roundMenu.GetComponentInChildren<ContinueTrigger>(true);
                    if (ct != null) ct.enabled = false;
                    MelonLogger.Msg("[Multiplayer] ✓ Continue button hidden at scene load (frame-1)");
                    yield break;
                }
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }
            MelonLogger.Warning("[Multiplayer] ⚠ Could not find PlayerCorner within 2s to hide continue button");
        }

        private void HideContinueButton()
        {
            try
            {
                var pc = BlueCornerUI.GetCornerUI(_assignedCorner);
                if (pc?.roundMenu == null) { MelonLogger.Error("[Multiplayer] ✗ Can't find roundMenu to hide"); return; }

                // Suppressor keeps it hidden even when CornerPositionMarker re-shows the
                // menu on every corner-zone re-entry.
                ContinueButtonSuppressor.SetSuppressed(pc.roundMenu, true);
                MelonLogger.Msg("[Multiplayer] ✓ Hidden continue button (waiting period)");

                var ct = pc.roundMenu.GetComponentInChildren<ContinueTrigger>(true);
                if (ct != null) { ct.enabled = false; MelonLogger.Msg("[Multiplayer] ✓ Disabled ContinueTrigger"); }
            }
            catch (Exception e) { MelonLogger.Error($"[Multiplayer] ✗ HideContinueButton: {e.Message}"); }
        }

        private void EnableReadyUpButton(MultiplayerReadyUpTrigger.TriggerMode mode)
        {
            try
            {
                var pc = BlueCornerUI.GetCornerUI(_assignedCorner);
                if (pc?.roundMenu == null) { MelonLogger.Error("[Multiplayer] ✗ Can't find roundMenu for ReadyUp"); return; }

                ContinueButtonSuppressor.SetSuppressed(pc.roundMenu, false);
                pc.roundMenu.SetActive(true);
                MelonLogger.Msg("[Multiplayer] ✓ roundMenu shown");

                var ct = pc.roundMenu.GetComponentInChildren<ContinueTrigger>(true);
                if (ct != null) { ct.enabled = false; }

                RenameButtonLabel(pc.roundMenu, mode == MultiplayerReadyUpTrigger.TriggerMode.ReadyUp
                    ? "Ready Up" : "Skip Break");

                var triggerGO = ct != null ? ct.gameObject : pc.roundMenu;
                readyUpTrigger = triggerGO.GetComponent<MultiplayerReadyUpTrigger>()
                              ?? triggerGO.AddComponent<MultiplayerReadyUpTrigger>();

                readyUpTrigger.Mode = mode;
                readyUpTrigger.Fired = false;
                readyUpTrigger.enabled = true;

                if (ct?.ProgressBar != null) readyUpTrigger.ProgressBar = ct.ProgressBar;

                MelonLogger.Msg($"[Multiplayer] ✓ ReadyUpTrigger attached (mode: {mode})");
            }
            catch (Exception e) { MelonLogger.Error($"[Multiplayer] ✗ EnableReadyUpButton: {e.Message}\n{e.StackTrace}"); }
        }

        private void RenameButtonLabel(GameObject root, string newLabel)
        {
            try
            {
                bool any = false;

                foreach (var t in root.GetComponentsInChildren<Text>(true))
                {
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    string lower = t.text.ToLowerInvariant();
                    if (lower.Contains("continue") || lower.Contains("ready") || lower.Contains("skip"))
                    { t.text = newLabel; any = true; }
                }

                foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (tmp == null || string.IsNullOrEmpty(tmp.text)) continue;
                    string lower = tmp.text.ToLowerInvariant();
                    if (lower.Contains("continue") || lower.Contains("ready") || lower.Contains("skip"))
                    { tmp.text = newLabel; any = true; }
                }

                if (any)
                    MelonLogger.Msg($"[Multiplayer] ✓ Button label → '{newLabel}'");
                else
                    MelonLogger.Warning($"[Multiplayer] ⚠ Could not find Continue/Ready/Skip label to rename → '{newLabel}'");
            }
            catch (Exception e) { MelonLogger.Warning($"[Multiplayer] RenameButtonLabel: {e.Message}"); }
        }

        // ── Pre-fight Ready Up

        public void OnLocalPlayerReadiedUp()
        {
            if (localReadiedUp) return;
            localReadiedUp = true;
            MelonLogger.Msg("[Multiplayer] ✓ LOCAL player readied up");
            NetworkManager.Instance?.SendReadyUp();
            CheckBothReadyToFight();
        }

        public static void OnRemotePlayerReadiedUp()
        {
            if (Instance == null) return;
            MelonLogger.Msg("[Multiplayer] ✓ REMOTE player readied up");
            Instance.remoteReadiedUp = true;
            Instance.CheckBothReadyToFight();
        }

        public static void OnRemoteCornerAssigned(BoutController.Corner corner)
        {
            if (Instance == null) return;
            Instance._assignedCorner = corner;
            Instance._cornerAssigned = true;
            Instance.EnsureCornerAssignmentApplied();
        }

        private void CheckBothReadyToFight()
        {
            // No opponent in solo debug — their ready is implicit.
            if (SoloDebugActive) remoteReadiedUp = true;

            MelonLogger.Msg($"[Multiplayer] Ready check — Local: {localReadiedUp}, Remote: {remoteReadiedUp}");
            if (!localReadiedUp || !remoteReadiedUp) return;

            MelonLogger.Msg("[Multiplayer] ═══ BOTH READY — STARTING FIGHT ═══");
            MelonCoroutines.Start(StartFightSequence());
        }

        private IEnumerator StartFightSequence()
        {
            yield return new WaitForSeconds(0.5f);

            readyUpPhaseActive = false;
            if (readyUpTrigger != null) readyUpTrigger.enabled = false;

            // Guest only: re-anchor the post-match exit UI to the blue side once it spawns.
            MelonCoroutines.Start(WatchPostMatchPlacement());

            // After this match ends (exit or natural finish), land back on the
            // multiplayer menu instead of the singleplayer fight menu.
            _returnToMultiplayerMenu = true;

            try
            {
                if (!_trackingReady)
                {
                    var pc = PlayerController.instance;
                    if (pc != null && pc.hmd != null &&
                        pc.LeftHandTarget != null &&
                        pc.RightHandTarget != null)
                    {
                        _headTransform = pc.hmd.transform;
                        _leftTransform = pc.LeftHandTarget.transform;
                        _rightTransform = pc.RightHandTarget.transform;
                        _trackingReady = true;
                        MelonLogger.Msg("[Multiplayer] ✓ VR tracking acquired at fight start");
                    }
                    else
                    {
                        MelonLogger.Warning("[Multiplayer] ⚠ VR tracking still unavailable at fight start");
                    }
                }

                if (_cornerAssigned)
                    ApplyCornerAssignment(_assignedCorner);

                // We leave punch force on the game's own auto-calibration on purpose.
                // effectiveMassModifier is 3600 / your hardest punch ever, so only a
                // genuine max-effort swing hits the 3600 cap — that's the game's whole
                // damage balance. We tried pinning it to a fixed 2.4 once and it put
                // every medium punch at the cap, which meant 3 good punches = a KO
                // (dizzyMax is 1080, so hits over 3200 add up fast, and anything over
                // 4280 alone is an instant knockdown). Auto-calibration also self-corrects
                // as you land punches, which is what reins in a fresh install that starts
                // out punching above its real strength.

                MelonLogger.Msg("[Multiplayer] Calling BoutController.Continue()...");
                BoutController.Continue();
                MelonLogger.Msg("[Multiplayer] ✓ BoutController.Continue() called");

                if (ghostBoxer != null)
                    MelonCoroutines.Start(NeutraliseGhostCollidersDelayed(ghostBoxer));

                multiplayerBoutActive = true;
                HookLocalPlayerDamageEvents();
                HookQuitTrigger();

                // Arm the Elo tracker for real matches only — never for solo debug.
                var nmElo = NetworkManager.Instance;
                if (nmElo != null && nmElo.IsConnected && !SoloDebugActive)
                    EloManager.BeginMatch(nmElo.OpponentElo);

                MelonLogger.Msg("[Multiplayer] ✓✓✓ BOUT ACTIVE — position sync running");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Multiplayer] ✗ StartFightSequence: {e.Message}\n{e.StackTrace}");
            }
        }

        // ── Between-round break skip

        public void OnLocalBreakSkipVote()
        {
            if (localBreakSkipVoted) return;
            localBreakSkipVoted = true;
            MelonLogger.Msg("[Multiplayer] ✓ LOCAL break-skip vote");
            NetworkManager.Instance?.SendBreakSkipVote();
            CheckBothBreakSkipVotes();
        }

        public static void OnRemoteBreakSkipVote()
        {
            if (Instance == null) return;
            MelonLogger.Msg("[Multiplayer] ✓ REMOTE break-skip vote");
            Instance.remoteBreakSkipVoted = true;
            Instance.CheckBothBreakSkipVotes();
        }

        private void CheckBothBreakSkipVotes()
        {
            // No opponent in solo debug — their vote is implicit.
            if (SoloDebugActive) remoteBreakSkipVoted = true;

            MelonLogger.Msg($"[Multiplayer] Break-skip check — Local: {localBreakSkipVoted}, Remote: {remoteBreakSkipVoted}");
            if (!localBreakSkipVoted || !remoteBreakSkipVoted) return;

            MelonLogger.Msg("[Multiplayer] ═══ BOTH VOTED TO SKIP BREAK ═══");
            breakSkipPhaseActive = false;
            if (readyUpTrigger != null) readyUpTrigger.enabled = false;

            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHost)
            {
                MelonLogger.Msg("[Multiplayer] Host calling BoutController.Continue() to skip break...");
                try { BoutController.Continue(); }
                catch (Exception e) { MelonLogger.Error($"[Multiplayer] ✗ Break skip Continue: {e.Message}"); }
            }
            else
            {
                MelonLogger.Msg("[Multiplayer] Guest: waiting for host to call Continue()");
            }
        }

        // ── Guest-side round lifecycle notifications

        public static void OnRemoteRoundStart(int roundNumber)
        {
            if (Instance == null) return;
            MelonLogger.Msg($"[Multiplayer] ✓ ROUND_START sync: round {roundNumber} confirmed by host");

            if (!Instance.multiplayerBoutActive)
            {
                Instance.multiplayerBoutActive = true;
                MelonLogger.Msg("[Multiplayer] multiplayerBoutActive set true via ROUND_START fallback");
            }

            // Host's round started, so start ours too — otherwise the guest's own
            // BoutController just sits in break until its own timer runs out, and
            // break-skip wouldn't actually do anything. Also stops us drifting behind.
            try
            {
                if (BoutController.instance != null && !BoutController.IsFightHappening())
                {
                    MelonLogger.Msg("[Multiplayer] Guest: continuing local BoutController to match host round start");
                    BoutController.Continue();
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Multiplayer] OnRemoteRoundStart Continue error: {e.Message}");
            }
        }

        // The guest runs its own round/break clocks, and they start a little late
        // (ping + the Continue() path) and drift worse every round — which is what
        // caused rounds ending while the clock still looked like it was counting. The
        // host's packets are the source of truth here, so we just force the guest's
        // timers to match.
        private static readonly FieldInfo RoundTimerField = typeof(BoutController).GetField("roundTimer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo BreakTimerField = typeof(BoutController).GetField("breakTimer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static void OnRemoteRoundEnd(int roundNumber)
        {
            if (Instance == null) return;
            MelonLogger.Msg($"[Multiplayer] Guest: ROUND {roundNumber} ended (via host packet)");

            // Zero the round clock and let our own BoutController run its normal end path
            // (bell, EndRound, StartBreak/decision) — happens within a frame instead of
            // dragging on for seconds. Can't cut a knockdown count short doing this since
            // the game itself holds the round open during one (isCountingDown).
            try
            {
                var bc = BoutController.instance;
                if (bc != null && RoundTimerField != null)
                    RoundTimerField.SetValue(bc, 0f);
            }
            catch (Exception e) { MelonLogger.Warning($"[Multiplayer] OnRemoteRoundEnd timer sync: {e.Message}"); }
        }

        public static void OnRemoteBreakStart(float breakTime)
        {
            if (Instance == null) return;
            MelonLogger.Msg($"[Multiplayer] Guest: BREAK started ({breakTime:F0}s, via host packet)");

            // Re-align our break clock to the host's full break duration so the next
            // round starts at the same moment on both machines.
            try
            {
                var bc = BoutController.instance;
                if (bc != null && BreakTimerField != null && breakTime > 0f)
                    BreakTimerField.SetValue(bc, breakTime);
            }
            catch (Exception e) { MelonLogger.Warning($"[Multiplayer] OnRemoteBreakStart timer sync: {e.Message}"); }

            MelonCoroutines.Start(Instance.EnableBreakSkipButtonDelayed());
        }

        // ── State resets

        private void ResetReadyPhaseState()
        {
            localReadiedUp = false;
            remoteReadiedUp = false;
            localRematchVoted = false;
            remoteRematchVoted = false;
            _rematchGO = null;      // old scene object; destroyed with the scene
            _rematchLabel = null;
            readyUpPhaseActive = false;
            localBreakSkipVoted = false;
            remoteBreakSkipVoted = false;
            breakSkipPhaseActive = false;
            readyUpTrigger = null;
            _cornerAssigned = false;
            _assignedCorner = BoutController.Corner.Red;
            if (_cornerApplyCoroutine != null) { MelonCoroutines.Stop(_cornerApplyCoroutine); _cornerApplyCoroutine = null; }
        }

        private void ResetBreakSkipState()
        {
            localBreakSkipVoted = false;
            remoteBreakSkipVoted = false;
        }

        private void FullBoutReset()
        {
            multiplayerBoutActive = false;
            isWaitingForPlayer = false;
            localRematchVoted = false;
            remoteRematchVoted = false;
            _rematchGO = null;
            _rematchLabel = null;
            _localPlacementSettled = false;
            ghostBoxer = null;
            _headTransform = null;
            _leftTransform = null;
            _rightTransform = null;
            _trackingReady = false;
            boutListenerRegistered = false;
            boutEndSent = false;
            _setupAuditoriumRunning = false;
            _quitTriggerHooked = false;
            _damageHooksApplied = false;
            _localWasDown = false;
            _cornerAssigned = false;
            _assignedCorner = BoutController.Corner.Red;
            BlueCornerUI.Reset();
            if (_cornerApplyCoroutine != null) { MelonCoroutines.Stop(_cornerApplyCoroutine); _cornerApplyCoroutine = null; }
            ResetReadyPhaseState();
        }

        private void EnsureCornerAssignmentApplied()
        {
            if (_cornerApplyCoroutine != null) MelonCoroutines.Stop(_cornerApplyCoroutine);
            _cornerApplyCoroutine = MelonCoroutines.Start(ApplyCornerWhenReady());
        }

        private IEnumerator ApplyCornerWhenReady()
        {
            float elapsed = 0f;
            while (PlayerController.instance == null && elapsed < 10f)
            {
                yield return null;
                elapsed += Time.deltaTime;
            }

            if (PlayerController.instance != null)
                ApplyCornerAssignment(_assignedCorner);
            else
                MelonLogger.Warning("[Multiplayer] ⚠ PlayerController not ready for corner assignment after 10s");
        }

        private void ApplyCornerAssignment(BoutController.Corner corner)
        {
            try
            {
                var pc = PlayerController.instance;
                if (pc == null)
                {
                    MelonLogger.Warning("[Multiplayer] ⚠ PlayerController.instance null — cannot assign corner yet");
                    return;
                }

                pc.corner = corner;

                // Move the corner UI + quit trigger to the assigned corner. This also makes
                // every game-driven corner return (PreMatchSetup, StartBreakMovement, ...)
                // send the player to the right corner, since PlayerController always uses
                // the scene's PlayerCorner object.
                BlueCornerUI.EnsurePlacement(corner);

                if (corner == BoutController.Corner.Blue)
                {
                    if (BoutController.instance?.blueStart != null)
                        PlayAreaController.SendPlayerToPosition(BoutController.instance.blueStart.position,
                            BoutController.instance.blueStart.rotation.eulerAngles,
                            PlayAreaController.PlayAreaEdge.Back, PlayAreaController.PlayAreaEdge.Center);
                }
                else
                {
                    if (BoutController.instance?.redStart != null)
                        PlayAreaController.SendPlayerToPosition(BoutController.instance.redStart.position,
                            BoutController.instance.redStart.rotation.eulerAngles,
                            PlayAreaController.PlayAreaEdge.Back, PlayAreaController.PlayAreaEdge.Center);
                }

                _localPlacementSettled = true;
                MelonLogger.Msg($"[Multiplayer] ✓ Local player assigned to {corner} corner");
            }
            catch (Exception e)
            {
                _localPlacementSettled = true;   // never leave pose streaming blocked
                MelonLogger.Warning($"[Multiplayer] ApplyCornerAssignment error: {e.Message}");
            }
        }

        // ── Static callbacks from NetworkManager

        public static void OnHostLobbyCreated()
        {
            // The lobby (and its join code) now exists. Do NOT load the Auditorium here:
            // the host stays on the lobby screen showing the code and only enters the match
            // when they press "Start Lobby" (StartMatchAsHost).
            MelonLogger.Msg("[Multiplayer] \u2713 Lobby created \u2014 showing join code, waiting for host to press Start Lobby");
            if (Instance != null) Instance.isWaitingForPlayer = false;

            if (string.IsNullOrEmpty(BoutRules.boxerResourceName))
            {
                BoutRules.boxerResourceName = "Hojo Mizushima";
                MelonLogger.Msg("[Multiplayer] Set default boxer: Hojo Mizushima");
            }
        }

        public static void OnBothPlayersReady()
        {
            MelonLogger.Msg("[Multiplayer] ✓✓✓ OnBothPlayersReady() called!");
            if (Instance == null) { MelonLogger.Error("[Multiplayer] ✗ Instance null!"); return; }

            // Guard against double-invoke from a race between TryFinalizeConnectionIfLobbyFull
            // (lobby poll / chat_entered paths) and the OnP2PSessionRequest host fallback.
            // NetworkManager already sets _connectionFinalized before calling us, but this
            // guards the case where that path is bypassed.
            Instance.isWaitingForPlayer = false;

            string currentScene = SceneManager.GetActiveScene().name;
            MelonLogger.Msg($"[Multiplayer] Current scene: {currentScene}");

            if (currentScene == "Auditorium")
            {
                Instance.ResetReadyPhaseState();
                NetworkManager.Instance?.ResetReadyState();
                NetworkManager.Instance?.ResetBreakSkipState();
                Instance._setupAuditoriumRunning = false;
                MelonCoroutines.Start(Instance.SetupAuditorium(bothPlayersAlreadyConnected: true));
            }
            else
            {
                // Both players are connected but still sitting in the menu. Do NOT auto-load
                // the match. The host decides when to begin by pressing "Start Lobby"
                // (StartMatchAsHost), which sends START_MATCH to the joiner. Until then the
                // joiner's lobby screen shows "Waiting for Lobby Start".
                MelonLogger.Msg("[Multiplayer] Both players connected in lobby \u2014 waiting for host to press Start Lobby");
                if (string.IsNullOrEmpty(BoutRules.boxerResourceName))
                {
                    BoutRules.boxerResourceName = "Hojo Mizushima";
                    MelonLogger.Msg("[Multiplayer] Set default boxer for joiner: Hojo Mizushima");
                }

                // Exception: matchmade lobbies (Queue for Match) start on their own.
                var nmAuto = NetworkManager.Instance;
                if (AutoStartWhenReady && nmAuto != null && nmAuto.IsHost)
                {
                    AutoStartWhenReady = false;
                    MelonLogger.Msg("[Multiplayer] Matchmade opponent joined \u2014 auto-starting match");
                    MelonCoroutines.Start(AutoStartMatchSoon());
                }
            }
        }

        private static IEnumerator AutoStartMatchSoon()
        {
            // Give the joiner a beat to finish lobby setup + see "Match found".
            yield return new WaitForSeconds(1.5f);
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsConnected) StartMatchAsHost();
        }

        /// <summary>
        /// Pins BoutRules to the same neutral values on both machines at match start.
        /// These fields are statics that carry over from whatever singleplayer fight was
        /// set up last, so a leftover difficulty handicap or a different boxer could
        /// leave one machine's ghost tougher than the other's. Since every damage and
        /// knockdown call happens against the ghost on the puncher's machine, matching
        /// ghosts means the match actually comes down to boxing skill.
        /// </summary>
        private static void ApplyMultiplayerBoutRules()
        {
            BoutRules.boxerResourceName = "Hojo Mizushima";  // same model + same base stats everywhere
            BoutRules.numberOfRounds = 3;
            BoutRules.roundTime = 180f;
            BoutRules.breakTime = 60f;
            BoutRules.knockdownLimit = 3;
            BoutRules.damageDifferenceToWin = 0f;
            BoutRules.judgeDecisionFuzziness = 0f;
            BoutRules.overrideBoxerStats = false;
            BoutRules.chin = 1f;
            BoutRules.power = 1f;
            BoutRules.fistSpeed = 1f;
            BoutRules.dodgeSpeed = 1f;
            BoutRules.blockSpeed = 1f;
            BoutRules.aggression = 1f;
            BoutRules.traumaGainRate = 1f;
            BoutRules.judgeStrictness = 1f;
            MelonLogger.Msg("[Multiplayer] \u2713 Bout rules normalized \u2014 identical health/damage rules on both machines");
        }

        private static bool _matchStartCountdownRunning = false;
        private const int MATCH_START_COUNTDOWN_SECONDS = 5;

        /// <summary>
        /// Host pressed "Start Lobby": notify the joiner IMMEDIATELY (so both sides count
        /// down together), run the 5-second countdown, then load the Auditorium.
        /// </summary>
        public static void StartMatchAsHost()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) { MelonLogger.Error("[Multiplayer] \u2717 StartMatchAsHost: NetworkManager is null"); return; }
            if (_matchStartCountdownRunning) return;

            MelonLogger.Msg("[Multiplayer] Host starting match \u2014 countdown begins");

            if (nm.IsConnected) nm.SendStartMatch();
            else MelonLogger.Warning("[Multiplayer] StartMatchAsHost: no joiner connected \u2014 solo/debug start");

            MelonCoroutines.Start(CountdownThenLoadMatch());
        }

        /// <summary>Joiner received START_MATCH: run the same countdown, then load in.
        /// Both sides start counting within one network hop of each other.</summary>
        public static void OnStartMatchReceived()
        {
            MelonLogger.Msg("[Multiplayer] START_MATCH received from host \u2014 countdown begins");
            if (_matchStartCountdownRunning) return;
            MelonCoroutines.Start(CountdownThenLoadMatch());
        }

        private static IEnumerator CountdownThenLoadMatch()
        {
            _matchStartCountdownRunning = true;
            try { MultiplayerMenuManager.Instance?.ShowMatchCountdown(MATCH_START_COUNTDOWN_SECONDS); }
            catch (Exception e) { MelonLogger.Warning($"[Multiplayer] Countdown UI error: {e.Message}"); }

            yield return new WaitForSeconds(MATCH_START_COUNTDOWN_SECONDS);
            _matchStartCountdownRunning = false;

            // If the opponent bailed during the countdown, don't load into an empty match
            // (solo debug is the exception \u2014 it starts alone on purpose).
            var nm = NetworkManager.Instance;
            bool soloDebug = Instance != null && Instance.SoloDebugActive;
            if (!soloDebug && (nm == null || !nm.IsConnected))
            {
                MelonLogger.Warning("[Multiplayer] Opponent left during the countdown \u2014 match start aborted");
                yield break;
            }

            ApplyMultiplayerBoutRules();
            if (Instance != null) Instance.isWaitingForPlayer = false;
            _multiplayerMatchPending = true;
            SafeLoadScene("Auditorium");
        }

        public static void ShowWaitingForOpponentOverlay(bool show)
        {
            if (Instance != null) Instance.isWaitingForPlayer = show;
            if (show) MelonLogger.Msg("[Multiplayer] Waiting for opponent...");
            else MelonLogger.Msg("[Multiplayer] Hiding waiting overlay");
        }

        public static void OnOpponentDisconnected()
        {
            MelonLogger.Msg("[Multiplayer] Opponent disconnected");
            // Hard disconnect ≠ retire: could be a genuine network drop, so the match is
            // voided rather than scored (prevents rating grief from flaky connections;
            // intentional quits go through the retire path and DO count).
            EloManager.CancelMatch();
            Instance?.FullBoutReset();

            // Only force a scene change when we're actually inside the match. If we're
            // still on the home menu (e.g. the joiner left the lobby before Start), a
            // reload would just tear the menus down for nothing.
            if (SceneManager.GetActiveScene().name == "Auditorium")
                SafeLoadScene("title");
        }

        // ── Scene loading

        private static void SafeLoadScene(string sceneName)
        {
            if (LevelLoader.instance != null)
            {
                MelonLogger.Msg($"[Multiplayer] Loading scene via LevelLoader: {sceneName}");
                LevelLoader.LoadScene(sceneName);
            }
            else
            {
                MelonLogger.Msg($"[Multiplayer] LevelLoader.instance is null — loading via SceneManager: {sceneName}");
                try { SceneManager.LoadScene(sceneName); }
                catch (Exception e) { MelonLogger.Error($"[Multiplayer] SceneManager.LoadScene({sceneName}) failed: {e.Message}"); }
            }
        }

        // ── Singletons

        public static NetworkManager EnsureNetworkManagerExists()
        {
            if (NetworkManager.Instance != null) return NetworkManager.Instance;
            if (networkManagerHost == null)
            {
                networkManagerHost = new GameObject("ToFMultiplayer_NetworkManager");
                UnityEngine.Object.DontDestroyOnLoad(networkManagerHost);
            }
            return networkManagerHost.GetComponent<NetworkManager>()
                ?? networkManagerHost.AddComponent<NetworkManager>();
        }

        public static LobbyBrowser EnsureLobbyBrowserExists()
        {
            if (LobbyBrowser.Instance != null) return LobbyBrowser.Instance;
            var go = new GameObject("ToFMultiplayer_LobbyBrowser");
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<LobbyBrowser>();
        }

        // ── IMGUI

        private void DrawMenuWindow(int id)
        {
            var nm = NetworkManager.Instance;
            bool connected = nm != null && nm.IsConnected;
            bool bothReady = nm != null && nm.BothPlayersReady;
            bool hosting = nm != null && nm.IsHosting;

            GUILayout.BeginVertical();
            GUILayout.Label("TOTF Multiplayer  [F4 to close]", _titleStyle);
            GUILayout.Space(6);

            string statusText; Color statusColor;
            if (hosting && SoloDebugActive) { statusText = "◆ Hosting — DEBUG solo start available"; statusColor = Color.magenta; }
            else if (hosting) { statusText = "● Hosting (waiting for join...)"; statusColor = Color.cyan; }
            else if (connected && bothReady) { statusText = "● Connected — both players ready"; statusColor = Color.green; }
            else if (connected) { statusText = "◌ Waiting for opponent..."; statusColor = Color.yellow; }
            else { statusText = "○ Not connected"; statusColor = Color.red; }

            _statusStyle.normal.textColor = statusColor;
            GUILayout.Label(statusText, _statusStyle);

            if (hosting && nm.CurrentJoinCode != null)
            {
                GUILayout.Label($"[{(nm.IsPublicLobby ? "PUBLIC" : "PRIVATE")}] Code: {nm.CurrentJoinCode}", _labelStyle);
                GUILayout.Label(nm.IsPublicLobby ? "Visible in PUBLIC browser" : "Share code with friend!", _labelStyle);
            }

            if (multiplayerBoutActive) GUILayout.Label("Bout active — syncing positions", _statusStyle);

            // ── Debug mode
            GUILayout.Space(8);
            bool newDebug = GUILayout.Toggle(DebugMode,
                " Debug Mode — diagnostics HUD + solo lobby testing");
            if (newDebug != DebugMode)
            {
                DebugMode = newDebug;
                MelonLogger.Msg($"[Multiplayer] Debug mode {(DebugMode ? "ENABLED" : "disabled")}");
            }
            if (DebugMode)
                GUILayout.Label("Host a lobby, then Start Solo: the ghost mirrors your movements.", _labelStyle);

            if (DebugMode && hosting && !connected)
            {
                if (GUILayout.Button("▶ Start SOLO Test Match", GUILayout.Height(34)))
                    StartMatchAsHost();
                GUILayout.Space(4);
            }

            GUILayout.Space(6);
            GUI.enabled = !connected && !hosting;

            if (GUILayout.Button("Host PUBLIC (Visible in Browser)", GUILayout.Height(40)))
                EnsureNetworkManagerExists().HostGame(isPublic: true);
            GUILayout.Space(4);
            if (GUILayout.Button("Host PRIVATE (Code Only)", GUILayout.Height(40)))
                EnsureNetworkManagerExists().HostGame(isPublic: false);
            GUILayout.Space(4);
            if (GUILayout.Button("Browse PUBLIC Lobbies", GUILayout.Height(40)))
            {
                _showLobbyBrowser = !_showLobbyBrowser;
                if (_showLobbyBrowser) EnsureLobbyBrowserExists().SearchLobbies();
            }
            GUILayout.Space(4);
            GUILayout.Label("Or join PRIVATE lobby by code:", _labelStyle);
            _joinCodeInput = GUILayout.TextField(_joinCodeInput, GUILayout.Height(25)).ToUpper();
            if (GUILayout.Button("Join", GUILayout.Height(30)) && !string.IsNullOrEmpty(_joinCodeInput))
            {
                EnsureNetworkManagerExists().JoinByCode(_joinCodeInput);
                _joinCodeInput = ""; _showLobbyBrowser = false;
            }

            GUI.enabled = true;
            GUILayout.Space(4);
            GUI.enabled = connected || hosting;

            if (hosting && GUILayout.Button("End Lobby", GUILayout.Height(30)))
            {
                FullBoutReset();
                nm?.EndLobby();
                _showLobbyBrowser = false;
                SafeLoadScene("title");
            }
            else if (connected && GUILayout.Button("Disconnect", GUILayout.Height(30)))
            {
                FullBoutReset();
                nm?.Disconnect();
                _showLobbyBrowser = false;
                SafeLoadScene("title");
            }

            if (_showLobbyBrowser) { GUI.enabled = !connected && !hosting; GUILayout.Space(10); DrawLobbyBrowser(); }

            GUI.enabled = true;
            GUILayout.Space(6);
            GUILayout.Label($"Scene: {SceneManager.GetActiveScene().name}", _labelStyle);
            GUILayout.Label($"Packets sent: {packetSequenceNumber}", _labelStyle);
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
        }

        private void DrawLobbyBrowser()
        {
            var browser = LobbyBrowser.Instance;
            if (browser == null) return;
            GUILayout.Label("PUBLIC Lobbies on Steam Network", _titleStyle);
            if (browser.IsSearching) { GUILayout.Label("Searching...", _labelStyle); return; }
            var lobbies = browser.GetDiscoveredLobbies();
            if (lobbies.Count == 0)
            {
                GUILayout.Label("No PUBLIC lobbies found.", _labelStyle);
                if (GUILayout.Button("Search Again", GUILayout.Height(25))) browser.SearchLobbies();
                return;
            }
            GUILayout.Label($"Found {lobbies.Count} lobby/lobbies:", _labelStyle);
            _lobbyScrollPos = GUILayout.BeginScrollView(_lobbyScrollPos, GUILayout.Height(150));
            foreach (var lobby in lobbies)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"[PUBLIC] {lobby.JoinCode} - {lobby.HostName} ({lobby.PlayerCount}/{lobby.MaxPlayers})", _labelStyle);
                if (GUILayout.Button("Join", GUILayout.Width(50))) { browser.JoinLobby(lobby.LobbyID); _showLobbyBrowser = false; }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Refresh", GUILayout.Height(25))) browser.SearchLobbies();
        }

        private void InitStyles()
        {
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = Color.white } };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = Color.white } };
            _waitingStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 36,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.yellow }
            };
            _readyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            _hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white }
            };
            _hudHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _stylesInitialized = true;
        }

        // —— Main Menu button setup

        private IEnumerator SetupMainMenuButton()
        {
            // The HomeMenuManager is created in Awake and wires up its child menus in
            // Start, so give the scene a few frames to settle before we look for it.
            HomeMenuManager homeMenuManager = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                homeMenuManager = UnityEngine.Object.FindObjectOfType<HomeMenuManager>();
                if (homeMenuManager != null && homeMenuManager.mainMenu != null)
                    break;
                yield return new WaitForEndOfFrame();
            }

            try
            {
                MelonLogger.Msg("[Menu] Searching for HomeMenuManager...");

                if (homeMenuManager == null)
                {
                    MelonLogger.Error("[Menu] \u2717 Could not find HomeMenuManager in scene — multiplayer button NOT created");
                    yield break;
                }

                MelonLogger.Msg("[Menu] Found HomeMenuManager, searching for Canvas...");

                var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
                if (canvas == null)
                {
                    MelonLogger.Error("[Menu] \u2717 Could not find Canvas in scene — multiplayer button NOT created");
                    yield break;
                }

                // Need the mainMenu object before we can create anything — the multiplayer
                // menu anchors right next to it, same world-space Canvas.
                var mainMenuManager = homeMenuManager.mainMenu;
                if (mainMenuManager == null)
                {
                    MelonLogger.Error("[Menu] \u2717 Could not find mainMenu in HomeMenuManager — multiplayer button NOT created");
                    yield break;
                }

                // The menu manager survives scene reloads but the button doesn't, so check
                // it's actually missing before making a new one — otherwise a home-scene
                // reload after a bout would give us two.
                if (mainMenuManager.transform.Find("MultiplayerButton") != null)
                {
                    MelonLogger.Msg("[Menu] Multiplayer button already present, skipping creation");
                    yield break;
                }

                // Create the multiplayer menu if it doesn't exist
                if (_multiplayerMenuManager == null)
                {
                    _multiplayerMenuManager = MultiplayerMenuManager.CreateMultiplayerMenu(homeMenuManager, mainMenuManager);
                    if (_multiplayerMenuManager == null)
                    {
                        MelonLogger.Error("[Menu] \u2717 Failed to create MultiplayerMenuManager");
                        yield break;
                    }
                }

                // Building this from scratch instead of cloning the settings button —
                // cloning brought along its serialized onClick too, so it kept reopening
                // Settings instead of our menu. This way the only thing wired up is
                // opening multiplayer, plus its own VR raycaster/collider so the fist
                // ray actually hits it.
                bool created = AddMultiplayerButton(mainMenuManager.settingsButton, mainMenuManager.transform, _multiplayerMenuManager);

                if (created)
                    MelonLogger.Msg("[Menu] \u2713 Multiplayer button added to the main menu");
                else
                    MelonLogger.Error("[Menu] \u2717 Failed to add the multiplayer button to the main menu");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Menu] \u2717 SetupMainMenuButton error: {e.Message}\n{e.StackTrace}");
            }
        }

        private bool AddMultiplayerButton(GameObject settingsButton, Transform mainMenuTransform, MultiplayerMenuManager menuManager)
        {
            try
            {
                MelonLogger.Msg("[Menu] Creating Multiplayer button (from scratch, self-contained VR raycaster)...");

                var buttonGO = new GameObject("MultiplayerButton");
                buttonGO.transform.SetParent(mainMenuTransform, false);

                var rt = buttonGO.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);

                // Matching the game's own bottom-row layout: SPEED BAG / DUMMY / HEAVY BAG
                // on top, FOCUS BALL / EXTRA / MULTIPLAYER underneath. Nudge Focus Ball and
                // Extra into their columns, and give Multiplayer the Heavy Bag column.
                Vector2 size = new Vector2(300f, 60f);

                var speedBag = FindButtonByLabel(mainMenuTransform, "speed bag");
                var dummy = FindButtonByLabel(mainMenuTransform, "dummy");
                var heavyBag = FindButtonByLabel(mainMenuTransform, "heavy bag");
                var focusBall = FindButtonByLabel(mainMenuTransform, "focus ball");
                var extra = FindButtonByLabel(mainMenuTransform, "extra");

                if (speedBag != null && dummy != null && heavyBag != null &&
                    focusBall != null && extra != null)
                {
                    // Working in world space projected onto the canvas plane since these
                    // buttons can live under different parents — comparing local positions
                    // wouldn't be safe. Align Focus Ball under Speed Bag, Extra under Dummy.
                    AlignColumn(focusBall, speedBag);
                    AlignColumn(extra, dummy);

                    // Multiplayer goes in the Heavy Bag column, Focus Ball row, same size as Heavy Bag.
                    size = heavyBag.rect.size;
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = size;
                    Vector3 planeRight = heavyBag.right.normalized;
                    float horiz = Vector3.Dot(heavyBag.position - focusBall.position, planeRight);
                    rt.position = focusBall.position + planeRight * horiz;
                    rt.rotation = heavyBag.rotation;

                    MelonLogger.Msg($"[Menu] \u2713 Arranged bottom row grid — Multiplayer placed under Heavy Bag at {rt.position}");
                }
                else
                {
                    // Couldn't find the native buttons, so just mirror the settings button
                    // and nudge up — at least it lands somewhere on-plane and sane.
                    MelonLogger.Warning($"[Menu] \u26a0 Grid arrange skipped (speedBag={speedBag != null}, dummy={dummy != null}, heavyBag={heavyBag != null}, focusBall={focusBall != null}, extra={extra != null}) — using fallback position");
                    var refRT = settingsButton != null ? settingsButton.GetComponent<RectTransform>() : null;
                    if (refRT != null)
                    {
                        size = refRT.sizeDelta;
                        rt.sizeDelta = size;
                        rt.anchoredPosition = refRT.anchoredPosition + new Vector2(0f, 90f);
                    }
                    else
                    {
                        rt.sizeDelta = size;
                        rt.anchoredPosition = new Vector2(0f, -150f);
                    }
                }

                var img = buttonGO.AddComponent<Image>();
                img.color = new Color(0.2f, 0.6f, 0.85f);
                img.raycastTarget = true;

                var btn = buttonGO.AddComponent<Button>();
                btn.targetGraphic = img;
                var colors = btn.colors;
                colors.normalColor = new Color(0.2f, 0.6f, 0.85f);
                colors.highlightedColor = new Color(0.3f, 0.72f, 1f);
                colors.pressedColor = new Color(0.12f, 0.45f, 0.65f);
                colors.disabledColor = new Color(0.3f, 0.3f, 0.3f);
                btn.colors = colors;

                btn.onClick.AddListener(() =>
                {
                    MelonLogger.Msg("[Menu] Multiplayer button pressed");
                    if (MenuManager.lastHand == null || !MenuManager.lastHand.active) return; // native VR click gate
                    MenuManager.ButtonPressFeedback(true);
                    menuManager?.OpenMenu();
                });

                var textGO = new GameObject("Label");
                textGO.transform.SetParent(buttonGO.transform, false);
                var textRT = textGO.AddComponent<RectTransform>();
                textRT.anchoredPosition = Vector2.zero;
                textRT.sizeDelta = new Vector2(size.x - 20f, size.y - 10f);

                var tmp = textGO.AddComponent<TextMeshProUGUI>();
                tmp.text = "Multiplayer";
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = 32;
                tmp.color = Color.white;
                tmp.raycastTarget = false;

                // Match the Heavy Bag button's box style exactly (sprite, tint, font) so the
                // Multiplayer button reads as a native menu button. Fall back to the generic
                // captured style if Heavy Bag couldn't be found/copied.
                if (heavyBag == null || !MultiplayerMenuManager.StyleButtonFrom(heavyBag, img, btn, tmp))
                {
                    MultiplayerMenuManager.CaptureNativeButtonStyle(mainMenuTransform);
                    MultiplayerMenuManager.StyleButton(img, btn, tmp);
                }

                // Own VR raycaster + collider, same as the native menus, so the fist ray
                // reliably hits this button regardless of the game's canvas-collider bounds.
                // Higher sortingOrder also means it wins if it overlaps another button, so
                // it can't accidentally fall through to Settings.
                MultiplayerMenuManager.MakeVRInteractable(buttonGO, size.x, size.y);

                MelonLogger.Msg("[Menu] \u2713 Multiplayer button created successfully");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Menu] \u2717 AddMultiplayerButton error: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        // Finds a main-menu button by its visible label (e.g. "heavy bag") so we can
        // reposition it. Checks both TMP and uGUI Text, and walks up to the nearest
        // Selectable so we grab the whole button, not just its label.
        private static RectTransform FindButtonByLabel(Transform root, string label)
        {
            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
                if (LabelMatches(tmp != null ? tmp.text : null, label))
                    return SelectableAncestor(tmp.transform, root);

            foreach (var t in root.GetComponentsInChildren<Text>(true))
                if (LabelMatches(t != null ? t.text : null, label))
                    return SelectableAncestor(t.transform, root);

            return null;
        }

        private static bool LabelMatches(string text, string label)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim().ToLowerInvariant();
            return t == label || t.StartsWith(label);
        }

        // Slides target horizontally so it lines up in columnRef's column, keeping its
        // own row. Done in world space so it works across different parent containers.
        private static void AlignColumn(RectTransform target, RectTransform columnRef)
        {
            Vector3 right = columnRef.right.normalized;
            float horiz = Vector3.Dot(columnRef.position - target.position, right);
            target.position = target.position + right * horiz;
        }

        private static RectTransform SelectableAncestor(Transform node, Transform root)
        {
            Transform cur = node;
            while (cur != null && cur != root)
            {
                if (cur.GetComponent<Selectable>() != null) return cur as RectTransform;
                cur = cur.parent;
            }
            return (node.parent as RectTransform) ?? (node as RectTransform);
        }
    }
}