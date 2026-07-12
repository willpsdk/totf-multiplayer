using System;
using System.Collections;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using MelonLoader;
using TotF;

namespace ToFMultiplayer
{
    public class MultiplayerMenuManager : MonoBehaviour
    {
        public static MultiplayerMenuManager Instance { get; private set; }

        private enum Screen { Main, EnterCode, HostLobby, JoinWaiting, Browse, Queue }

        private const string JoinCodeChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        private const int JoinCodeLength = 6;

        private CanvasGroup _canvasGroup;

        // The game's home menu controller. We drive open/close through it so the
        // multiplayer menu behaves exactly like the native Fight menu: opening it
        // closes every other menu, closing it returns the player to the main menu.
        private HomeMenuManager _homeManager;

        private GameObject _mainScreen;
        private GameObject _enterCodeScreen;
        private GameObject _hostLobbyScreen;
        private GameObject _joinWaitingScreen;
        private GameObject _browseScreen;
        private GameObject _queueScreen;

        private Screen _current = Screen.Main;

        // ── Main screen state (lobby type switch)
        private bool _createPublic = false;
        private TextMeshProUGUI _lobbyTypeLabel;

        // ── Host lobby screen state
        private TextMeshProUGUI _hostCodeText;
        private TextMeshProUGUI _hostStatusText;
        private Button _startLobbyButton;
        private TextMeshProUGUI _startLobbyLabel;
        private bool _hostIsPublic;
        private Coroutine _hostLoop;

        // ── "HOST vs CLIENT" avatar panels (Steam profile pictures)
        // Worldwide Elo leaderboard (left side of the home screen)
        private TextMeshProUGUI[] _lbRows;
        private TextMeshProUGUI _lbSelf;
        private Coroutine _lbLoop;

        /// <summary>The "this is you" card: avatar on top, name, gold rating, W/L/D.</summary>
        private class PlayerCard
        {
            public RawImage Img;
            public TextMeshProUGUI Name, Elo, Record;
        }
        private PlayerCard _mainCard, _browseCard;

        private class VsPanel
        {
            public RawImage LeftImg, RightImg;
            public TextMeshProUGUI LeftName, RightName;
            public TextMeshProUGUI LeftElo, RightElo;
            public TextMeshProUGUI VsText;      // doubles as the match-start countdown
            public TextMeshProUGUI Stakes;      // "Win +12  ·  Lose −20"
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
        private VsPanel _hostVs;    // host screen:  left = me (host),  right = joiner
        private VsPanel _joinVs;    // join screen:  left = host,       right = me
        private Coroutine _joinLoop;
        private static readonly Dictionary<ulong, Texture2D> _avatarCache = new Dictionary<ulong, Texture2D>();

        // ── Enter-code screen state
        private string _enteredCode = "";
        private TextMeshProUGUI _codeDisplayText;

        // ── Join waiting screen state
        private TextMeshProUGUI _joinWaitingText;

        // ── Browse screen state (a scrollable multi-row lobby list, paged in blocks of rows)
        private const int BrowseVisibleRows = 6;
        private TextMeshProUGUI _browseStatusText;
        private Button[] _browseRowButtons;
        private TextMeshProUGUI[] _browseRowLabels;
        private Image[][] _browseRowPingBars;
        private int _browsePageStart;
        private List<LobbyBrowser.LobbyInfo> _browseLobbies = new List<LobbyBrowser.LobbyInfo>();

        // ── Queue (P2P matchmaking) state
        private TextMeshProUGUI _queueStatusBig;
        private TextMeshProUGUI _queueElapsedText;
        private TextMeshProUGUI _queueEstText;
        private TextMeshProUGUI _queueDetailText;
        private Coroutine _queueLoop;
        private bool _queueActive;
        private float _queueStartTime;

        // Physical collider of the menu plane; handed to the laser pointer as its aim target.
        private BoxCollider _menuCollider;

        private static readonly Color ButtonColor = new Color(0.2f, 0.6f, 0.85f);
        private static readonly Color ButtonHighlight = new Color(0.3f, 0.72f, 1f);
        private static readonly Color ButtonPressed = new Color(0.12f, 0.45f, 0.65f);

        // ── Native button style, captured once from the game's own menu buttons so ours match.
        private static bool _styleCaptured;
        private static bool _hasNativeColors;
        private static ColorBlock _nativeColors;
        private static Sprite _nativeSprite;
        private static Image.Type _nativeSpriteType = Image.Type.Sliced;
        private static TMP_FontAsset _nativeFont;
        private static Material _nativeFontMat;

        public static MultiplayerMenuManager CreateMultiplayerMenu(
            HomeMenuManager homeManager, MainMenuManager mainMenu)
        {
            MelonLogger.Msg("[Menu] Creating multiplayer menu...");

            try
            {
                // Grab the game's own button look (background sprite + font + tint colors) so
                // our buttons visually match the native menu instead of flat blue rectangles.
                CaptureNativeButtonStyle(mainMenu != null ? mainMenu.transform : null);

                // We anchor this as a sibling of the game's own main menu, under the same
                // world-space Canvas — that's the whole trick to making it clickable in VR.
                // Buttons only get the fist ray if they live under a Canvas with a
                // VRGraphicRaycaster + canvasCollider, and the native menus already have
                // that. Matching the main menu's transform puts us right on that plane.
                var siblingRT = mainMenu.transform as RectTransform;
                Transform parent = siblingRT != null ? siblingRT.parent : mainMenu.transform.parent;

                var menuGO = new GameObject("MultiplayerMenu");
                var rt = menuGO.AddComponent<RectTransform>();
                menuGO.transform.SetParent(parent, false);

                if (siblingRT != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.localPosition = siblingRT.localPosition;
                    rt.localRotation = siblingRT.localRotation;
                    rt.localScale = siblingRT.localScale;
                }
                else
                {
                    rt.anchoredPosition = Vector2.zero;
                }
                rt.sizeDelta = new Vector2(900, 580);

                var bg = menuGO.AddComponent<Image>();
                bg.color = new Color(0.05f, 0.05f, 0.05f, 0.9f);
                bg.raycastTarget = true;

                // Needs its own VR raycaster + collider or the fist ray can't hit it at all.
                // The native menus are clickable because their Canvas has a VRGraphicRaycaster
                // whose canvasCollider is a physical quad on the menu plane (see
                // VRGraphicRaycaster.RaycastToCanvas) — relying on the game's shared collider
                // is fragile since our panel can end up outside its bounds. This just builds
                // the same setup ourselves so it's self-contained.
                MakeVRInteractable(menuGO, 900f, 580f);

                var cg = menuGO.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
                cg.interactable = false;
                cg.blocksRaycasts = false;

                var mgr = menuGO.AddComponent<MultiplayerMenuManager>();
                Instance = mgr;
                mgr._canvasGroup = cg;
                mgr._homeManager = homeManager;
                mgr._menuCollider = menuGO.GetComponent<BoxCollider>();

                mgr._mainScreen = mgr.BuildMainScreen(menuGO.transform);
                mgr._enterCodeScreen = mgr.BuildEnterCodeScreen(menuGO.transform);
                mgr._hostLobbyScreen = mgr.BuildHostLobbyScreen(menuGO.transform);
                mgr._joinWaitingScreen = mgr.BuildJoinWaitingScreen(menuGO.transform);
                mgr._browseScreen = mgr.BuildBrowseScreen(menuGO.transform);
                mgr._queueScreen = mgr.BuildQueueScreen(menuGO.transform);

                CreateCloseButton(menuGO.transform, mgr);

                mgr.ShowScreen(Screen.Main);
                menuGO.SetActive(false); // hidden until opened

                MelonLogger.Msg("[Menu] Multiplayer menu created");
                return mgr;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Menu] CreateMultiplayerMenu error: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        // --------------------------------------------------------
        // Open / Close
        // --------------------------------------------------------

        public void OpenMenu()
        {
            MelonLogger.Msg("[Menu] OpenMenu()");

            // Mirror HomeMenuManager.OpenFightMenu(): hide every native menu first so the
            // multiplayer menu takes over the same spot, exactly like the Fight button does.
            if (_homeManager != null)
                _homeManager.CloseAllMenus();

            gameObject.SetActive(true);
            if (_canvasGroup != null)
            {
                _canvasGroup.interactable = true;
                _canvasGroup.blocksRaycasts = true;
            }

            // Resume on the correct screen if we're mid-session (menu was closed/reopened).
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHosting)
                ShowScreen(Screen.HostLobby);
            else if (nm != null && nm.IsConnected)
                ShowScreen(Screen.JoinWaiting);
            else
                ShowScreen(Screen.Main);

            StartCoroutine(FadeCanvas(0f, 1f, 0.25f));
        }

        public void CloseMenu()
        {
            // Closing the menu ends an active queue — otherwise we'd keep hosting an
            // invisible matchmaking lobby the player forgot about.
            if (_queueActive) CancelQueue();

            StartCoroutine(FadeCanvas(1f, 0f, 0.25f, () =>
            {
                gameObject.SetActive(false);
                if (_canvasGroup != null)
                {
                    _canvasGroup.interactable = false;
                    _canvasGroup.blocksRaycasts = false;
                }

                if (LaserPointer.Instance != null)
                    LaserPointer.Instance.SetActive(false);

                if (_homeManager != null)
                    _homeManager.OpenMainMenu();
            }));
        }

        private void ShowScreen(Screen screen)
        {
            _current = screen;

            if (_hostLoop != null) { StopCoroutine(_hostLoop); _hostLoop = null; }
            if (_joinLoop != null) { StopCoroutine(_joinLoop); _joinLoop = null; }

            _mainScreen.SetActive(screen == Screen.Main);
            _enterCodeScreen.SetActive(screen == Screen.EnterCode);
            _hostLobbyScreen.SetActive(screen == Screen.HostLobby);
            _joinWaitingScreen.SetActive(screen == Screen.JoinWaiting);
            _browseScreen.SetActive(screen == Screen.Browse);
            _queueScreen.SetActive(screen == Screen.Queue);

            switch (screen)
            {
                case Screen.Main: RefreshPlayerCard(_mainCard); RefreshLeaderboard(); break;
                case Screen.EnterCode: OnEnterCodeScreenOpened(); break;
                case Screen.HostLobby: OnHostLobbyScreenOpened(); break;
                case Screen.JoinWaiting: OnJoinWaitingScreenOpened(); break;
                case Screen.Browse: OnBrowseScreenOpened(); break;
                case Screen.Queue: OnQueueScreenOpened(); break;
            }

            // The Server Browser list and the Enter-Code keypad are fiddly to fist-poke, so on
            // those screens turn on the VR laser pointer (aim + trigger to click). Other screens
            // use the normal fist input.
            bool useLaser = (screen == Screen.Browse || screen == Screen.EnterCode);
            var laser = LaserPointer.GetOrCreate();
            laser.SetTargets(_menuCollider);
            laser.SetActive(useLaser);
        }

        // --------------------------------------------------------
        // MAIN screen
        // --------------------------------------------------------

        private GameObject BuildMainScreen(Transform parent)
        {
            var screen = NewScreen(parent, "MainScreen");

            CreateLabel(screen.transform, "MULTIPLAYER", 0, 215, 48);
            CreateDivider(screen.transform, 178);
            var sub = CreateLabel(screen.transform, "Welcome to Thrill Of The Fight Multiplayer!", 0, 152, 22);
            sub.color = new Color(1f, 1f, 1f, 0.65f);

            CreateMenuButton(screen.transform, "Queue for Match", 0, 104, 480, 66, OnQueueClicked);

            CreateMenuButton(screen.transform, "Create Lobby", 0, 30, 480, 66,
                () => BeginHosting(_createPublic));

            // Lobby-type switch: one tap flips between Private and Public. The Create
            // Lobby button always uses whatever this shows.
            var typeBtn = CreateMenuButton(screen.transform, "", 0, -46, 480, 48, ToggleLobbyType);
            _lobbyTypeLabel = typeBtn != null ? typeBtn.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            RefreshLobbyTypeLabel();

            CreateMenuButton(screen.transform, "Enter Code", 0, -114, 480, 66,
                () => ShowScreen(Screen.EnterCode));

            CreateMenuButton(screen.transform, "Server Browser", 0, -186, 480, 66,
                () => ShowScreen(Screen.Browse));

            // Your player card on the right: avatar, name, rating, record.
            _mainCard = CreatePlayerCard(screen.transform, 345, -30);

            // Worldwide Elo top-10 on the left, mirroring the player card.
            BuildLeaderboardPanel(screen.transform, -345, -35);

            return screen;
        }

        private void ToggleLobbyType()
        {
            _createPublic = !_createPublic;
            RefreshLobbyTypeLabel();
        }

        private void RefreshLobbyTypeLabel()
        {
            if (_lobbyTypeLabel == null) return;
            _lobbyTypeLabel.text = _createPublic
                ? "Lobby Type:  PUBLIC  ◀▶"
                : "Lobby Type:  PRIVATE  ◀▶";
        }

        // --------------------------------------------------------
        // QUEUE (P2P matchmaking) screen
        // --------------------------------------------------------

        private GameObject BuildQueueScreen(Transform parent)
        {
            var screen = NewScreen(parent, "QueueScreen");

            CreateLabel(screen.transform, "MATCHMAKING", 0, 215, 44);
            CreateDivider(screen.transform, 180);

            _queueStatusBig = CreateLabel(screen.transform, "QUEUING", 0, 110, 42);
            _queueElapsedText = CreateLabel(screen.transform, "Time in queue: 0:00", 0, 48, 26);
            _queueEstText = CreateLabel(screen.transform, "Est. wait: —", 0, 2, 22);
            _queueEstText.color = new Color(1f, 1f, 1f, 0.65f);
            _queueDetailText = CreateLabel(screen.transform, "", 0, -45, 20);
            _queueDetailText.color = new Color(1f, 1f, 1f, 0.65f);

            var rating = CreateLabel(screen.transform, "", 0, -95, 20);
            rating.color = new Color(1f, 1f, 1f, 0.55f);
            rating.text = $"Your rating: {EloManager.Rating:F0}";

            CreateMenuButton(screen.transform, "Cancel Queue", 0, -175, 280, 60, CancelQueue);

            return screen;
        }

        private void OnQueueClicked()
        {
            var nm = MultiplayerPlugin.EnsureNetworkManagerExists();
            if (nm.IsHosting || nm.IsConnected)
            {
                MelonLogger.Warning("[Menu] Queue pressed while already in a lobby — ignoring");
                return;
            }
            ShowScreen(Screen.Queue);
        }

        private void OnQueueScreenOpened()
        {
            _queueActive = true;
            _queueStartTime = Time.time;
            _queueStatusBig.text = "QUEUING";
            _queueEstText.text = "Est. wait: —";
            _queueDetailText.text = "Searching for opponents...";
            _queueLoop = StartCoroutine(QueueLoop());
        }

        private void CancelQueue()
        {
            _queueActive = false;
            if (_queueLoop != null) { StopCoroutine(_queueLoop); _queueLoop = null; }

            MultiplayerPlugin.AutoStartWhenReady = false;
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHosting) nm.EndLobby();
            else if (nm != null && nm.IsConnected) nm.Disconnect();

            MelonLogger.Msg("[Menu] Queue cancelled");
            ShowScreen(Screen.Main);
        }

        /// <summary>
        /// Serverless P2P matchmaking ("host-and-scan"):
        ///   1. Immediately host a matchmaking lobby, so every queuer is ALWAYS
        ///      discoverable — there is never a moment where two people are both
        ///      searching and nobody is hosting.
        ///   2. While hosting, re-search Steam every few seconds for other queue
        ///      lobbies. Seeing one means two queuers found each other; exactly one
        ///      of them must switch from host to joiner.
        ///   3. Tie-break is deterministic: only the player with the SMALLER lobby ID
        ///      closes their slot and joins (closest Elo first). The other player
        ///      keeps hosting and receives them. No randomness, no missed windows.
        ///   4. If the join fails (lobby vanished/full), reopen our own slot and
        ///      keep scanning.
        /// </summary>
        private IEnumerator QueueLoop()
        {
            var nm = MultiplayerPlugin.EnsureNetworkManagerExists();
            var browser = MultiplayerPlugin.EnsureLobbyBrowserExists();

            while (_queueActive)
            {
                UpdateQueueTimers();

                if (nm.IsConnected)
                {
                    _queueStatusBig.text = "MATCH FOUND!";
                    _queueEstText.text = "Opponent connected";
                    _queueDetailText.text = "Starting match...";
                    yield break;    // host auto-starts; joiner receives START_MATCH
                }

                // ── Always be discoverable: keep our own queue slot open.
                if (!nm.IsHosting)
                {
                    MultiplayerPlugin.AutoStartWhenReady = true;
                    nm.HostGame(isPublic: false, matchmaking: true);
                    _queueDetailText.text = "Queue slot open — scanning for opponents...";

                    // Wait for Steam to hand us the lobby ID (needed for the tie-break).
                    float c = 0f;
                    while (_queueActive && nm.CurrentLobbyID.m_SteamID == 0 && c < 5f)
                    {
                        yield return new WaitForSeconds(0.25f);
                        c += 0.25f;
                        UpdateQueueTimers();
                    }
                    continue;
                }

                // ── SCAN while hosting — we stay visible the whole time.
                browser.SearchMatchmakingLobbies();
                float t = 0f;
                while (browser.IsSearching && t < 8f && _queueActive && !nm.IsConnected)
                {
                    yield return new WaitForSeconds(0.25f);
                    t += 0.25f;
                    UpdateQueueTimers();
                }
                if (!_queueActive || nm.IsConnected) continue;

                // Two kinds of candidates:
                //  • Other QUEUE slots — tie-break applies: we only join lobbies with a
                //    LARGER ID than ours. The other queuer runs the same rule, sees our
                //    smaller ID, and stays put — so exactly one side switches.
                //  • SERVER-BROWSER lobbies — the host is idling, not scanning, so no
                //    tie-break is needed: the queuer always does the joining.
                // Queue slots are preferred (both sides queued → instant auto-start).
                ulong myLobby = nm.CurrentLobbyID.m_SteamID;
                var candidates = new List<LobbyBrowser.LobbyInfo>();
                var publicLobbies = new List<LobbyBrowser.LobbyInfo>();
                foreach (var l in browser.GetMatchmakingLobbies())
                {
                    if (l.IsMatchmaking) { if (l.LobbyID.m_SteamID > myLobby) candidates.Add(l); }
                    else publicLobbies.Add(l);
                }
                bool joiningPublic = candidates.Count == 0 && publicLobbies.Count > 0;
                if (joiningPublic) candidates = publicLobbies;

                if (candidates.Count == 0)
                {
                    // Nobody else queuing — or their ID is smaller and THEY will join us.
                    _queueEstText.text = "Est. wait: waiting for another player to queue";
                    _queueDetailText.text = "Queue slot open — scanning for opponents...";
                    float idle = 0f;
                    while (_queueActive && idle < 6f && !nm.IsConnected)
                    {
                        yield return new WaitForSeconds(0.5f);
                        idle += 0.5f;
                        UpdateQueueTimers();
                    }
                    continue;
                }

                // Fairest opponent first: smallest Elo difference to us.
                float myElo = EloManager.Rating;
                candidates.Sort((a, b) =>
                    Mathf.Abs((a.HostElo > 0f ? a.HostElo : EloManager.DEFAULT_RATING) - myElo).CompareTo(
                    Mathf.Abs((b.HostElo > 0f ? b.HostElo : EloManager.DEFAULT_RATING) - myElo)));
                var target = candidates[0];

                if (nm.IsConnected) continue;   // someone joined US during the scan
                _queueEstText.text = "Est. wait: under 15s";
                _queueDetailText.text = $"Found {target.HostName} — connecting...";
                nm.EndLobby();                  // close our slot: we're the designated joiner
                browser.JoinLobby(target.LobbyID);

                float w = 0f;
                while (!nm.IsConnected && w < 10f && _queueActive)
                {
                    yield return new WaitForSeconds(0.25f);
                    w += 0.25f;
                    UpdateQueueTimers();
                }

                if (nm.IsConnected && joiningPublic)
                {
                    // A browser host starts the match by hand — hand over to the normal
                    // join screen (VS panel, "waiting for lobby start") instead of
                    // sitting on "Starting match..." forever.
                    _queueActive = false;
                    MultiplayerPlugin.AutoStartWhenReady = false;
                    ShowScreen(Screen.JoinWaiting);
                    yield break;
                }
                if (nm.IsConnected || !_queueActive) continue;

                // Their lobby vanished or filled — reopen our slot and keep scanning.
                _queueDetailText.text = "Couldn't connect — reopening queue slot...";
                nm.Disconnect();
                yield return new WaitForSeconds(1f);
            }
        }

        private void UpdateQueueTimers()
        {
            if (_queueElapsedText == null) return;
            float elapsed = Time.time - _queueStartTime;
            int m = (int)(elapsed / 60f);
            int s = (int)(elapsed % 60f);
            _queueElapsedText.text = $"Time in queue: {m}:{s:D2}";
        }

        // --------------------------------------------------------
        // ENTER CODE screen
        // --------------------------------------------------------

        private GameObject BuildEnterCodeScreen(Transform parent)
        {
            var screen = NewScreen(parent, "EnterCodeScreen");

            CreateLabel(screen.transform, "ENTER LOBBY CODE", 0, 218, 34);
            CreateDivider(screen.transform, 192);
            _codeDisplayText = CreateLabel(screen.transform, "_ _ _ _ _ _", 0, 158, 46);

            // On-screen keypad: all 36 join-code characters in a tidy 9x4 grid, evenly spaced.
            const int cols = 9;
            const float cellW = 80f, cellH = 48f;
            const float stepX = cellW + 4f, stepY = cellH + 6f;
            const float startX = -((cols - 1) * stepX) / 2f;
            const float startY = 95f;

            for (int i = 0; i < JoinCodeChars.Length; i++)
            {
                int row = i / cols;
                int col = i % cols;
                char c = JoinCodeChars[i];
                float x = startX + col * stepX;
                float y = startY - row * stepY;
                CreateMenuButton(screen.transform, c.ToString(), x, y, cellW, cellH,
                    () => AppendCodeChar(c));
            }

            CreateMenuButton(screen.transform, "Back", -330, 215, 120, 48, () => ShowScreen(Screen.Main));
            CreateMenuButton(screen.transform, "Delete", -150, -140, 210, 54, DeleteCodeChar);
            CreateMenuButton(screen.transform, "Join", 150, -140, 210, 54, OnJoinByCodeClicked);

            return screen;
        }

        private void OnEnterCodeScreenOpened()
        {
            _enteredCode = "";
            RefreshCodeDisplay();
        }

        private void AppendCodeChar(char c)
        {
            if (_enteredCode.Length >= JoinCodeLength) return;
            _enteredCode += c;
            RefreshCodeDisplay();
        }

        private void DeleteCodeChar()
        {
            if (_enteredCode.Length == 0) return;
            _enteredCode = _enteredCode.Substring(0, _enteredCode.Length - 1);
            RefreshCodeDisplay();
        }

        private void RefreshCodeDisplay()
        {
            if (_codeDisplayText == null) return;
            var padded = _enteredCode.PadRight(JoinCodeLength, '_');
            // space the characters so the blanks are readable
            _codeDisplayText.text = string.Join(" ", padded.ToCharArray());
        }

        private void OnJoinByCodeClicked()
        {
            if (_enteredCode.Length < JoinCodeLength)
            {
                _codeDisplayText.text = "Enter all 6 characters";
                return;
            }

            MelonLogger.Msg($"[Menu] Joining by code: {_enteredCode}");
            var nm = MultiplayerPlugin.EnsureNetworkManagerExists();
            nm.JoinByCode(_enteredCode);
            ShowScreen(Screen.JoinWaiting);
        }

        // --------------------------------------------------------
        // HOST LOBBY screen
        // --------------------------------------------------------

        private GameObject BuildHostLobbyScreen(Transform parent)
        {
            var screen = NewScreen(parent, "HostLobbyScreen");

            CreateLabel(screen.transform, "YOUR LOBBY", 0, 215, 44);
            CreateDivider(screen.transform, 180);

            _hostCodeText = CreateLabel(screen.transform, "Code: (generating...)", 0, 135, 36);

            // HOST vs CLIENT — Steam profile pictures, filled in as players are known.
            _hostVs = CreateVsPanel(screen.transform, 30f);

            _hostStatusText = CreateLabel(screen.transform, "Waiting for a player to join...", 0, -124, 24);

            _startLobbyButton = CreateMenuButton(screen.transform, "Start Lobby", 0, -178, 340, 64, OnStartLobbyClicked);
            if (_startLobbyButton != null)
            {
                _startLobbyButton.interactable = false;
                _startLobbyLabel = _startLobbyButton.GetComponentInChildren<TextMeshProUGUI>(true);
            }

            CreateMenuButton(screen.transform, "Cancel", 0, -248, 240, 54, OnCancelHostClicked);

            return screen;
        }

        private void BeginHosting(bool isPublic)
        {
            _hostIsPublic = isPublic;

            var nm = MultiplayerPlugin.EnsureNetworkManagerExists();
            if (nm.IsHosting || nm.IsConnected)
            {
                MelonLogger.Warning("[Menu] Already hosting/connected — reusing existing lobby");
            }
            else
            {
                MelonLogger.Msg($"[Menu] Creating {(isPublic ? "PUBLIC" : "PRIVATE")} lobby...");
                nm.HostGame(isPublic: isPublic);
            }

            ShowScreen(Screen.HostLobby);
        }

        private void OnHostLobbyScreenOpened()
        {
            bool debug = MultiplayerPlugin.DebugMode;

            // Debug mode allows starting alone: the ghost mirrors your own movements.
            if (_startLobbyButton != null) _startLobbyButton.interactable = debug;
            if (_startLobbyLabel != null) _startLobbyLabel.text = debug ? "Start Solo (Debug)" : "Start Lobby";

            // Left slot: me (the host), immediately. Right slot: joiner, once known.
            ResetVsPanel(_hostVs, "Waiting...");
            SetVsSlotToSelf(_hostVs, selfOnLeft: true);

            _hostCodeText.text = "Code: (generating...)";
            _hostStatusText.text = debug
                ? "Waiting for a player...  or start solo to test the ghost."
                : "Waiting for a player to join...";
            _hostLoop = StartCoroutine(HostLobbyLoop());
        }

        private IEnumerator HostLobbyLoop()
        {
            var nm = NetworkManager.Instance;

            // 1) Wait for Steam to hand us the join code.
            float elapsed = 0f;
            while (nm != null && nm.IsHosting && nm.CurrentJoinCode == null && elapsed < 10f)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }

            if (nm == null || !nm.IsHosting)
                yield break;

            if (nm.CurrentJoinCode != null)
                _hostCodeText.text = "Code: " + nm.CurrentJoinCode;
            else
                _hostCodeText.text = "\u26a0 Lobby taking longer than expected \u2014 check Steam";

            // 2) Wait for a player to join, then enable Start Lobby.
            while (nm != null && nm.IsHosting && !nm.IsConnected)
                yield return new WaitForSeconds(0.2f);

            if (nm != null && nm.IsConnected)
            {
                _hostStatusText.text = "Player joined! Press Start Lobby.";
                if (_startLobbyLabel != null) _startLobbyLabel.text = "Start Lobby";
                if (_startLobbyButton != null) _startLobbyButton.interactable = true;

                // 3) Fill the right slot with the joiner's Steam profile + rating + stakes.
                yield return FillOpponentSlot(_hostVs, opponentOnLeft: false);
            }
        }

        private void OnStartLobbyClicked()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) return;

            // Debug mode may start without a joiner \u2014 the ghost mirrors the host.
            if (!nm.IsConnected && !(MultiplayerPlugin.DebugMode && nm.IsHosting))
            {
                MelonLogger.Warning("[Menu] Start Lobby pressed but no player has joined yet");
                return;
            }
            MultiplayerPlugin.StartMatchAsHost();
        }

        private void OnCancelHostClicked()
        {
            var nm = NetworkManager.Instance;
            if (nm != null && nm.IsHosting) nm.EndLobby();
            MultiplayerPlugin.ShowWaitingForOpponentOverlay(false);
            ShowScreen(Screen.Main);
        }

        // --------------------------------------------------------
        // JOIN WAITING screen
        // --------------------------------------------------------

        private GameObject BuildJoinWaitingScreen(Transform parent)
        {
            var screen = NewScreen(parent, "JoinWaitingScreen");

            CreateLabel(screen.transform, "IN LOBBY", 0, 215, 44);
            CreateDivider(screen.transform, 180);
            _joinWaitingText = CreateLabel(screen.transform, "Waiting for Lobby Start", 0, 145, 30);

            // HOST vs YOU — Steam profile pictures.
            _joinVs = CreateVsPanel(screen.transform, 30f);

            var hint = CreateLabel(screen.transform, "The host will start the match shortly.", 0, -124, 22);
            hint.color = new Color(1f, 1f, 1f, 0.65f);

            CreateMenuButton(screen.transform, "Leave", 0, -180, 240, 60, OnLeaveJoinClicked);

            return screen;
        }

        private void OnJoinWaitingScreenOpened()
        {
            if (_joinWaitingText != null)
                _joinWaitingText.text = "Waiting for Lobby Start";

            // Right slot: me, immediately. Left slot: the host, once the connection
            // is finalized and their SteamID is known.
            ResetVsPanel(_joinVs, "Connecting...");
            SetVsSlotToSelf(_joinVs, selfOnLeft: false);
            _joinLoop = StartCoroutine(FillOpponentSlot(_joinVs, opponentOnLeft: true));
        }

        private void OnLeaveJoinClicked()
        {
            NetworkManager.Instance?.Disconnect();
            ShowScreen(Screen.Main);
        }

        // --------------------------------------------------------
        // BROWSE screen
        // --------------------------------------------------------

        private GameObject BuildBrowseScreen(Transform parent)
        {
            var screen = NewScreen(parent, "BrowseScreen");

            CreateLabel(screen.transform, "SERVER BROWSER", 0, 255, 38);
            CreateDivider(screen.transform, 232);
            _browseStatusText = CreateLabel(screen.transform, "Searching...", 0, 205, 24);

            // A proper multi-row list: each visible lobby is its own wide row-button you can
            // click (or laser-click) to join. The up/down arrows page through when there are
            // more lobbies than fit on screen.
            _browseRowButtons = new Button[BrowseVisibleRows];
            _browseRowLabels = new TextMeshProUGUI[BrowseVisibleRows];
            _browseRowPingBars = new Image[BrowseVisibleRows][];

            // Rows sit left of the player card; the paging arrows live under the card.
            const float rowW = 600f, rowH = 54f, top = 155f, step = 62f;
            for (int i = 0; i < BrowseVisibleRows; i++)
            {
                int row = i;
                float y = top - i * step;
                var b = CreateMenuButton(screen.transform, "", -70, y, rowW, rowH,
                    () => OnBrowseRowClicked(row));
                _browseRowButtons[i] = b;
                _browseRowLabels[i] = b != null ? b.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                if (_browseRowLabels[i] != null)
                    _browseRowLabels[i].alignment = TextAlignmentOptions.Left;
                // Wifi-style signal bars on the right edge of the row: ping estimate to
                // the host — full green = great, one red bar = rough.
                _browseRowPingBars[i] = b != null
                    ? CreatePingBars(b.transform, rowW / 2f - 52f, 0f)
                    : null;
                if (b != null) b.gameObject.SetActive(false);
            }

            _browseCard = CreatePlayerCard(screen.transform, 345, 60);

            CreateMenuButton(screen.transform, "\u25b2", 310, -105, 64, 56, () => StepBrowsePage(-1));
            CreateMenuButton(screen.transform, "\u25bc", 380, -105, 64, 56, () => StepBrowsePage(1));

            CreateMenuButton(screen.transform, "Refresh", -250, -235, 190, 54,
                () => { MultiplayerPlugin.EnsureLobbyBrowserExists(); OnBrowseScreenOpened(); });

            CreateMenuButton(screen.transform, "Back", -370, 255, 120, 48, () => ShowScreen(Screen.Main));

            return screen;
        }

        private void OnBrowseScreenOpened()
        {
            RefreshPlayerCard(_browseCard);
            var browser = MultiplayerPlugin.EnsureLobbyBrowserExists();
            browser.SearchLobbies();
            _browseStatusText.text = "Searching...";
            _browsePageStart = 0;
            for (int i = 0; i < BrowseVisibleRows; i++) SetRow(i, -1);
            StartCoroutine(WaitForBrowseResults(browser));
        }

        private IEnumerator WaitForBrowseResults(LobbyBrowser browser)
        {
            float elapsed = 0f;
            const float timeout = 8f;
            while (browser.IsSearching && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.2f);
                elapsed += 0.2f;
            }

            _browseLobbies = browser.GetDiscoveredLobbies();
            _browsePageStart = 0;
            RefreshBrowseList();
        }

        private void StepBrowsePage(int dir)
        {
            int total = _browseLobbies.Count;
            if (total <= BrowseVisibleRows) return;
            int maxStart = Mathf.Max(0, total - BrowseVisibleRows);
            _browsePageStart = Mathf.Clamp(_browsePageStart + dir * BrowseVisibleRows, 0, maxStart);
            RefreshBrowseList();
        }

        private void RefreshBrowseList()
        {
            int total = _browseLobbies.Count;
            if (total == 0)
            {
                _browseStatusText.text = "No open lobbies found";
                for (int i = 0; i < BrowseVisibleRows; i++) SetRow(i, -1);
                return;
            }

            int maxStart = Mathf.Max(0, total - BrowseVisibleRows);
            _browsePageStart = Mathf.Clamp(_browsePageStart, 0, maxStart);
            int shown = Mathf.Min(BrowseVisibleRows, total - _browsePageStart);

            _browseStatusText.text = total <= BrowseVisibleRows
                ? (total == 1 ? "1 lobby found" : $"{total} lobbies found")
                : $"Lobbies {_browsePageStart + 1}\u2013{_browsePageStart + shown} of {total}";

            for (int i = 0; i < BrowseVisibleRows; i++)
                SetRow(i, _browsePageStart + i);
        }

        private void SetRow(int rowIndex, int lobbyIndex)
        {
            if (_browseRowButtons == null) return;
            var btn = _browseRowButtons[rowIndex];
            if (btn == null) return;
            var lbl = _browseRowLabels[rowIndex];

            if (lobbyIndex < 0 || lobbyIndex >= _browseLobbies.Count)
            {
                btn.gameObject.SetActive(false);
                return;
            }

            var lobby = _browseLobbies[lobbyIndex];
            btn.gameObject.SetActive(true);
            btn.interactable = lobby.IsAvailable;
            if (lbl != null)
                lbl.text = $"{lobby.HostName}{(lobby.IsMatchmaking ? "  [Queue]" : "")}"
                         + $"    {lobby.PlayerCount}/{lobby.MaxPlayers}"
                         + (lobby.PingEstimateMs >= 0 ? $"    {lobby.PingEstimateMs}ms" : "");

            if (_browseRowPingBars != null && _browseRowPingBars[rowIndex] != null)
                UpdatePingBars(_browseRowPingBars[rowIndex], lobby.PingEstimateMs);
        }

        // ── Wifi-style ping indicator ─────────────────────────

        /// <summary>Four ascending signal bars, right-aligned inside a row.</summary>
        private static Image[] CreatePingBars(Transform parent, float xStart, float centerY)
        {
            var bars = new Image[4];
            for (int k = 0; k < 4; k++)
            {
                var go = new GameObject("PingBar" + k);
                go.transform.SetParent(parent, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                float h = 9f + k * 5f;
                rt.sizeDelta = new Vector2(7f, h);
                // bottom-aligned so the bars grow upward like a signal meter
                rt.anchoredPosition = new Vector2(xStart + k * 10f, centerY - (24f - h) / 2f);
                var img = go.AddComponent<Image>();
                img.raycastTarget = false;
                img.color = new Color(1f, 1f, 1f, 0.15f);
                bars[k] = img;
            }
            return bars;
        }

        /// <summary>Lights 1–4 bars, green (fast) → yellow → orange → red (slow). All
        /// bars stay dim when the ping is unknown.</summary>
        private static void UpdatePingBars(Image[] bars, int pingMs)
        {
            int lit;
            Color c;
            if (pingMs < 0) { lit = 0; c = Color.white; }
            else if (pingMs <= 60) { lit = 4; c = new Color(0.25f, 0.9f, 0.3f); }   // green
            else if (pingMs <= 110) { lit = 3; c = new Color(0.75f, 0.9f, 0.2f); }   // yellow-green
            else if (pingMs <= 180) { lit = 2; c = new Color(1f, 0.6f, 0.1f); }     // orange
            else { lit = 1; c = new Color(0.95f, 0.25f, 0.2f); }   // red
            var dim = new Color(1f, 1f, 1f, 0.15f);

            for (int k = 0; k < bars.Length; k++)
            {
                if (bars[k] == null) continue;
                bars[k].color = k < lit ? c : dim;
            }
        }

        private void OnBrowseRowClicked(int rowIndex)
        {
            int lobbyIndex = _browsePageStart + rowIndex;
            if (lobbyIndex < 0 || lobbyIndex >= _browseLobbies.Count) return;
            var lobby = _browseLobbies[lobbyIndex];

            MelonLogger.Msg($"[Menu] Joining public lobby: {lobby.HostName}");
            var browser = MultiplayerPlugin.EnsureLobbyBrowserExists();
            browser.JoinLobby(lobby.LobbyID);

            ShowScreen(Screen.JoinWaiting);
        }

        // --------------------------------------------------------
        // Steam avatar "VS" panel
        // --------------------------------------------------------

        /// <summary>Builds an "(avatar) VS (avatar)" row centered on <paramref name="centerY"/>,
        /// with persona-name labels under each picture. Slots start dim until filled.</summary>
        private VsPanel CreateVsPanel(Transform parent, float centerY)
        {
            const float avatarSize = 110f, xOff = 170f;

            var panel = new VsPanel();
            panel.LeftImg = CreateAvatarSlot(parent, -xOff, centerY, avatarSize);
            panel.RightImg = CreateAvatarSlot(parent, xOff, centerY, avatarSize);

            panel.VsText = CreateLabel(parent, "VS", 0, centerY, 44);
            panel.VsText.fontStyle = FontStyles.Bold;

            // What the match is worth, shown just under the VS between the two pictures.
            panel.Stakes = CreateLabel(parent, "", 0, centerY - 44, 18);
            panel.Stakes.rectTransform.sizeDelta = new Vector2(280f, 26f);
            panel.Stakes.color = new Color(1f, 1f, 1f, 0.7f);

            // Name labels sit centered directly BELOW each picture (same x as the avatar,
            // just under the frame's bottom edge). Constrained + auto-sized so a long
            // Steam name shrinks/ellipsizes inside its own slot instead of spilling
            // sideways into the "VS" or the other player's picture.
            panel.LeftName = CreateLabel(parent, "", -xOff, centerY - 78, 20);
            panel.RightName = CreateLabel(parent, "", xOff, centerY - 78, 20);
            foreach (var nameLbl in new[] { panel.LeftName, panel.RightName })
            {
                nameLbl.rectTransform.sizeDelta = new Vector2(300f, 30f);
                nameLbl.overflowMode = TextOverflowModes.Ellipsis;
                nameLbl.enableAutoSizing = true;
                nameLbl.fontSizeMax = 20f;
                nameLbl.fontSizeMin = 12f;
            }

            // Elo ratings, one line under each name.
            panel.LeftElo = CreateLabel(parent, "", -xOff, centerY - 104, 18);
            panel.RightElo = CreateLabel(parent, "", xOff, centerY - 104, 18);
            foreach (var eloLbl in new[] { panel.LeftElo, panel.RightElo })
            {
                eloLbl.rectTransform.sizeDelta = new Vector2(300f, 24f);
                eloLbl.color = new Color(1f, 0.85f, 0.4f, 0.9f);   // gold-ish, reads as "rating"
            }

            return panel;
        }

        // --------------------------------------------------------
        // Worldwide Elo leaderboard panel
        // --------------------------------------------------------

        private const int LeaderboardRows = 10;

        private void BuildLeaderboardPanel(Transform parent, float cx, float cy)
        {
            var frame = CreatePanel(parent, cx, cy, 184f, 410f);
            frame.color = new Color(0f, 0f, 0f, 0.35f);

            var header = CreateLabel(parent, "WORLD TOP 10", cx, cy + 183f, 16);
            header.rectTransform.sizeDelta = new Vector2(172f, 22f);
            header.color = new Color(1f, 0.85f, 0.4f, 0.9f);

            _lbRows = new TextMeshProUGUI[LeaderboardRows];
            for (int i = 0; i < LeaderboardRows; i++)
            {
                var row = CreateLabel(parent, "", cx, cy + 150f - i * 29f, 14);
                row.rectTransform.sizeDelta = new Vector2(172f, 22f);
                row.alignment = TextAlignmentOptions.Left;
                row.overflowMode = TextOverflowModes.Ellipsis;
                row.color = new Color(1f, 1f, 1f, 0.85f);
                _lbRows[i] = row;
            }

            _lbSelf = CreateLabel(parent, "", cx, cy - 175f, 14);
            _lbSelf.rectTransform.sizeDelta = new Vector2(172f, 22f);
            _lbSelf.color = new Color(1f, 0.85f, 0.4f, 0.9f);
        }

        private static bool _lbSelfPublished;

        private void RefreshLeaderboard()
        {
            if (_lbRows == null) return;
            if (_lbLoop != null) { StopCoroutine(_lbLoop); _lbLoop = null; }

            // Publish our rating once per session so the board has entries even
            // before anyone finishes a match this boot.
            if (!_lbSelfPublished)
            {
                _lbSelfPublished = true;
                EloLeaderboard.UploadRating(EloManager.Rating);
            }

            EloLeaderboard.Refresh();
            _lbLoop = StartCoroutine(FillLeaderboard());
        }

        private IEnumerator FillLeaderboard()
        {
            _lbRows[0].text = "Loading...";
            for (int i = 1; i < LeaderboardRows; i++) _lbRows[i].text = "";
            _lbSelf.text = "";

            // Wait for the download, then keep repainting briefly so persona names
            // resolve (Steam delivers strangers' names asynchronously).
            float waited = 0f;
            while (EloLeaderboard.Refreshing && waited < 10f)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
            }

            for (float t = 0f; t <= 6f; t += 1f)
            {
                PaintLeaderboard();
                yield return new WaitForSeconds(1f);
            }
            _lbLoop = null;
        }

        private void PaintLeaderboard()
        {
            if (EloLeaderboard.Unavailable)
            {
                _lbRows[0].text = "Leaderboard unavailable";
                for (int i = 1; i < LeaderboardRows; i++) _lbRows[i].text = "";
                return;
            }

            var top = EloLeaderboard.Top;
            if (top.Count == 0)
            {
                _lbRows[0].text = "No ranked players yet";
                for (int i = 1; i < LeaderboardRows; i++) _lbRows[i].text = "";
            }
            else
            {
                CSteamID me = SteamUser.GetSteamID();
                for (int i = 0; i < LeaderboardRows; i++)
                {
                    if (i >= top.Count) { _lbRows[i].text = ""; continue; }
                    var e = top[i];
                    string name;
                    try { name = SteamFriends.GetFriendPersonaName(e.User); } catch { name = null; }
                    if (string.IsNullOrEmpty(name) || name == "[unknown]") name = "...";
                    _lbRows[i].text = $"#{e.Rank}  {e.Rating}  {name}";
                    _lbRows[i].color = e.User == me
                        ? new Color(1f, 0.85f, 0.4f, 1f)          // that's you — gold
                        : new Color(1f, 1f, 1f, 0.85f);
                }
            }

            var self = EloLeaderboard.Self;
            _lbSelf.text = self != null ? $"You:  #{self.Rank}  ·  {self.Rating}" : "You:  unranked";
        }

        /// <summary>Builds the framed "you" card centered on (cx, cy): avatar on top,
        /// then persona name, gold rating, and W/L/D record.</summary>
        private PlayerCard CreatePlayerCard(Transform parent, float cx, float cy)
        {
            var frame = CreatePanel(parent, cx, cy, 184f, 244f);
            frame.color = new Color(0f, 0f, 0f, 0.35f);

            var card = new PlayerCard();
            card.Img = CreateAvatarSlot(parent, cx, cy + 58f, 100f);

            card.Name = CreateLabel(parent, "", cx, cy - 16f, 20);
            card.Name.rectTransform.sizeDelta = new Vector2(168f, 30f);
            card.Name.overflowMode = TextOverflowModes.Ellipsis;
            card.Name.enableAutoSizing = true;
            card.Name.fontSizeMax = 20f;
            card.Name.fontSizeMin = 12f;

            card.Elo = CreateLabel(parent, "", cx, cy - 46f, 18);
            card.Elo.rectTransform.sizeDelta = new Vector2(168f, 24f);
            card.Elo.color = new Color(1f, 0.85f, 0.4f, 0.9f);

            card.Record = CreateLabel(parent, "", cx, cy - 72f, 15);
            card.Record.rectTransform.sizeDelta = new Vector2(168f, 22f);
            card.Record.color = new Color(1f, 1f, 1f, 0.55f);

            return card;
        }

        /// <summary>Fills a player card with the local player's live values. Called every
        /// time its screen opens, so the rating/record stay current between matches.</summary>
        private void RefreshPlayerCard(PlayerCard card)
        {
            if (card == null) return;
            try
            {
                card.Name.text = SteamFriends.GetPersonaName();
                card.Elo.text = $"Rating {EloManager.Rating:F0}";
                card.Record.text = $"Wins: {EloManager.Wins}   Losses: {EloManager.Losses}";
            }
            catch (Exception e) { MelonLogger.Warning($"[Menu] RefreshPlayerCard: {e.Message}"); }
            StartCoroutine(FillCardAvatar(card));
        }

        private IEnumerator FillCardAvatar(PlayerCard card)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Texture2D tex = null;
                try { tex = TryGetAvatar(SteamUser.GetSteamID()); } catch { }
                if (tex != null)
                {
                    card.Img.texture = tex;
                    card.Img.color = Color.white;
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        private static RawImage CreateAvatarSlot(Transform parent, float x, float y, float size)
        {
            // dark frame behind the picture so an empty slot still reads as a slot
            var frame = CreatePanel(parent, x, y, size + 8f, size + 8f);
            frame.color = new Color(0f, 0f, 0f, 0.45f);

            var go = new GameObject("Avatar");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(size, size);

            var img = go.AddComponent<RawImage>();
            img.color = new Color(1f, 1f, 1f, 0.12f);   // dim placeholder until a texture arrives
            img.raycastTarget = false;
            return img;
        }

        private void ResetVsPanel(VsPanel panel, string emptySlotLabel)
        {
            if (panel == null) return;
            panel.LeftImg.texture = null;
            panel.RightImg.texture = null;
            panel.LeftImg.color = new Color(1f, 1f, 1f, 0.12f);
            panel.RightImg.color = new Color(1f, 1f, 1f, 0.12f);
            panel.LeftName.text = emptySlotLabel;
            panel.RightName.text = emptySlotLabel;
            panel.LeftElo.text = "";
            panel.RightElo.text = "";
            panel.VsText.text = "VS";
            panel.VsText.fontSize = 44;
            panel.VsText.color = Color.white;
            panel.Stakes.text = "";
        }

        private static void SetVsSlotToSelf(VsPanel panel, bool selfOnLeft)
        {
            try
            {
                var nameLabel = selfOnLeft ? panel.LeftName : panel.RightName;
                var eloLabel = selfOnLeft ? panel.LeftElo : panel.RightElo;
                var img = selfOnLeft ? panel.LeftImg : panel.RightImg;

                nameLabel.text = SteamFriends.GetPersonaName();
                eloLabel.text = $"Rating {EloManager.Rating:F0}";
                var tex = TryGetAvatar(SteamUser.GetSteamID());
                if (tex != null) { img.texture = tex; img.color = Color.white; }
            }
            catch (Exception e) { MelonLogger.Warning($"[Menu] SetVsSlotToSelf: {e.Message}"); }
        }

        /// <summary>Polls until the opponent's SteamID is known, then fills their slot:
        /// name, Elo rating, win/lose stakes, and (once Steam has downloaded it) their
        /// avatar. Steam delivers avatars asynchronously, so the first few
        /// GetLargeFriendAvatar calls for a stranger can return nothing.</summary>
        private IEnumerator FillOpponentSlot(VsPanel panel, bool opponentOnLeft)
        {
            var img = opponentOnLeft ? panel.LeftImg : panel.RightImg;
            var nameLabel = opponentOnLeft ? panel.LeftName : panel.RightName;
            var eloLabel = opponentOnLeft ? panel.LeftElo : panel.RightElo;

            bool statsShown = false;
            float elapsed = 0f;
            const float timeout = 30f;

            while (elapsed < timeout)
            {
                var nm = NetworkManager.Instance;
                CSteamID id = nm != null ? nm.RemotePlayerID : default;

                if (id.m_SteamID != 0)
                {
                    try
                    {
                        string name = SteamFriends.GetFriendPersonaName(id);
                        if (!string.IsNullOrEmpty(name) && name != "[unknown]")
                            nameLabel.text = name;

                        if (!statsShown)
                        {
                            statsShown = true;
                            eloLabel.text = $"Rating {nm.OpponentElo:F0}";
                            UpdateStakes(panel, nm.OpponentElo);
                        }

                        var tex = TryGetAvatar(id);
                        if (tex != null)
                        {
                            img.texture = tex;
                            img.color = Color.white;
                            yield break;    // done — name, rating, stakes and picture are in
                        }
                    }
                    catch (Exception e) { MelonLogger.Warning($"[Menu] FillOpponentSlot: {e.Message}"); }
                }

                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
            }
        }

        /// <summary>Shows what this match is worth: "Win +X  ·  Lose −Y".</summary>
        private static void UpdateStakes(VsPanel panel, float opponentElo)
        {
            float winDelta, lossDelta;
            EloManager.PreviewDeltas(opponentElo, out winDelta, out lossDelta);
            panel.Stakes.text = $"Win +{winDelta:F0}   ·   Lose {lossDelta:F0}";
        }

        /// <summary>Match is starting: count the VS text down (5…1) on whichever screen
        /// is showing. Called by MultiplayerPlugin when START_MATCH fires on either side.</summary>
        public void ShowMatchCountdown(int seconds)
        {
            StartCoroutine(MatchCountdownRoutine(seconds));
        }

        private IEnumerator MatchCountdownRoutine(int seconds)
        {
            if (_startLobbyButton != null) _startLobbyButton.interactable = false;
            if (_current == Screen.HostLobby && _hostStatusText != null)
                _hostStatusText.text = "Match starting!";
            if (_current == Screen.JoinWaiting && _joinWaitingText != null)
                _joinWaitingText.text = "Match starting!";

            bool onVsPanel = _current == Screen.HostLobby || _current == Screen.JoinWaiting;
            TextMeshProUGUI target =
                _current == Screen.HostLobby ? (_hostVs != null ? _hostVs.VsText : null)
                : _current == Screen.JoinWaiting ? (_joinVs != null ? _joinVs.VsText : null)
                : _current == Screen.Queue ? _queueStatusBig
                : null;

            // The VS text swaps into a big gold counter; ResetVsPanel restores it.
            if (target != null && onVsPanel)
            {
                target.fontSize = 60;
                target.color = new Color(1f, 0.85f, 0.4f);
            }

            for (int i = seconds; i >= 1; i--)
            {
                if (target != null)
                    target.text = onVsPanel ? i.ToString() : $"STARTING IN {i}";
                yield return new WaitForSeconds(1f);
            }

            if (target != null)
                target.text = onVsPanel ? "GO!" : "FIGHT!";
        }

        /// <summary>Fetches a user's large (184x184) Steam avatar as a Texture2D, cached per
        /// SteamID. Returns null while Steam is still downloading it — call again later.</summary>
        private static Texture2D TryGetAvatar(CSteamID user)
        {
            if (user.m_SteamID == 0) return null;

            Texture2D cached;
            if (_avatarCache.TryGetValue(user.m_SteamID, out cached) && cached != null)
                return cached;

            try
            {
                int handle = SteamFriends.GetLargeFriendAvatar(user);
                if (handle <= 0)
                {
                    // Not cached by Steam yet — ask it to fetch this user's data and retry later.
                    SteamFriends.RequestUserInformation(user, false);
                    return null;
                }

                uint w, h;
                if (!SteamUtils.GetImageSize(handle, out w, out h) || w == 0 || h == 0)
                    return null;

                byte[] buf = new byte[w * h * 4];
                if (!SteamUtils.GetImageRGBA(handle, buf, buf.Length))
                    return null;

                var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(buf);

                // Steam image rows are top-down, Unity textures are bottom-up — flip vertically.
                var px = tex.GetPixels32();
                var flipped = new Color32[px.Length];
                for (int row = 0; row < (int)h; row++)
                    Array.Copy(px, row * (int)w, flipped, ((int)h - 1 - row) * (int)w, (int)w);
                tex.SetPixels32(flipped);
                tex.Apply();

                _avatarCache[user.m_SteamID] = tex;
                return tex;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Menu] TryGetAvatar: {e.Message}");
                return null;
            }
        }

        // --------------------------------------------------------
        // UI helpers
        // --------------------------------------------------------

        /// <summary>
        /// Replicates the game's native "clickable in VR" setup on <paramref name="go"/>:
        /// a Canvas + a <see cref="VRGraphicRaycaster"/> whose <c>canvasCollider</c> is a
        /// BoxCollider quad sized to (<paramref name="width"/> x <paramref name="height"/>)
        /// on the UI plane. The game's fist ray (VRInputModule.CustomControllerRay, set by the
        /// shared UIPointerController trigger) is raycast against this collider; the hit point is
        /// converted to a canvas point and fed to the normal uGUI raycast. Without this, buttons
        /// under the object are never hit by the fist. worldCamera + sorting are copied from the
        /// game's own raycaster canvas so our overlay uses the same camera and renders on top.
        /// </summary>
        public static void MakeVRInteractable(GameObject go, float width, float height)
        {
            try
            {
                var refRaycaster = UnityEngine.Object.FindObjectOfType<VRGraphicRaycaster>();
                Canvas refCanvas = refRaycaster != null ? refRaycaster.GetComponent<Canvas>() : null;

                var canvas = go.GetComponent<Canvas>();
                if (canvas == null) canvas = go.AddComponent<Canvas>();
                canvas.overrideSorting = true;
                canvas.sortingOrder = (refCanvas != null ? refCanvas.sortingOrder : 0) + 50;
                if (refCanvas != null)
                {
                    canvas.worldCamera = refCanvas.worldCamera;
                    canvas.sortingLayerID = refCanvas.sortingLayerID;
                }
                if (canvas.worldCamera == null && Camera.main != null)
                    canvas.worldCamera = Camera.main;

                var col = go.GetComponent<BoxCollider>();
                if (col == null) col = go.AddComponent<BoxCollider>();
                col.isTrigger = true;                 // Collider.Raycast still hits triggers; avoids blocking the fist
                col.size = new Vector3(width, height, 10f);
                col.center = Vector3.zero;

                var vr = go.GetComponent<VRGraphicRaycaster>();
                if (vr == null) vr = go.AddComponent<VRGraphicRaycaster>();
                vr.canvasCollider = col;

                MelonLogger.Msg($"[Menu] MakeVRInteractable: attached VRGraphicRaycaster + {width}x{height} collider to {go.name}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Menu] MakeVRInteractable error: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Captures the game's own button look once, so our buttons match instead of being flat
        /// blue rectangles. Scans <paramref name="searchRoot"/> for a native <see cref="Button"/>
        /// that has both a background sprite and a TMP label and copies its sprite, tint
        /// <see cref="ColorBlock"/> and font. Falls back to grabbing any TMP font it can find.
        /// </summary>
        public static void CaptureNativeButtonStyle(Transform searchRoot)
        {
            if (_styleCaptured || searchRoot == null) return;
            try
            {
                foreach (var b in searchRoot.GetComponentsInChildren<Button>(true))
                {
                    var lbl = b.GetComponentInChildren<TextMeshProUGUI>(true);
                    var im = b.targetGraphic as Image ?? b.GetComponent<Image>();
                    if (lbl != null && im != null && im.sprite != null)
                    {
                        _nativeSprite = im.sprite;
                        _nativeSpriteType = im.type;
                        _nativeColors = b.colors;
                        _hasNativeColors = true;
                        _nativeFont = lbl.font;
                        _nativeFontMat = lbl.fontSharedMaterial;
                        _styleCaptured = true;
                        MelonLogger.Msg($"[Menu] Captured native button style from '{b.name}'");
                        return;
                    }
                }

                var anyTmp = searchRoot.GetComponentInChildren<TextMeshProUGUI>(true);
                if (anyTmp != null)
                {
                    _nativeFont = anyTmp.font;
                    _nativeFontMat = anyTmp.fontSharedMaterial;
                }
                MelonLogger.Msg("[Menu] No ideal native button found; using font-only style fallback");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Menu] CaptureNativeButtonStyle error: {e.Message}");
            }
        }

        /// <summary>Applies the captured native look to a button we built from scratch.</summary>
        public static void StyleButton(Image img, Button btn, TextMeshProUGUI label)
        {
            if (img != null && _nativeSprite != null)
            {
                img.sprite = _nativeSprite;
                img.type = _nativeSpriteType;
                img.color = Color.white;                     // let the sprite + tint colors show through
                if (btn != null && _hasNativeColors) btn.colors = _nativeColors;
            }
            ApplyLabelStyle(label);
        }

        private static void ApplyLabelStyle(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            if (_nativeFont != null) tmp.font = _nativeFont;
            if (_nativeFontMat != null) tmp.fontSharedMaterial = _nativeFontMat;
        }

        /// <summary>Copies a specific existing button's exact box style (background sprite, tint
        /// colors and font) onto a button we built. Returns false if the source has no usable
        /// Image so the caller can fall back to the generic captured style.</summary>
        public static bool StyleButtonFrom(Transform source, Image img, Button btn, TextMeshProUGUI label)
        {
            if (source == null) return false;
            try
            {
                var srcBtn = source.GetComponent<Button>();
                var srcImg = (srcBtn != null ? srcBtn.targetGraphic as Image : null)
                             ?? source.GetComponent<Image>()
                             ?? source.GetComponentInChildren<Image>(true);
                if (srcImg == null || srcImg.sprite == null) return false;

                if (img != null)
                {
                    img.sprite = srcImg.sprite;
                    img.type = srcImg.type;
                    img.color = srcImg.color;
                    if (btn != null && srcBtn != null) btn.colors = srcBtn.colors;
                }

                var srcLbl = source.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null && srcLbl != null)
                {
                    label.font = srcLbl.font;
                    label.fontSharedMaterial = srcLbl.fontSharedMaterial;
                    label.color = srcLbl.color;
                    label.fontSize = srcLbl.fontSize;
                }

                MelonLogger.Msg($"[Menu] Styled Multiplayer button from '{source.name}'");
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Menu] StyleButtonFrom error: {e.Message}");
                return false;
            }
        }

        private static GameObject NewScreen(Transform parent, string name)
        {
            var screen = new GameObject(name);
            screen.transform.SetParent(parent, false);
            var rt = screen.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(900, 580);
            return screen;
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string text, float x, float y, int fontSize)
        {
            var go = new GameObject("Label_" + (text.Length > 0 ? text.Substring(0, Math.Min(text.Length, 12)) : "Empty"));
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(760, Mathf.Max(fontSize * 1.6f, 44f));

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = fontSize;
            tmp.color = Color.white;
            tmp.enableWordWrapping = false;                 // one line; never wrap/clip
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            ApplyLabelStyle(tmp);

            return tmp;
        }

        /// <summary>Thin horizontal rule under a screen title — gives every screen the same header shape.</summary>
        private static Image CreateDivider(Transform parent, float y)
        {
            var img = CreatePanel(parent, 0f, y, 700f, 3f);
            img.color = new Color(1f, 1f, 1f, 0.25f);
            return img;
        }

        /// <summary>A simple framed background panel used to group content (e.g. a browser row).</summary>
        private static Image CreatePanel(Transform parent, float x, float y, float width, float height)
        {
            var go = new GameObject("Panel");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(width, height);
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.08f);
            img.raycastTarget = false;
            return img;
        }

        private static Button CreateMenuButton(Transform parent, string label, float x, float y,
            float width, float height, Action onClick)
        {
            try
            {
                var go = new GameObject(label.Replace(" ", "") + "Button");
                go.transform.SetParent(parent, false);

                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(x, y);
                rt.sizeDelta = new Vector2(width, height);

                var img = go.AddComponent<Image>();
                img.color = ButtonColor;
                img.raycastTarget = true;

                var btn = go.AddComponent<Button>();
                btn.targetGraphic = img;

                var cb = btn.colors;
                cb.normalColor = ButtonColor;
                cb.highlightedColor = ButtonHighlight;
                cb.pressedColor = ButtonPressed;
                cb.disabledColor = new Color(0.3f, 0.3f, 0.3f);
                btn.colors = cb;

                btn.onClick.AddListener(() =>
                {
                    MelonLogger.Msg($"[Menu] {label} pressed");
                    // Native VR click gate: ignore unless the pointer hand is armed (like
                    // MainMenuManager.LoadFightMenu/LoadSettings). Bypassed when the laser
                    // pointer is driving input, since no fist ever arms lastHand then.
                    if (!LaserPointer.PointerActive &&
                        (MenuManager.lastHand == null || !MenuManager.lastHand.active)) return;
                    try { MenuManager.ButtonPressFeedback(true); } catch { }
                    onClick?.Invoke();
                });

                var tGO = new GameObject("Label");
                tGO.transform.SetParent(go.transform, false);
                var tRT = tGO.AddComponent<RectTransform>();
                tRT.anchorMin = new Vector2(0.5f, 0.5f);
                tRT.anchorMax = new Vector2(0.5f, 0.5f);
                tRT.pivot = new Vector2(0.5f, 0.5f);
                tRT.anchoredPosition = Vector2.zero;
                tRT.sizeDelta = new Vector2(width - 20f, height - 8f);

                var tmp = tGO.AddComponent<TextMeshProUGUI>();
                tmp.text = label;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                tmp.raycastTarget = false;
                // Auto-size + no-wrap so labels always fit their button (fixes overlap/clipping).
                tmp.enableWordWrapping = false;
                tmp.overflowMode = TextOverflowModes.Ellipsis;
                tmp.enableAutoSizing = true;
                tmp.fontSizeMax = Mathf.Clamp(height * 0.5f, 20f, 34f);
                tmp.fontSizeMin = 12f;

                // Apply the captured native look (background sprite, font, tint colors).
                StyleButton(img, btn, tmp);

                return btn;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Menu] CreateMenuButton error: {e.Message}");
                return null;
            }
        }

        /// <summary>Finds the game's own close/X icon among loaded sprites so our close
        /// button matches the native menus. Returns null if none is loaded.</summary>
        private static Sprite FindNativeCloseSprite()
        {
            try
            {
                Sprite best = null;
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (s == null) continue;
                    string n = s.name.ToLowerInvariant();
                    if (n.Contains("close")) return s;              // exact intent — take it
                    if (best == null && (n == "x" || n.Contains("exit_") || n.Contains("_exit")))
                        best = s;
                }
                return best;
            }
            catch { return null; }
        }

        private static void CreateCloseButton(Transform parent, MultiplayerMenuManager mgr)
        {
            var go = new GameObject("CloseButton");
            go.transform.SetParent(parent, false);

            // The whole 84x84 rect is the hitbox — nearly 3x the old clickable area,
            // sized for a fist-poke rather than a precise laser click.
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-6, -6);
            rt.sizeDelta = new Vector2(84, 84);

            var img = go.AddComponent<Image>();
            Sprite native = FindNativeCloseSprite();
            if (native != null)
            {
                img.sprite = native;
                img.color = Color.white;
                img.preserveAspect = true;
            }
            else
            {
                img.color = new Color(0.8f, 0.2f, 0.2f, 0.9f);
            }

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() =>
            {
                if (!LaserPointer.PointerActive &&
                    (MenuManager.lastHand == null || !MenuManager.lastHand.active)) return;
                try { MenuManager.ButtonPressFeedback(true); } catch { }
                mgr.CloseMenu();
            });

            // The × label is only needed on the flat-color fallback; the native sprite
            // already draws its own glyph.
            if (native == null)
            {
                var tGO = new GameObject("X");
                tGO.transform.SetParent(go.transform, false);
                var tRT = tGO.AddComponent<RectTransform>();
                tRT.anchoredPosition = Vector2.zero;
                tRT.sizeDelta = new Vector2(84, 84);
                var tmp = tGO.AddComponent<TextMeshProUGUI>();
                tmp.text = "×";
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = 52;
                tmp.fontStyle = FontStyles.Bold;
                tmp.color = Color.white;
                tmp.raycastTarget = false;
            }
        }

        private IEnumerator FadeCanvas(float from, float to, float duration, Action onComplete = null)
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                if (_canvasGroup != null)
                    _canvasGroup.alpha = Mathf.Lerp(from, to, t / duration);
                yield return null;
            }
            if (_canvasGroup != null)
                _canvasGroup.alpha = to;
            onComplete?.Invoke();
        }
    }
}
