using MelonLoader;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ToFMultiplayer
{
    /// <summary>
    /// Handles lobby discovery and join-by-code through Steam Matchmaking. Two separate
    /// search modes live here: <see cref="SearchLobbies"/> asks Steam for every public
    /// lobby tagged game=thrill-of-the-fight with a free slot, and results land in
    /// _discoveredLobbies for the browser UI. <see cref="JoinByCode"/> instead filters
    /// directly on join_code, works for both public and private lobbies, doesn't need
    /// a prior SearchLobbies call, and keeps its own pending-code state so the two
    /// modes don't step on each other.
    /// </summary>
    public class LobbyBrowser : MonoBehaviour
    {
        public static LobbyBrowser Instance { get; private set; }

        // RequestLobbyList results come back as a Steam call result tied to the specific
        // API call handle, not a broadcast callback. We used to register it as a plain
        // Callback<LobbyMatchList_t>, and that worked on some machines and just silently
        // never fired on others — which was exactly the "my friend sees my lobby but I
        // never see his" bug. CallResult + the returned SteamAPICall_t handle fixes it.
        private CallResult<LobbyMatchList_t> _crLobbyMatchList;

        // Browse state
        private List<LobbyInfo> _discoveredLobbies = new List<LobbyInfo>();
        private bool _isBrowsing = false;
        private float _browseStartTime = 0f;
        private float _lastBrowseTime = 0f;
        private const float BROWSE_COOLDOWN = 5f;
        private const float SEARCH_TIMEOUT = 30f;

        // Join-by-code state
        private bool _isJoiningByCode = false;
        private string _pendingJoinCode = null;

        // Matchmaking-queue search state (lobbies tagged lobby_type=matchmaking)
        private bool _isMatchmakingSearch = false;
        private List<LobbyInfo> _matchmakingLobbies = new List<LobbyInfo>();

        // Join code generation
        private const string CODE_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        private const int CODE_LENGTH = 6;

        public class LobbyInfo
        {
            public CSteamID LobbyID;
            public string JoinCode;
            public string HostName;
            public int PlayerCount;
            public int MaxPlayers;
            public string Version;
            public DateTime CreatedTime;
            /// <summary>Estimated ping to the host in ms via Steam relay coordinates; -1 = unknown.</summary>
            public int PingEstimateMs = -1;
            /// <summary>Host's self-reported Elo rating; -1 = unknown.</summary>
            public float HostElo = -1f;
            /// <summary>"public" (browser lobby) or "matchmaking" (queue slot).</summary>
            public string LobbyType = "";
            public bool IsMatchmaking => LobbyType == "matchmaking";
            public bool IsAvailable => PlayerCount < MaxPlayers;
        }

        // ─────────────────────────────────────────────────────
        // Unity lifecycle
        // ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            RegisterCallbacks();
            MelonLogger.Msg("[LobbyBrowser] Initialized");
        }

        private void Update()
        {
            // Timeout browse searches
            if (_isBrowsing && Time.time - _browseStartTime > SEARCH_TIMEOUT)
            {
                MelonLogger.Msg("[LobbyBrowser] Browse search timed out");
                _isBrowsing = false;
            }

            // Timeout join-by-code searches
            if (_isJoiningByCode && Time.time - _browseStartTime > SEARCH_TIMEOUT)
            {
                MelonLogger.Warning("[LobbyBrowser] Join-by-code search timed out — code not found on Steam");
                _isJoiningByCode = false;
                _pendingJoinCode = null;
            }

            // Timeout matchmaking searches
            if (_isMatchmakingSearch && Time.time - _browseStartTime > SEARCH_TIMEOUT)
            {
                MelonLogger.Warning("[LobbyBrowser] Matchmaking search timed out");
                _isMatchmakingSearch = false;
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DisposeCallbacks();
        }

        // ─────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────

        public string GenerateJoinCode()
        {
            var rng = new System.Random();
            return new string(Enumerable.Range(0, CODE_LENGTH)
                .Select(_ => CODE_CHARS[rng.Next(CODE_CHARS.Length)])
                .ToArray());
        }

        /// <summary>
        /// Browse all public lobbies for this game.
        /// Results appear in GetDiscoveredLobbies() after the callback fires.
        /// </summary>
        public void SearchLobbies()
        {
            if (_isBrowsing)
            {
                MelonLogger.Msg("[LobbyBrowser] Browse already in progress");
                return;
            }

            if (Time.time - _lastBrowseTime < BROWSE_COOLDOWN)
            {
                MelonLogger.Msg($"[LobbyBrowser] Browse cooldown ({BROWSE_COOLDOWN}s)");
                return;
            }

            _isBrowsing = true;
            _isJoiningByCode = false;   // cancel any pending code join
            _isMatchmakingSearch = false;
            _pendingJoinCode = null;
            _browseStartTime = Time.time;
            _lastBrowseTime = Time.time;
            _discoveredLobbies.Clear();

            MelonLogger.Msg("[LobbyBrowser] Searching for public + queue lobbies...");

            // Filter: our game, at least 1 free slot. Lobby type is filtered client-side
            // because we accept BOTH "public" and "matchmaking" (queue slots show in the
            // browser so browser players can fight queuers) and Steam filters can't OR.
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "game", "thrill-of-the-fight", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);

            // Without an explicit distance filter Steam defaults to "Default" (same region),
            // so lobbies hosted in other regions never show up. Worldwide is required for a
            // public browser to actually list everyone's lobbies.
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);

            SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
            _crLobbyMatchList.Set(call);
        }

        /// <summary>
        /// Join a lobby by its 6-character code.
        /// Performs a fresh Steam query filtered by join_code — works for both
        /// public AND private lobbies. Does NOT require SearchLobbies first.
        /// </summary>
        public void JoinByCode(string code)
        {
            code = code.ToUpper().Trim();

            if (string.IsNullOrEmpty(code) || code.Length != CODE_LENGTH)
            {
                MelonLogger.Error($"[LobbyBrowser] Invalid code '{code}' — must be {CODE_LENGTH} characters");
                return;
            }

            if (_isJoiningByCode)
            {
                MelonLogger.Warning("[LobbyBrowser] Already searching for a code, please wait");
                return;
            }

            if (_isBrowsing)
            {
                MelonLogger.Warning("[LobbyBrowser] Browse in progress — cancelling it to join by code");
                _isBrowsing = false;
            }

            MelonLogger.Msg($"[LobbyBrowser] Looking up code '{code}' on Steam...");

            _isJoiningByCode = true;
            _pendingJoinCode = code;
            _browseStartTime = Time.time;   // reuse for timeout tracking

            // Filter by the exact join code — Steam returns both public and private lobbies
            // when you filter by a specific metadata value, as long as the client knows the key.
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "game", "thrill-of-the-fight", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "join_code", code, ELobbyComparison.k_ELobbyComparisonEqual);

            // A code can belong to a friend in another region — search worldwide, not just
            // the local region, or private/cross-region codes silently return no results.
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);

            SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
            _crLobbyMatchList.Set(call);
        }

        /// <summary>
        /// Searches for open matchmaking-queue lobbies (lobby_type=matchmaking). Results
        /// land in GetMatchmakingLobbies(); poll IsSearching to know when done. Used by
        /// the Queue for Match loop — these lobbies never appear in the normal browser.
        /// </summary>
        public void SearchMatchmakingLobbies()
        {
            if (_isMatchmakingSearch) return;

            _isMatchmakingSearch = true;
            _isBrowsing = false;
            _isJoiningByCode = false;
            _pendingJoinCode = null;
            _browseStartTime = Time.time;
            _matchmakingLobbies.Clear();

            // No lobby_type filter: the queue accepts BOTH queue slots ("matchmaking")
            // and server-browser lobbies ("public") — filtered client-side.
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                "game", "thrill-of-the-fight", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(50);

            SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
            _crLobbyMatchList.Set(call);
        }

        public List<LobbyInfo> GetMatchmakingLobbies() => new List<LobbyInfo>(_matchmakingLobbies);

        /// <summary>Join a lobby directly by its Steam lobby ID.</summary>
        public void JoinLobby(CSteamID lobbyID)
        {
            MelonLogger.Msg($"[LobbyBrowser] Joining lobby {lobbyID.m_SteamID}...");
            NetworkManager.Instance.IsHost = false;
            SteamMatchmaking.JoinLobby(lobbyID);
        }

        public List<LobbyInfo> GetDiscoveredLobbies() => new List<LobbyInfo>(_discoveredLobbies);
        public LobbyInfo GetLobbyByCode(string code) =>
            _discoveredLobbies.FirstOrDefault(l => l.JoinCode == code.ToUpper());
        public bool IsSearching => _isBrowsing || _isJoiningByCode || _isMatchmakingSearch;

        // ─────────────────────────────────────────────────────
        // Steam callback
        // ─────────────────────────────────────────────────────

        private void OnLobbyMatchList(LobbyMatchList_t callback, bool ioFailure)
        {
            if (ioFailure)
            {
                MelonLogger.Error("[LobbyBrowser] ✗ Steam IO failure while requesting lobby list");
                _isBrowsing = false;
                _isJoiningByCode = false;
                _isMatchmakingSearch = false;
                _pendingJoinCode = null;
                return;
            }

            uint count = callback.m_nLobbiesMatching;

            // ── MATCHMAKING QUEUE path ───────────────────────
            if (_isMatchmakingSearch)
            {
                _isMatchmakingSearch = false;

                // The queue hosts-while-scanning, so our own lobby matches the filters.
                CSteamID own = NetworkManager.Instance != null
                    ? NetworkManager.Instance.CurrentLobbyID : default;

                for (int i = 0; i < count; i++)
                {
                    var id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id == own) continue;
                    string type = SteamMatchmaking.GetLobbyData(id, "lobby_type");
                    if (type != "matchmaking" && type != "public") continue;   // never pull in private lobbies
                    if (SteamMatchmaking.GetNumLobbyMembers(id) >= 2) continue;
                    _matchmakingLobbies.Add(ReadLobbyInfo(id));
                }
                MelonLogger.Msg($"[LobbyBrowser] Matchmaking search: {_matchmakingLobbies.Count} open queue lobby/lobbies");
                return;
            }

            // ── JOIN BY CODE path ────────────────────────────
            if (_isJoiningByCode)
            {
                _isJoiningByCode = false;
                string code = _pendingJoinCode;
                _pendingJoinCode = null;

                MelonLogger.Msg($"[LobbyBrowser] Code lookup returned {count} result(s) for '{code}'");

                if (count == 0)
                {
                    MelonLogger.Error($"[LobbyBrowser] ✗ No lobby found with code '{code}' — check the code and try again");
                    return;
                }

                // Take the first match (codes should be unique)
                CSteamID lobbyID = SteamMatchmaking.GetLobbyByIndex(0);
                string foundCode = SteamMatchmaking.GetLobbyData(lobbyID, "join_code");
                string hostName = SteamMatchmaking.GetLobbyData(lobbyID, "host_name");
                int playerCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID);

                if (playerCount >= 2)
                {
                    MelonLogger.Error($"[LobbyBrowser] ✗ Lobby '{code}' is full ({playerCount}/2)");
                    return;
                }

                MelonLogger.Msg($"[LobbyBrowser] ✓ Found lobby '{foundCode}' hosted by {hostName} ({playerCount}/2) — joining...");
                JoinLobby(lobbyID);
                return;
            }

            // ── BROWSE PUBLIC LOBBIES path ───────────────────
            _isBrowsing = false;
            MelonLogger.Msg($"[LobbyBrowser] Browse found {count} lobby/lobbies");

            CSteamID ownLobby = NetworkManager.Instance != null
                ? NetworkManager.Instance.CurrentLobbyID : default;

            for (int i = 0; i < count; i++)
            {
                var lobbyID = SteamMatchmaking.GetLobbyByIndex(i);
                if (lobbyID == ownLobby) continue;
                string lobbyType = SteamMatchmaking.GetLobbyData(lobbyID, "lobby_type");

                // Browser shows normal public lobbies AND open queue slots — never private.
                if (lobbyType != "public" && lobbyType != "matchmaking") continue;

                var info = ReadLobbyInfo(lobbyID);
                if (string.IsNullOrEmpty(info.JoinCode)) continue;

                _discoveredLobbies.Add(info);
                MelonLogger.Msg($"[LobbyBrowser]   └─ {info.JoinCode} by {info.HostName} ({info.PlayerCount}/2, ~{info.PingEstimateMs}ms)");
            }

            if (count == 0)
                MelonLogger.Msg("[LobbyBrowser] No public lobbies found");
        }

        /// <summary>Reads one lobby's metadata into a LobbyInfo, including the pre-connect
        /// ping estimate and the host's advertised Elo.</summary>
        private static LobbyInfo ReadLobbyInfo(CSteamID lobbyID)
        {
            string hostName = SteamMatchmaking.GetLobbyData(lobbyID, "host_name");
            string hostElo = SteamMatchmaking.GetLobbyData(lobbyID, "host_elo");
            float elo;

            return new LobbyInfo
            {
                LobbyID = lobbyID,
                JoinCode = SteamMatchmaking.GetLobbyData(lobbyID, "join_code"),
                HostName = string.IsNullOrEmpty(hostName) ? "Unknown" : hostName,
                PlayerCount = SteamMatchmaking.GetNumLobbyMembers(lobbyID),
                MaxPlayers = 2,
                Version = SteamMatchmaking.GetLobbyData(lobbyID, "version"),
                LobbyType = SteamMatchmaking.GetLobbyData(lobbyID, "lobby_type"),
                CreatedTime = DateTime.Now,
                PingEstimateMs = EstimatePingToLobby(lobbyID),
                HostElo = (!string.IsNullOrEmpty(hostElo) && float.TryParse(hostElo, out elo)) ? elo : -1f,
            };
        }

        /// <summary>
        /// Estimates ping to a lobby's host WITHOUT connecting: the host published its
        /// Steam relay coordinates ("ping_loc") in lobby data; Steam compares them with
        /// ours. Returns -1 when unknown (old mod version, or relay data not ready).
        /// </summary>
        private static int EstimatePingToLobby(CSteamID lobbyID)
        {
            try
            {
                string loc = SteamMatchmaking.GetLobbyData(lobbyID, "ping_loc");
                if (string.IsNullOrEmpty(loc)) return -1;

                SteamNetworkPingLocation_t remote;
                if (!SteamNetworkingUtils.ParsePingLocationString(loc, out remote)) return -1;

                int est = SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref remote);
                return est > 0 ? est : -1;
            }
            catch { return -1; }
        }

        // ─────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────

        private void RegisterCallbacks()
        {
            _crLobbyMatchList = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);
        }

        private void DisposeCallbacks()
        {
            _crLobbyMatchList?.Dispose();
        }
    }
}