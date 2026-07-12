using MelonLoader;
using Steamworks;
using System.Collections;
using System.Reflection;
using System;
using TotF;
using UnityEngine;
using Random = System.Random;

namespace ToFMultiplayer
{
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        // Steam callbacks
        private Callback<LobbyCreated_t> _cbLobbyCreated;
        private Callback<GameLobbyJoinRequested_t> _cbLobbyJoinRequested;
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private Callback<P2PSessionRequest_t> _cbP2PSessionRequest;
        private Callback<P2PSessionConnectFail_t> _cbP2PSessionFailed;

        // State
        public bool IsConnected { get; private set; }
        public bool BothPlayersReady { get; private set; }
        public CSteamID CurrentLobbyID { get; private set; }
        public CSteamID RemotePlayerID { get; private set; }
        public bool IsHost { get; set; }
        public bool IsHosting { get; private set; }
        public string CurrentJoinCode { get; private set; }
        public bool IsPublicLobby { get; private set; }
        public bool IsMatchmakingLobby { get; private set; }
        public bool SteamConnected { get; private set; }

        /// <summary>Opponent's self-reported Elo, read from lobby member data when we
        /// connect. Just DEFAULT_RATING if they're on a mod version without ratings.</summary>
        public float OpponentElo { get; private set; } = EloManager.DEFAULT_RATING;

        private static bool _steamApiInitialized = false;

        // Ready-up / break-skip state
        public bool RemotePlayerReadiedUp { get; private set; }
        public bool RemoteBreakSkipVoted { get; private set; }

        private GhostBoxer _ghostBoxer;
        private uint _sendSeq;
        private float _lastLobbyPollTime;
        private const float LobbyPollIntervalSeconds = 0.5f;
        private static readonly Random JoinCodeRandom = new Random();

        // Both TryFinalizeConnectionIfLobbyFull (poll/chat_entered) and OnP2PSessionRequest
        // (host fallback) can fire on the same frame and both want to call
        // OnBothPlayersReady — this flag stops it from happening twice.
        private bool _connectionFinalized = false;

        // This is an instance field on purpose, not static — we're hard-locked to 1v1
        // right now, 3+ players would need a real per-instance lock instead.
        public bool ApplyingRemoteDamage = false;

        // True while a remote KNOCKDOWN packet is being applied to the local player, so
        // the local OnKnockdown hook doesn't echo a KNOCKDOWN packet straight back.
        public bool ApplyingRemoteKnockdown = false;

        // Unity lifecycle

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            try
            {
                if (!_steamApiInitialized)
                {
                    if (!SteamAPI.Init())
                    {
                        MelonLogger.Error("[Network] ✗ SteamAPI.Init() failed");
                        SteamConnected = false;
                        return;
                    }
                    _steamApiInitialized = true;
                }

                CSteamID steamID = SteamUser.GetSteamID();
                if (steamID.m_SteamID == 0)
                {
                    MelonLogger.Error("[Network] ✗ Steam user not authenticated");
                    SteamConnected = false;
                    return;
                }

                SteamConnected = true;
                MelonLogger.Msg("[Network] ✓ Steam connected successfully");
                MelonLogger.Msg($"[Network] ✓ Player: {SteamFriends.GetPersonaName()} (ID: {steamID.m_SteamID})");
                RegisterCallbacks();

                // Warm up Steam Datagram Relay so GetLocalPingLocation becomes available —
                // needed to publish/estimate pre-connect ping (browser wifi bars).
                try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
                catch (Exception relayEx) { MelonLogger.Warning($"[Network] InitRelayNetworkAccess: {relayEx.Message}"); }

                MelonLogger.Msg("[Network] NetworkManager ready");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Network] ✗ Failed to initialize Steam: {ex.Message}");
                SteamConnected = false;
            }
        }

        private void Update()
        {
            try { SteamAPI.RunCallbacks(); } catch { }

            try
            {
                if (IsHosting && !IsConnected && CurrentLobbyID.m_SteamID != 0)
                {
                    float now = Time.realtimeSinceStartup;
                    if (now - _lastLobbyPollTime >= LobbyPollIntervalSeconds)
                    {
                        _lastLobbyPollTime = now;
                        TryFinalizeConnectionIfLobbyFull(CurrentLobbyID, source: "poll");
                    }
                }
            }
            catch { /* non-fatal */ }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Disconnect();
            DisposeCallbacks();
        }

        // Public API

        public void HostGame(bool isPublic = false, bool matchmaking = false)
        {
            if (IsHosting) { MelonLogger.Warning("[Network] Already hosting!"); return; }
            if (IsConnected) { MelonLogger.Warning("[Network] Already connected!"); return; }

            MelonLogger.Msg($"[Network] Creating {(matchmaking ? "MATCHMAKING" : isPublic ? "PUBLIC" : "PRIVATE")} lobby...");
            IsHost = true;
            IsHosting = true;
            IsPublicLobby = isPublic;
            IsMatchmakingLobby = matchmaking;

            try
            {
                // Has to be Invisible, not Private, for private lobbies. Steam just never
                // returns Private lobbies from RequestLobbyList, which meant join-by-code
                // (a filtered search under the hood) could never find them. Invisible still
                // stays off the public browser and friends list, but you can search for it
                // by exact code.
                ELobbyType type = (matchmaking || isPublic)
                    ? ELobbyType.k_ELobbyTypePublic
                    : ELobbyType.k_ELobbyTypeInvisible;
                SteamMatchmaking.CreateLobby(type, 2);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[Network] Failed to create lobby: {ex.Message}");
                IsHosting = false;
                IsHost = false;
                IsMatchmakingLobby = false;
            }
        }

        public void JoinByCode(string code)
        {
            if (LobbyBrowser.Instance != null) LobbyBrowser.Instance.JoinByCode(code);
            else MelonLogger.Error("[Network] LobbyBrowser not initialized");
        }

        public void SearchLobbies()
        {
            if (LobbyBrowser.Instance != null) LobbyBrowser.Instance.SearchLobbies();
            else MelonLogger.Error("[Network] LobbyBrowser not initialized");
        }

        public void SetGhostBoxer(GhostBoxer gb) => _ghostBoxer = gb;

        // ── Elo exchange + pre-connect ping location

        /// <summary>Writes our rating into our lobby MEMBER data (each member may only
        /// write their own), so the other player can read it for post-match Elo math.</summary>
        private void PublishMyElo()
        {
            try
            {
                if (CurrentLobbyID.m_SteamID == 0) return;
                SteamMatchmaking.SetLobbyMemberData(CurrentLobbyID, "elo", EloManager.Rating.ToString("F0"));
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] PublishMyElo: {e.Message}"); }
        }

        private void CaptureOpponentElo(CSteamID lobbyID, CSteamID remote)
        {
            OpponentElo = EloManager.DEFAULT_RATING;
            try
            {
                string s = SteamMatchmaking.GetLobbyMemberData(lobbyID, remote, "elo");
                float parsed;
                if (!string.IsNullOrEmpty(s) && float.TryParse(s, out parsed) && parsed > 0f)
                    OpponentElo = parsed;
                MelonLogger.Msg($"[Network] Opponent Elo: {OpponentElo:F0}");
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] CaptureOpponentElo: {e.Message}"); }
        }

        /// <summary>
        /// Publishes our Steam relay "ping location" into the lobby data once Steam has
        /// measured it (takes a moment after InitRelayNetworkAccess). Browsing clients
        /// parse it and call EstimatePingTimeFromLocalHost to show ping bars WITHOUT
        /// connecting to us.
        /// </summary>
        private IEnumerator PublishPingLocationWhenReady(CSteamID lobbyID)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (CurrentLobbyID != lobbyID) yield break;   // lobby changed/closed

                bool published = false;
                try
                {
                    SteamNetworkPingLocation_t loc;
                    float age = SteamNetworkingUtils.GetLocalPingLocation(out loc);
                    if (age >= 0f)
                    {
                        string encoded;
                        SteamNetworkingUtils.ConvertPingLocationToString(ref loc, out encoded, 512);
                        if (!string.IsNullOrEmpty(encoded))
                        {
                            SteamMatchmaking.SetLobbyData(lobbyID, "ping_loc", encoded);
                            MelonLogger.Msg("[Network] ✓ Published ping location for browser ping estimates");
                            published = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[Network] PublishPingLocation: {e.Message}");
                    yield break;
                }

                if (published) yield break;
                yield return new WaitForSeconds(1f);
            }
            MelonLogger.Warning("[Network] ⚠ Ping location never became available — browser will show unknown ping for our lobby");
        }

        // ── Diagnostics (read by the debug HUD)

        /// <summary>Round-trip time in milliseconds. -1 until the first PONG arrives.</summary>
        public float PingMs { get; private set; } = -1f;
        public int PacketsSentPerSec { get; private set; }
        public int PacketsRecvPerSec { get; private set; }

        private int _sentCounter, _recvCounter;
        private float _statWindowStart;
        private float _lastPingSentAt;
        private const float PING_INTERVAL = 2f;

        // Called every frame from ReceivePackets: rolls the per-second packet counters
        // and fires a latency probe every couple of seconds while connected.
        private void TickDiagnostics()
        {
            float now = Time.realtimeSinceStartup;

            if (now - _statWindowStart >= 1f)
            {
                PacketsSentPerSec = _sentCounter;
                PacketsRecvPerSec = _recvCounter;
                _sentCounter = 0;
                _recvCounter = 0;
                _statWindowStart = now;
            }

            if (IsConnected && now - _lastPingSentAt >= PING_INTERVAL)
            {
                _lastPingSentAt = now;
                SendPlayerState(PlayerStatePacket.CreatePing(now, _sendSeq++));
            }
        }

        // Send helpers

        public void SendPlayerState(PlayerStatePacket packet)
        {
            if (!IsConnected || RemotePlayerID.m_SteamID == 0) return;
            try
            {
                packet.sequenceNumber = _sendSeq++;
                byte[] data = packet.Serialize();
                EP2PSend sendType = (packet.packetType == PlayerStatePacket.PACKET_TYPE_POSITION_UPDATE ||
                                     packet.packetType == PlayerStatePacket.PACKET_TYPE_PING ||
                                     packet.packetType == PlayerStatePacket.PACKET_TYPE_PONG)
                    ? EP2PSend.k_EP2PSendUnreliable
                    : EP2PSend.k_EP2PSendReliable;
                SteamNetworking.SendP2PPacket(RemotePlayerID, data, (uint)data.Length, sendType);
                _sentCounter++;
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] SendPlayerState error: {e.Message}"); }
        }

        public void SendReadyUp()
        {
            SendReliable(PlayerStatePacket.CreateReadyUp(_sendSeq++));
            MelonLogger.Msg("[Network] ✓ Sent READY_UP");
        }

        public void SendBreakSkipVote()
        {
            SendReliable(PlayerStatePacket.CreateBreakSkipVote(_sendSeq++));
            MelonLogger.Msg("[Network] ✓ Sent BREAK_SKIP_VOTE");
        }

        public void SendRematchVote()
        {
            SendReliable(PlayerStatePacket.CreateRematchVote(_sendSeq++));
            MelonLogger.Msg("[Network] ✓ Sent REMATCH_VOTE");
        }

        /// <summary>Re-exchanges ratings before a rematch: both ratings changed when the
        /// previous bout was scored, so the stakes math needs fresh values.</summary>
        public void RefreshEloExchange()
        {
            PublishMyElo();
            if (CurrentLobbyID.m_SteamID != 0 && RemotePlayerID.m_SteamID != 0)
                CaptureOpponentElo(CurrentLobbyID, RemotePlayerID);
        }

        public void SendRoundStart(int roundNumber)
        {
            SendReliable(PlayerStatePacket.CreateRoundStart(roundNumber, _sendSeq++));
            MelonLogger.Msg($"[Network] ✓ Sent ROUND_START (round {roundNumber})");
        }

        public void SendRoundEnd(int roundNumber)
        {
            SendReliable(PlayerStatePacket.CreateRoundEnd(roundNumber, _sendSeq++));
            MelonLogger.Msg($"[Network] ✓ Sent ROUND_END (round {roundNumber})");
        }

        public void SendBreakStart(float breakTime)
        {
            SendReliable(PlayerStatePacket.CreateBreakStart(breakTime, _sendSeq++));
            MelonLogger.Msg($"[Network] ✓ Sent BREAK_START (breakTime {breakTime:F0}s)");
        }

        public void SendRetire()
        {
            if (!IsConnected || RemotePlayerID.m_SteamID == 0) return;
            var packet = PlayerStatePacket.CreateRetireNotice(MultiplayerPlugin.Instance?.GetNextPacketSeq() ?? 0);
            byte[] data = packet.Serialize();
            SteamNetworking.SendP2PPacket(RemotePlayerID, data, (uint)data.Length,
                                          EP2PSend.k_EP2PSendReliable);
            MelonLogger.Msg("[Network] ✓ Sent RETIRE notice to remote");
        }

        public void SendBoutEnd(int winner, int winCondition, int wentToRound,
                                int redScored, int blueScored, int drawScored,
                                int celebrateIndex)
        {
            SendReliable(PlayerStatePacket.CreateBoutEnd(
                winner, winCondition, wentToRound,
                redScored, blueScored, drawScored,
                celebrateIndex, _sendSeq++));
            MelonLogger.Msg($"[Network] ✓ Sent BOUT_END — winner={winner} cond={winCondition} round={wentToRound}");
        }

        public void SendStartMatch()
        {
            SendReliable(PlayerStatePacket.CreateStartMatch(_sendSeq++));
            MelonLogger.Msg("[Network] ✓ Sent START_MATCH");
        }

        public void SendGetUp()
        {
            SendReliable(PlayerStatePacket.CreateGetUp(_sendSeq++));
            MelonLogger.Msg("[Network] ✓ Sent GET_UP");
        }

        public void SendCornerAssignment(BoutController.Corner corner)
        {
            SendReliable(PlayerStatePacket.CreateCornerAssignment((int)corner, _sendSeq++));
            MelonLogger.Msg($"[Network] ✓ Sent CORNER_ASSIGN ({corner})");
        }

        private void SendReliable(PlayerStatePacket packet)
        {
            if (!IsConnected || RemotePlayerID.m_SteamID == 0) return;
            try
            {
                byte[] data = packet.Serialize();
                SteamNetworking.SendP2PPacket(RemotePlayerID, data, (uint)data.Length, EP2PSend.k_EP2PSendReliable);
                _sentCounter++;
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] SendReliable error: {e.Message}"); }
        }

        // Receive

        public void ReceivePackets()
        {
            TickDiagnostics();
            if (!IsConnected) return;
            try
            {
                uint msgSize;
                while (SteamNetworking.IsP2PPacketAvailable(out msgSize))
                {
                    if (msgSize == 0 || msgSize > 4096)
                    {
                        byte[] junk = new byte[Math.Min(msgSize, 4096u)];
                        CSteamID dummy; uint dummyRead;
                        SteamNetworking.ReadP2PPacket(junk, (uint)junk.Length, out dummyRead, out dummy);
                        MelonLogger.Warning($"[Network] Skipped packet with bad size: {msgSize}");
                        continue;
                    }
                    byte[] data = new byte[msgSize];
                    CSteamID sender;
                    uint bytesRead;
                    if (SteamNetworking.ReadP2PPacket(data, msgSize, out bytesRead, out sender))
                        OnReceivedPacket(data);
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] ReceivePackets error: {e.Message}"); }
        }

        // State resets

        public void ResetReadyState()
        {
            RemotePlayerReadiedUp = false;
            MelonLogger.Msg("[Network] Ready state reset");
        }

        public void ResetBreakSkipState()
        {
            RemoteBreakSkipVoted = false;
        }

        // Disconnect

        public void Disconnect()
        {
            MelonLogger.Msg("[Network] Disconnecting...");
            if (CurrentLobbyID.m_SteamID != 0)
            {
                SteamMatchmaking.LeaveLobby(CurrentLobbyID);
                CurrentLobbyID = default;
            }
            if (RemotePlayerID.m_SteamID != 0)
                SteamNetworking.CloseP2PSessionWithUser(RemotePlayerID);

            IsConnected = false;
            BothPlayersReady = false;
            IsHosting = false;
            IsPublicLobby = false;
            IsMatchmakingLobby = false;
            OpponentElo = EloManager.DEFAULT_RATING;
            RemotePlayerID = default;
            CurrentJoinCode = null;
            RemotePlayerReadiedUp = false;
            RemoteBreakSkipVoted = false;
            _ghostBoxer = null;
            _connectionFinalized = false; // reset so reconnect works
            PingMs = -1f;
        }

        public void EndLobby()
        {
            if (!IsHosting) { MelonLogger.Warning("[Network] Not hosting!"); return; }
            MelonLogger.Msg("[Network] Ending lobby...");
            try
            {
                if (IsConnected && RemotePlayerID.m_SteamID != 0)
                {
                    SendReliable(PlayerStatePacket.CreateDisconnectNotice(_sendSeq++));
                    MelonLogger.Msg("[Network] Sent disconnect notice to remote");
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] EndLobby notify error: {e.Message}"); }
            Disconnect();
        }

        // Steam callback handlers

        private void OnLobbyCreated(LobbyCreated_t cb)
        {
            if (cb.m_eResult != EResult.k_EResultOK)
            {
                MelonLogger.Error("[Network] Lobby creation failed");
                IsHosting = false;
                return;
            }

            CurrentLobbyID = new CSteamID(cb.m_ulSteamIDLobby);
            CurrentJoinCode = GenerateJoinCode();

            string lobbyTypeTag = IsMatchmakingLobby ? "matchmaking" : (IsPublicLobby ? "public" : "private");
            SteamMatchmaking.SetLobbyData(CurrentLobbyID, "join_code", CurrentJoinCode);
            SteamMatchmaking.SetLobbyData(CurrentLobbyID, "game", "thrill-of-the-fight");
            SteamMatchmaking.SetLobbyData(CurrentLobbyID, "version", "1.0.0");
            SteamMatchmaking.SetLobbyData(CurrentLobbyID, "host_name", SteamFriends.GetPersonaName());
            SteamMatchmaking.SetLobbyData(CurrentLobbyID, "lobby_type", lobbyTypeTag);
            SteamMatchmaking.SetLobbyData(CurrentLobbyID, "host_elo", EloManager.Rating.ToString("F0"));
            PublishMyElo();

            // Publish our Steam Datagram Relay network coordinates so browsing players can
            // ESTIMATE their ping to us before ever connecting (drawn as the wifi bars).
            StartCoroutine(PublishPingLocationWhenReady(CurrentLobbyID));

            MelonLogger.Msg($"[Network] ✓ Lobby created with join code: {CurrentJoinCode}");
            MelonLogger.Msg($"[Network] ✓ Lobby ID: {CurrentLobbyID.m_SteamID}");
            MelonLogger.Msg($"[Network] ✓ Lobby Type: {lobbyTypeTag.ToUpper()}");

            MultiplayerPlugin.OnHostLobbyCreated();
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t cb)
        {
            MelonLogger.Msg("[Network] Joining lobby from Steam invite...");
            CurrentLobbyID = cb.m_steamIDLobby;
            IsHost = false;
            SteamMatchmaking.JoinLobby(cb.m_steamIDLobby);
        }

        private void OnLobbyEnter(LobbyEnter_t cb)
        {
            var lobbyID = new CSteamID(cb.m_ulSteamIDLobby);
            int count = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
            MelonLogger.Msg($"[Network] Lobby entered — {count}/2 players");

            CurrentLobbyID = lobbyID;
            PublishMyElo();   // so the host can read our rating for the post-match Elo math
            TryFinalizeConnectionIfLobbyFull(lobbyID, source: "lobby_enter");
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t cb)
        {
            uint state = cb.m_rgfChatMemberStateChange;

            if ((state & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                var lobbyID = new CSteamID(cb.m_ulSteamIDLobby);
                TryFinalizeConnectionIfLobbyFull(lobbyID, source: "chat_entered");
            }

            if ((state & (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0 ||
                (state & (uint)EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0)
            {
                if (IsConnected)
                {
                    MelonLogger.Msg("[Network] Remote player left lobby");
                    MultiplayerPlugin.OnOpponentDisconnected();
                    Disconnect();
                }
            }
        }

        private void TryFinalizeConnectionIfLobbyFull(CSteamID lobbyID, string source)
        {
            // Atomic guard — set immediately so concurrent Steam callbacks (chat_entered,
            // lobby_enter, poll) can't both reach OnBothPlayersReady.
            if (_connectionFinalized) return;
            if (lobbyID.m_SteamID == 0) return;

            try
            {
                int count = SteamMatchmaking.GetNumLobbyMembers(lobbyID);
                if (count != 2) return;

                CSteamID localID = SteamUser.GetSteamID();
                CSteamID remote = default;
                for (int i = 0; i < count; i++)
                {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex(lobbyID, i);
                    if (member != localID) { remote = member; break; }
                }

                if (remote.m_SteamID == 0)
                {
                    MelonLogger.Error($"[Network] ✗ ({source}) Could not find remote player in lobby member list");
                    return;
                }

                // Set flag before any side-effecting calls so re-entrant callbacks
                // can't sneak through.
                _connectionFinalized = true;

                CurrentLobbyID = lobbyID;
                RemotePlayerID = remote;
                IsConnected = true;
                BothPlayersReady = true;

                if (!IsHost)
                {
                    try
                    {
                        var localUserId = SteamUser.GetSteamID();
                        IsHost = SteamMatchmaking.GetLobbyOwner(lobbyID) == localUserId;
                    }
                    catch { }
                }

                CaptureOpponentElo(lobbyID, remote);

                MelonLogger.Msg($"[Network] ({source}) Remote player: {SteamFriends.GetFriendPersonaName(RemotePlayerID)} ({RemotePlayerID.m_SteamID})");
                MelonLogger.Msg($"[Network] ({source}) Both players connected! Loading Auditorium...");
                MultiplayerPlugin.OnBothPlayersReady();
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Network] ({source}) TryFinalizeConnectionIfLobbyFull error: {e.Message}");
            }
        }

        private static string GenerateJoinCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            char[] buf = new char[6];
            lock (JoinCodeRandom)
            {
                for (int i = 0; i < buf.Length; i++)
                    buf[i] = chars[JoinCodeRandom.Next(chars.Length)];
            }
            return new string(buf);
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t cb)
        {
            SteamNetworking.AcceptP2PSessionWithUser(cb.m_steamIDRemote);
            MelonLogger.Msg("[Network] Accepted P2P session request");

            // Host-side fallback: some Steam configs never fire lobby enter/chat callbacks.
            // Guarded by _connectionFinalized so this can't double-fire if
            // TryFinalizeConnectionIfLobbyFull already ran on the same frame.
            if (IsHosting && !IsConnected && !_connectionFinalized)
            {
                _connectionFinalized = true;
                RemotePlayerID = cb.m_steamIDRemote;
                IsConnected = true;
                BothPlayersReady = true;
                MelonLogger.Msg($"[Network] (p2p_request) Remote player: {SteamFriends.GetFriendPersonaName(RemotePlayerID)} ({RemotePlayerID.m_SteamID})");
                MelonLogger.Msg("[Network] (p2p_request) Both players connected! Loading Auditorium...");
                MultiplayerPlugin.OnBothPlayersReady();
            }
        }

        // A P2P session error doesn't always mean the connection is dead — could just be
        // a route change or a wifi hiccup, and Steam re-handshakes on its own as long as
        // we keep sending. So we give it a grace period before giving up on the opponent.
        // A deliberate leave still ends things instantly through the lobby-member-left
        // callback, this is only for the "did the connection actually die" case.
        private const float DISCONNECT_GRACE_SECONDS = 20f;
        private float _lastPacketReceivedTime;
        private bool _graceRunning;

        private void OnP2PSessionFailed(P2PSessionConnectFail_t cb)
        {
            MelonLogger.Warning($"[Network] P2P session failed: error={cb.m_eP2PSessionError}");
            if (IsConnected && !_graceRunning)
                StartCoroutine(DisconnectGrace());
        }

        private System.Collections.IEnumerator DisconnectGrace()
        {
            _graceRunning = true;
            float failedAt = Time.realtimeSinceStartup;
            MelonLogger.Msg($"[Network] Connection hiccup — waiting up to {DISCONNECT_GRACE_SECONDS:F0}s for it to recover...");

            while (Time.realtimeSinceStartup - failedAt < DISCONNECT_GRACE_SECONDS)
            {
                yield return new WaitForSeconds(0.5f);
                if (!IsConnected) { _graceRunning = false; yield break; }   // torn down elsewhere
                if (_lastPacketReceivedTime > failedAt)
                {
                    MelonLogger.Msg("[Network] ✓ Connection recovered — match continues");
                    _graceRunning = false;
                    yield break;
                }
            }

            _graceRunning = false;
            MelonLogger.Warning("[Network] ✗ Connection did not recover — opponent disconnected");
            if (IsConnected)
            {
                MultiplayerPlugin.OnOpponentDisconnected();
                Disconnect();
            }
        }

        // Packet routing

        private void OnReceivedPacket(byte[] data)
        {
            try
            {
                _recvCounter++;
                _lastPacketReceivedTime = Time.realtimeSinceStartup;
                var packet = PlayerStatePacket.Deserialize(data);
                switch (packet.packetType)
                {
                    case PlayerStatePacket.PACKET_TYPE_PING:
                        // Echo the timestamp straight back; the original sender computes RTT.
                        SendPlayerState(PlayerStatePacket.CreatePong(packet.roundData, _sendSeq++));
                        break;

                    case PlayerStatePacket.PACKET_TYPE_PONG:
                        PingMs = Mathf.Max(0f, (Time.realtimeSinceStartup - packet.roundData) * 1000f);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_POSITION_UPDATE:
                        _ghostBoxer?.UpdateFromNetworkPacket(packet);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_DAMAGE_EVENT:
                        MelonLogger.Msg($"[Network] Damage recv: trauma={packet.traumaDamage:F1}");
                        ApplyDamageToLocalPlayer(packet);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_KNOCKDOWN:
                        MelonLogger.Msg("[Network] Knockdown recv");
                        TriggerLocalKnockdown(packet);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_READY_UP:
                        MelonLogger.Msg("[Network] ✓ Remote READY_UP received");
                        RemotePlayerReadiedUp = true;
                        MultiplayerPlugin.OnRemotePlayerReadiedUp();
                        break;

                    case PlayerStatePacket.PACKET_TYPE_ROUND_START:
                        MelonLogger.Msg($"[Network] ✓ Remote ROUND_START received (round {(int)packet.roundData})");
                        MultiplayerPlugin.OnRemoteRoundStart((int)packet.roundData);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_ROUND_END:
                        MelonLogger.Msg($"[Network] ✓ Remote ROUND_END received (round {(int)packet.roundData})");
                        MultiplayerPlugin.OnRemoteRoundEnd((int)packet.roundData);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_BREAK_START:
                        MelonLogger.Msg($"[Network] ✓ Remote BREAK_START received (breakTime={packet.roundData:F0}s)");
                        MultiplayerPlugin.OnRemoteBreakStart(packet.roundData);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_BREAK_SKIP_VOTE:
                        MelonLogger.Msg("[Network] ✓ Remote BREAK_SKIP_VOTE received");
                        RemoteBreakSkipVoted = true;
                        MultiplayerPlugin.OnRemoteBreakSkipVote();
                        break;

                    case PlayerStatePacket.PACKET_TYPE_DISCONNECT:
                        MelonLogger.Msg("[Network] ✓ Remote sent DISCONNECT notice");
                        MultiplayerPlugin.OnOpponentDisconnected();
                        Disconnect();
                        break;

                    case PlayerStatePacket.PACKET_TYPE_BOUT_END:
                        MelonLogger.Msg($"[Network] ✓ Remote BOUT_END received — winner={(int)packet.traumaDamage} cond={(int)packet.painDamage}");
                        MultiplayerPlugin.OnRemoteBoutEnd(packet);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_RETIRE:
                        MelonLogger.Msg("[Network] Remote player held quit trigger — retiring locally");
                        MultiplayerPlugin.OnRemoteRetire();
                        break;

                    case PlayerStatePacket.PACKET_TYPE_CORNER_ASSIGN:
                        MelonLogger.Msg($"[Network] ✓ Remote CORNER_ASSIGN received ({(BoutController.Corner)packet.cornerAssignment})");
                        MultiplayerPlugin.OnRemoteCornerAssigned((BoutController.Corner)packet.cornerAssignment);
                        break;

                    case PlayerStatePacket.PACKET_TYPE_START_MATCH:
                        MelonLogger.Msg("[Network] ✓ Remote START_MATCH received");
                        MultiplayerPlugin.OnStartMatchReceived();
                        break;

                    case PlayerStatePacket.PACKET_TYPE_GET_UP:
                        MelonLogger.Msg("[Network] ✓ Remote GET_UP received");
                        _ghostBoxer?.OnRemoteGetUp();
                        break;

                    case PlayerStatePacket.PACKET_TYPE_REMATCH_VOTE:
                        MelonLogger.Msg("[Network] ✓ Remote REMATCH_VOTE received");
                        MultiplayerPlugin.OnRemoteRematchVote();
                        break;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Network] Packet parse error: {e.Message}");
            }
        }

        // Damage-packet rate limiting (sliding one-second window)
        private float _damageWindowStart;
        private int _damageEventsInWindow;

        private static readonly FieldInfo LocalTraumaField = typeof(BoxerController).GetField("traumaDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo LocalPainField = typeof(BoxerController).GetField("painDamage",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly MethodInfo KnockdownMethod = typeof(BoxerController).GetMethod("Knockdown",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>
        /// The remote player landed a punch on their ghost (which represents US). Apply the
        /// same condition deltas to our player, record it for local round scoring, and give
        /// the player haptic/visual feedback for meaningful hits.
        /// </summary>
        private void ApplyDamageToLocalPlayer(PlayerStatePacket packet)
        {
            try
            {
                if (BoutController.instance == null) return;
                var localBoxer = BoutController.allBoxers[0];
                if (localBoxer == null) return;

                // Clamp everything to what one real punch can actually do, so a buggy
                // or tampered client can't just send huge numbers. Force caps at 3600,
                // hit zones multiply by at most 1.248, and trauma amplification can add
                // maybe 20% late in a bout — so a single hit tops out around 5400 damage.
                // Dizzy gain (damage minus 3200) tops out around 2200.
                float deltaTrauma = Mathf.Clamp(packet.traumaDamage, 0f, 6000f);
                float deltaPain = Mathf.Clamp(packet.painDamage, 0f, 6000f);
                float deltaDizzy = Mathf.Clamp(packet.dizzyLevel, 0f, 2500f);
                float rawDamage = Mathf.Clamp(packet.roundData, 0f, 6000f);
                float painThreshold = Mathf.Clamp(packet.headPos.x > 0f ? packet.headPos.x : 2700f, 1500f, 5000f);

                if (packet.traumaDamage > 6000f || packet.painDamage > 6000f || packet.roundData > 6000f)
                    MelonLogger.Warning($"[Network] ⚠ Damage packet exceeded physical limits (raw={packet.roundData:F0}, trauma={packet.traumaDamage:F0}) — clamped");

                // And a rate limit — two fists just can't land more than ~10 hits a second.
                float nowT = Time.realtimeSinceStartup;
                if (nowT - _damageWindowStart > 1f) { _damageWindowStart = nowT; _damageEventsInWindow = 0; }
                if (++_damageEventsInWindow > 10)
                {
                    MelonLogger.Warning("[Network] ⚠ Damage packets over rate limit — dropped");
                    return;
                }

                if (LocalTraumaField == null || LocalPainField == null)
                {
                    MelonLogger.Error("[Network] ✗ trauma/pain fields not found on BoxerController — damage sync broken. Game may have updated.");
                    return;
                }

                ApplyingRemoteDamage = true;
                try
                {
                    LocalTraumaField.SetValue(localBoxer, (float)LocalTraumaField.GetValue(localBoxer) + deltaTrauma);
                    LocalPainField.SetValue(localBoxer, (float)LocalPainField.GetValue(localBoxer) + deltaPain);
                    localBoxer.dizzyLevel += deltaDizzy; // public property — no reflection needed

                    // Round scoring: in the local frame the local player is always Red.
                    BoutController.AddDamageToRed(rawDamage, painThreshold);

                    // Haptics + camera flash + hit sound for hits that actually hurt.
                    if (rawDamage > painThreshold)
                        localBoxer.DirectDamageExternalResults(rawDamage, false, Hurtbox.HurtboxType.Head, false);
                }
                finally
                {
                    ApplyingRemoteDamage = false;
                }
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] ApplyDamageToLocalPlayer error: {e.Message}"); }
        }

        /// <summary>
        /// The remote machine detected a knockdown. The corner is in the SENDER's frame:
        /// Blue = their ghost = OUR player. Force our player down; the game's own
        /// OnKnockdown wiring then counts it (RedKnockedDown) and runs the referee.
        /// </summary>
        private void TriggerLocalKnockdown(PlayerStatePacket packet)
        {
            try
            {
                if (packet.isKnockedOut == 0) return;
                if (BoutController.instance == null) return;

                var senderFrameCorner = (BoutController.Corner)Mathf.RoundToInt(packet.roundData);
                if (senderFrameCorner != BoutController.Corner.Blue)
                {
                    // Their own player going down is something we already detected on our
                    // ghost — never apply it twice.
                    MelonLogger.Msg("[Network] Ignoring KNOCKDOWN for sender's own corner (already handled locally)");
                    return;
                }

                var localBoxer = BoutController.allBoxers[0];
                if (localBoxer == null || localBoxer.isDown) return;

                float floorTime = packet.headPos.x;
                localBoxer.knockdownTimer = floorTime > 0.5f ? Mathf.Clamp(floorTime, 2f, 20f) : 5f;

                if (KnockdownMethod == null)
                {
                    MelonLogger.Error("[Network] ✗ Knockdown method not found on BoxerController — falling back to count-only");
                    BoutController.BoxerKnockedDown(BoutController.Corner.Red);
                    return;
                }

                ApplyingRemoteKnockdown = true;
                try
                {
                    // PlayerController.Knockdown: isDown = true, camera fade, hit-the-mat,
                    // and fires OnKnockdown -> BoutController.RedKnockedDown (referee count).
                    KnockdownMethod.Invoke(localBoxer, new object[] { null, null });
                }
                finally
                {
                    ApplyingRemoteKnockdown = false;
                }

                MelonLogger.Msg($"[Network] ✓ Local player knocked down by remote hit (floor time {localBoxer.knockdownTimer:F1}s)");
            }
            catch (Exception e) { MelonLogger.Warning($"[Network] TriggerLocalKnockdown error: {e.Message}"); }
        }

        // Callback registration

        private void RegisterCallbacks()
        {
            _cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _cbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _cbP2PSessionRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            _cbP2PSessionFailed = Callback<P2PSessionConnectFail_t>.Create(OnP2PSessionFailed);
        }

        private void DisposeCallbacks()
        {
            _cbLobbyCreated?.Dispose();
            _cbLobbyJoinRequested?.Dispose();
            _cbLobbyEnter?.Dispose();
            _cbLobbyChatUpdate?.Dispose();
            _cbP2PSessionRequest?.Dispose();
            _cbP2PSessionFailed?.Dispose();
        }
    }
}