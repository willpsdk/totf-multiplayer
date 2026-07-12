using System;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;

namespace ToFMultiplayer
{

    public static class EloManager
    {
        public const float DEFAULT_RATING = 1000f;
        public const float K_FACTOR = 32f;
        private const string INTEGRITY_SALT = "totf-mp-2026-x91";

        private static MelonPreferences_Category _cat;
        private static MelonPreferences_Entry<float> _rating;
        private static MelonPreferences_Entry<int> _wins, _losses, _draws;
        private static MelonPreferences_Entry<string> _sig;

        // Per-match state: set at fight start, consumed exactly once at fight end so
        // duplicate end signals (BOUT_END + retire, echoes) can't double-apply.
        private static bool _inMatch;
        private static float _opponentRating = DEFAULT_RATING;

        public static float Rating { get { Init(); return _rating.Value; } }
        public static int Wins { get { Init(); return _wins.Value; } }
        public static int Losses { get { Init(); return _losses.Value; } }
        public static int Draws { get { Init(); return _draws.Value; } }

        private static void Init()
        {
            if (_cat != null) return;
            _cat = MelonPreferences.CreateCategory("ToFMultiplayer");
            _rating = _cat.CreateEntry("elo", DEFAULT_RATING);
            _wins = _cat.CreateEntry("wins", 0);
            _losses = _cat.CreateEntry("losses", 0);
            _draws = _cat.CreateEntry("draws", 0);
            _sig = _cat.CreateEntry("sig", "");

            // Integrity check: a hand-edited config won't carry a matching signature.
            bool untouchedDefaults = _rating.Value == DEFAULT_RATING &&
                                     _wins.Value == 0 && _losses.Value == 0 && _draws.Value == 0;
            if (!untouchedDefaults && _sig.Value != ComputeSignature())
            {
                MelonLogger.Warning("[Elo] ⚠ Rating file failed the integrity check — resetting to 1000");
                _rating.Value = DEFAULT_RATING;
                _wins.Value = 0;
                _losses.Value = 0;
                _draws.Value = 0;
            }
            SaveSigned();
        }

        private static string ComputeSignature()
        {
            string payload = $"{_rating.Value:F2}|{_wins.Value}|{_losses.Value}|{_draws.Value}|{INTEGRITY_SALT}";
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void SaveSigned()
        {
            _sig.Value = ComputeSignature();
            try { MelonPreferences.Save(); } catch { }
        }

        /// <summary>What this match is worth BEFORE it's played: rating change on a win
        /// (positive) and on a loss (negative), given the opponent's rating.</summary>
        public static void PreviewDeltas(float opponentRating, out float winDelta, out float lossDelta)
        {
            Init();
            if (opponentRating <= 0f) opponentRating = DEFAULT_RATING;
            float expected = 1f / (1f + (float)Math.Pow(10.0, (opponentRating - _rating.Value) / 400.0));
            winDelta = K_FACTOR * (1f - expected);
            lossDelta = K_FACTOR * (0f - expected);
        }

        /// <summary>Arms rating tracking for a real (non-solo) match. Call at fight start.</summary>
        public static void BeginMatch(float opponentRating)
        {
            Init();
            _inMatch = true;
            _opponentRating = opponentRating > 0f ? opponentRating : DEFAULT_RATING;
            MelonLogger.Msg($"[Elo] Match armed — you {_rating.Value:F0} vs opponent {_opponentRating:F0}");
        }

        /// <summary>Applies the rating change once per armed match; later calls are ignored.</summary>
        public static void OnMatchEnd(bool won, bool draw = false)
        {
            Init();
            if (!_inMatch) return;
            _inMatch = false;

            float expected = 1f / (1f + (float)Math.Pow(10.0, (_opponentRating - _rating.Value) / 400.0));
            float score = draw ? 0.5f : (won ? 1f : 0f);
            float delta = K_FACTOR * (score - expected);

            _rating.Value = Math.Max(100f, _rating.Value + delta);
            if (draw) _draws.Value++;
            else if (won) _wins.Value++;
            else _losses.Value++;

            SaveSigned();
            EloLeaderboard.UploadRating(_rating.Value);

            MelonLogger.Msg($"[Elo] {(draw ? "DRAW" : won ? "WIN" : "LOSS")} vs {_opponentRating:F0} — " +
                            $"rating {(delta >= 0 ? "+" : "")}{delta:F1} → {_rating.Value:F0} " +
                            $"(W{_wins.Value} L{_losses.Value} D{_draws.Value})");
        }

        /// <summary>Disarms without applying (opponent disconnected before the bout, etc.).</summary>
        public static void CancelMatch() => _inMatch = false;
    }
}
