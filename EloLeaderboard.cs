using System;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;

namespace ToFMultiplayer
{
    /// <summary>
    /// Worldwide Elo leaderboard backed by Steam Leaderboards — no server needed.
    /// FindOrCreateLeaderboard creates a mod-specific board under the game's AppID
    /// on first use; every player's rating is uploaded after each match (and once at
    /// startup so the board fills up even before people finish matches).
    ///
    /// If Steam refuses (some titles disable client-created leaderboards), Unavailable
    /// goes true and the UI shows a graceful message instead.
    /// </summary>
    public static class EloLeaderboard
    {
        public class Entry
        {
            public int Rank;
            public CSteamID User;
            public int Rating;
        }

        private const string LEADERBOARD_NAME = "totf_multiplayer_elo";

        private static SteamLeaderboard_t _board;
        private static bool _boardReady;
        private static bool _finding;

        // Queued work for when the board handle arrives.
        private static float _pendingUpload = -1f;
        private static bool _refreshQueued;

        private static CallResult<LeaderboardFindResult_t> _crFind;
        private static CallResult<LeaderboardScoreUploaded_t> _crUpload;
        private static CallResult<LeaderboardScoresDownloaded_t> _crTop;
        private static CallResult<LeaderboardScoresDownloaded_t> _crSelf;

        /// <summary>Top global entries, best first. Valid once Refreshing is false.</summary>
        public static List<Entry> Top { get; } = new List<Entry>();
        /// <summary>Our own entry (global rank + score), null if we're not on the board yet.</summary>
        public static Entry Self { get; private set; }
        public static bool Refreshing { get; private set; }
        public static bool Unavailable { get; private set; }

        // ─────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────

        /// <summary>Publishes a rating to the global board (force-update: Elo can go down).</summary>
        public static void UploadRating(float rating)
        {
            _pendingUpload = rating;
            if (_boardReady) UploadPending();
            else EnsureBoard();
        }

        /// <summary>Re-downloads the global top + our own rank. Poll Refreshing to know when done.</summary>
        public static void Refresh()
        {
            if (Unavailable || Refreshing) return;
            Refreshing = true;
            if (_boardReady) StartDownloads();
            else { _refreshQueued = true; EnsureBoard(); }
        }

        // ─────────────────────────────────────────────────────
        // Board bootstrap
        // ─────────────────────────────────────────────────────

        private static void EnsureBoard()
        {
            if (_boardReady || _finding || Unavailable) return;

            try
            {
                if (_crFind == null)
                {
                    _crFind = CallResult<LeaderboardFindResult_t>.Create(OnBoardFound);
                    _crUpload = CallResult<LeaderboardScoreUploaded_t>.Create(OnScoreUploaded);
                    _crTop = CallResult<LeaderboardScoresDownloaded_t>.Create(OnTopDownloaded);
                    _crSelf = CallResult<LeaderboardScoresDownloaded_t>.Create(OnSelfDownloaded);
                }

                _finding = true;
                SteamAPICall_t call = SteamUserStats.FindOrCreateLeaderboard(
                    LEADERBOARD_NAME,
                    ELeaderboardSortMethod.k_ELeaderboardSortMethodDescending,
                    ELeaderboardDisplayType.k_ELeaderboardDisplayTypeNumeric);
                _crFind.Set(call);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Leaderboard] FindOrCreateLeaderboard failed: {e.Message}");
                _finding = false;
                Unavailable = true;
                Refreshing = false;
            }
        }

        private static void OnBoardFound(LeaderboardFindResult_t result, bool ioFailure)
        {
            _finding = false;

            if (ioFailure || result.m_bLeaderboardFound == 0)
            {
                MelonLogger.Warning("[Leaderboard] ✗ Steam did not return a leaderboard — global ranking unavailable");
                Unavailable = true;
                Refreshing = false;
                return;
            }

            _board = result.m_hSteamLeaderboard;
            _boardReady = true;
            MelonLogger.Msg("[Leaderboard] ✓ Global Elo leaderboard ready");

            UploadPending();
            if (_refreshQueued) { _refreshQueued = false; StartDownloads(); }
        }

        // ─────────────────────────────────────────────────────
        // Upload
        // ─────────────────────────────────────────────────────

        private static void UploadPending()
        {
            if (_pendingUpload < 0f) return;
            int score = (int)Math.Round(_pendingUpload);
            _pendingUpload = -1f;

            try
            {
                SteamAPICall_t call = SteamUserStats.UploadLeaderboardScore(
                    _board,
                    ELeaderboardUploadScoreMethod.k_ELeaderboardUploadScoreMethodForceUpdate,
                    score, null, 0);
                _crUpload.Set(call);
            }
            catch (Exception e) { MelonLogger.Warning($"[Leaderboard] Upload failed: {e.Message}"); }
        }

        private static void OnScoreUploaded(LeaderboardScoreUploaded_t result, bool ioFailure)
        {
            if (ioFailure || result.m_bSuccess == 0)
                MelonLogger.Warning("[Leaderboard] ✗ Rating upload rejected");
            else
                MelonLogger.Msg($"[Leaderboard] ✓ Rating {result.m_nScore} published (global rank #{result.m_nGlobalRankNew})");
        }

        // ─────────────────────────────────────────────────────
        // Download
        // ─────────────────────────────────────────────────────

        private static void StartDownloads()
        {
            try
            {
                // Global top 10.
                SteamAPICall_t topCall = SteamUserStats.DownloadLeaderboardEntries(
                    _board, ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobal, 1, 10);
                _crTop.Set(topCall);

                // Our own rank (may be far below the top 10).
                SteamAPICall_t selfCall = SteamUserStats.DownloadLeaderboardEntries(
                    _board, ELeaderboardDataRequest.k_ELeaderboardDataRequestGlobalAroundUser, 0, 0);
                _crSelf.Set(selfCall);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Leaderboard] Download failed: {e.Message}");
                Refreshing = false;
            }
        }

        private static void OnTopDownloaded(LeaderboardScoresDownloaded_t result, bool ioFailure)
        {
            Top.Clear();
            if (!ioFailure)
            {
                for (int i = 0; i < result.m_cEntryCount; i++)
                {
                    LeaderboardEntry_t e;
                    if (!SteamUserStats.GetDownloadedLeaderboardEntry(
                            result.m_hSteamLeaderboardEntries, i, out e, null, 0)) continue;
                    Top.Add(new Entry { Rank = e.m_nGlobalRank, User = e.m_steamIDUser, Rating = e.m_nScore });

                    // Warm Steam's persona cache so names resolve for strangers.
                    try { SteamFriends.RequestUserInformation(e.m_steamIDUser, true); } catch { }
                }
            }
            Refreshing = false;   // top list is what the UI waits for
        }

        private static void OnSelfDownloaded(LeaderboardScoresDownloaded_t result, bool ioFailure)
        {
            Self = null;
            if (ioFailure) return;

            CSteamID me = SteamUser.GetSteamID();
            for (int i = 0; i < result.m_cEntryCount; i++)
            {
                LeaderboardEntry_t e;
                if (!SteamUserStats.GetDownloadedLeaderboardEntry(
                        result.m_hSteamLeaderboardEntries, i, out e, null, 0)) continue;
                if (e.m_steamIDUser == me)
                {
                    Self = new Entry { Rank = e.m_nGlobalRank, User = e.m_steamIDUser, Rating = e.m_nScore };
                    break;
                }
            }
        }
    }
}
