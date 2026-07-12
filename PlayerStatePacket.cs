using System;
using System.IO;
using UnityEngine;

namespace ToFMultiplayer
{
    // We (de)serialize field by field instead of using Marshal — that struct layout
    // stuff isn't reliable on Il2Cpp with Unity's value types.
    public struct PlayerStatePacket
    {
        // ── Packet type constants ────────────────────────────────
        public const byte PACKET_TYPE_POSITION_UPDATE = 0;
        public const byte PACKET_TYPE_DAMAGE_EVENT = 1;
        public const byte PACKET_TYPE_KNOCKDOWN = 2;
        public const byte PACKET_TYPE_READY_UP = 3;
        // Round lifecycle stuff — host sends these to keep the guest's UI in sync.
        // Host's BoutController is the source of truth, guest just reacts to it.
        public const byte PACKET_TYPE_ROUND_START = 4;   // RefStartRound fired (bell rang)
        public const byte PACKET_TYPE_ROUND_END = 5;   // EndRound fired
        public const byte PACKET_TYPE_BREAK_START = 6;   // StartBreak fired (roundData = breakTime)
        public const byte PACKET_TYPE_BREAK_SKIP_VOTE = 7;   // remote player held the break skip button
        public const byte PACKET_TYPE_DISCONNECT = 8;         // player is intentionally disconnecting
        public const byte PACKET_TYPE_BOUT_END = 9;
        public const byte PACKET_TYPE_RETIRE = 10;  // player held quit trigger — sync exit to remote
        public const byte PACKET_TYPE_CORNER_ASSIGN = 11;
        public const byte PACKET_TYPE_START_MATCH = 12;   // host → guest: host pressed "Start Lobby"
        public const byte PACKET_TYPE_GET_UP = 13;        // sender's player stood back up after a knockdown
        public const byte PACKET_TYPE_PING = 14;          // roundData = sender's realtimeSinceStartup
        public const byte PACKET_TYPE_PONG = 15;          // roundData = echoed PING timestamp
        public const byte PACKET_TYPE_REMATCH_VOTE = 16;  // sender held the post-match Rematch button

        // ---- Header (5 bytes) ----
        public byte packetType;
        public uint sequenceNumber;

        // ---- Head (28 bytes) ----
        public Vector3 headPos;
        public Quaternion headRot;

        // ---- Left hand (28 bytes) ----
        public Vector3 leftHandPos;
        public Quaternion leftHandRot;

        // ---- Right hand (28 bytes) ----
        public Vector3 rightHandPos;
        public Quaternion rightHandRot;
        public int cornerAssignment;

        // ---- Punch state (17 bytes) ----
        public byte isPunching;
        public Vector3 punchVelocity;
        public int punchType;

        // ---- Damage / round state (18 bytes) ----
        public float traumaDamage;
        public float painDamage;
        public float dizzyLevel;
        public byte isDown;
        public byte isKnockedOut;
        // This one does double duty depending on packet type: breakTime for BREAK_START,
        // round number for ROUND_START/END, wentToRound for BOUT_END.
        public float roundData;

        // Total: 5 + 28 + 28 + 28 + 17 + 18 = 124 bytes

        // --------------------------------------------------------
        // Convenience bool accessors
        // --------------------------------------------------------
        public bool IsPunching { get => isPunching != 0; set => isPunching = value ? (byte)1 : (byte)0; }
        public bool IsDown { get => isDown != 0; set => isDown = value ? (byte)1 : (byte)0; }
        public bool IsKnockedOut { get => isKnockedOut != 0; set => isKnockedOut = value ? (byte)1 : (byte)0; }

        // --------------------------------------------------------
        // Factory helpers
        // --------------------------------------------------------

        public static PlayerStatePacket CreatePositionUpdate(
            Vector3 headPos, Quaternion headRot,
            Vector3 leftHandPos, Quaternion leftHandRot,
            Vector3 rightHandPos, Quaternion rightHandRot,
            bool punching, Vector3 punchVelocity, int punchType,
            uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_POSITION_UPDATE;
            p.sequenceNumber = sequenceNumber;
            p.headPos = headPos;
            p.headRot = headRot;
            p.leftHandPos = leftHandPos;
            p.leftHandRot = leftHandRot;
            p.rightHandPos = rightHandPos;
            p.rightHandRot = rightHandRot;
            p.IsPunching = punching;
            p.punchVelocity = punchVelocity;
            p.punchType = punchType;
            return p;
        }

        /// <summary>
        /// A hit landed on the sender's local ghost (which represents the RECEIVER).
        /// Field packing:
        ///   traumaDamage = delta trauma to apply
        ///   painDamage   = delta pain to apply
        ///   dizzyLevel   = delta dizzy to apply
        ///   roundData    = raw damage of the hit (for round scoring + haptics)
        ///   headPos.x    = attacker's pain threshold (for round scoring)
        /// </summary>
        public static PlayerStatePacket CreateDamageEvent(
            float deltaTrauma, float deltaPain, float deltaDizzy,
            float rawDamage, float painThreshold, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_DAMAGE_EVENT;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.traumaDamage = deltaTrauma;
            p.painDamage = deltaPain;
            p.dizzyLevel = deltaDizzy;
            p.roundData = rawDamage;
            p.headPos = new Vector3(painThreshold, 0f, 0f);
            return p;
        }

        /// <summary>
        /// A boxer went down. `corner` is in the SENDER's local frame (Red = the sender's
        /// own player, Blue = the sender's ghost i.e. the RECEIVER) — the receiver flips it.
        /// headPos.x carries the computed time on the floor so both sides agree.
        /// </summary>
        public static PlayerStatePacket CreateKnockdown(int corner, float floorTime, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_KNOCKDOWN;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.roundData = corner;
            p.headPos = new Vector3(floorTime, 0f, 0f);
            p.IsDown = true;
            p.IsKnockedOut = true;
            return p;
        }

        /// <summary>Sender's player finished their knockdown and stood back up.</summary>
        public static PlayerStatePacket CreateGetUp(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_GET_UP;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        public static PlayerStatePacket CreateReadyUp(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_READY_UP;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>Sender voted for a rematch on the post-match screen.</summary>
        public static PlayerStatePacket CreateRematchVote(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_REMATCH_VOTE;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>Latency probe. The receiver echoes the timestamp back as a PONG.</summary>
        public static PlayerStatePacket CreatePing(float timestamp, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_PING;
            p.sequenceNumber = sequenceNumber;
            p.roundData = timestamp;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>Reply to a PING, carrying the original sender's timestamp unchanged.</summary>
        public static PlayerStatePacket CreatePong(float timestamp, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_PONG;
            p.sequenceNumber = sequenceNumber;
            p.roundData = timestamp;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>Host → guest: bell just rang for roundNumber.</summary>
        public static PlayerStatePacket CreateRoundStart(int roundNumber, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_ROUND_START;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.roundData = (float)roundNumber;
            return p;
        }

        /// <summary>Host → guest: round just ended.</summary>
        public static PlayerStatePacket CreateRoundEnd(int roundNumber, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_ROUND_END;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.roundData = (float)roundNumber;
            return p;
        }

        /// <summary>Host → guest: break started; roundData = breakTime seconds.</summary>
        public static PlayerStatePacket CreateBreakStart(float breakTime, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_BREAK_START;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.roundData = breakTime;
            return p;
        }

        /// <summary>Guest → host: guest player held the break-skip button.</summary>
        public static PlayerStatePacket CreateBreakSkipVote(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_BREAK_SKIP_VOTE;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>Sent before intentional disconnect so remote can clean up gracefully.</summary>
        public static PlayerStatePacket CreateDisconnectNotice(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_DISCONNECT;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>
        /// Either player → remote: local player held the quit trigger.
        /// Remote should call BoutController.Retire() on their machine.
        /// </summary>
        public static PlayerStatePacket CreateRetireNotice(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_RETIRE;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        /// <summary>Host → guest: host pressed "Start Lobby"; guest should load into the match.</summary>
        public static PlayerStatePacket CreateStartMatch(uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_START_MATCH;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            return p;
        }

        public static PlayerStatePacket CreateCornerAssignment(int corner, uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_CORNER_ASSIGN;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.cornerAssignment = corner;
            return p;
        }

        /// <summary>
        /// Host → guest: the bout has ended. Carries all data needed to replicate
        /// BoutResults and trigger PostMatchSetupAction on the guest side.
        ///
        /// Field packing (reuses existing struct fields — no size change):
        ///   roundData    = wentToRound
        ///   traumaDamage = (float)winner          (BoutResults.Winner: 0=Draw, 1=Red, 2=Blue)
        ///   painDamage   = (float)winCondition    (BoutResults.WinCondition: 0=Decision, 1=KO, 2=TKO, 3=Retirement)
        ///   dizzyLevel   = (float)redScoredCount
        ///   headPos.x    = (float)blueScoredCount
        ///   headPos.y    = (float)drawScoredCount
        ///   headPos.z    = (float)celebrateIndex  (syncs PostCelebrateStateMachine random pick, 0-3)
        /// </summary>
        public static PlayerStatePacket CreateBoutEnd(
            int winner, int winCondition, int wentToRound,
            int redScored, int blueScored, int drawScored,
            int celebrateIndex,
            uint sequenceNumber)
        {
            var p = new PlayerStatePacket();
            p.packetType = PACKET_TYPE_BOUT_END;
            p.sequenceNumber = sequenceNumber;
            p.headRot = Quaternion.identity;
            p.leftHandRot = Quaternion.identity;
            p.rightHandRot = Quaternion.identity;
            p.roundData = (float)wentToRound;
            p.traumaDamage = (float)winner;
            p.painDamage = (float)winCondition;
            p.dizzyLevel = (float)redScored;
            p.headPos = new Vector3((float)blueScored, (float)drawScored, (float)celebrateIndex);
            return p;
        }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(packetType);
                w.Write(sequenceNumber);

                w.Write(headPos.x); w.Write(headPos.y); w.Write(headPos.z);
                w.Write(headRot.x); w.Write(headRot.y); w.Write(headRot.z); w.Write(headRot.w);

                w.Write(leftHandPos.x); w.Write(leftHandPos.y); w.Write(leftHandPos.z);
                w.Write(leftHandRot.x); w.Write(leftHandRot.y); w.Write(leftHandRot.z); w.Write(leftHandRot.w);

                w.Write(rightHandPos.x); w.Write(rightHandPos.y); w.Write(rightHandPos.z);
                w.Write(rightHandRot.x); w.Write(rightHandRot.y); w.Write(rightHandRot.z); w.Write(rightHandRot.w);

                w.Write(cornerAssignment);

                w.Write(isPunching);
                w.Write(punchVelocity.x); w.Write(punchVelocity.y); w.Write(punchVelocity.z);
                w.Write(punchType);

                w.Write(traumaDamage);
                w.Write(painDamage);
                w.Write(dizzyLevel);
                w.Write(isDown);
                w.Write(isKnockedOut);
                w.Write(roundData);

                return ms.ToArray();
            }
        }

        public static PlayerStatePacket Deserialize(byte[] buf)
        {
            using (var ms = new MemoryStream(buf))
            using (var r = new BinaryReader(ms))
            {
                var p = new PlayerStatePacket();

                p.packetType = r.ReadByte();
                p.sequenceNumber = r.ReadUInt32();

                p.headPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                p.headRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                p.leftHandPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                p.leftHandRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                p.rightHandPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                p.rightHandRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

                p.cornerAssignment = r.ReadInt32();

                p.isPunching = r.ReadByte();
                p.punchVelocity = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                p.punchType = r.ReadInt32();

                p.traumaDamage = r.ReadSingle();
                p.painDamage = r.ReadSingle();
                p.dizzyLevel = r.ReadSingle();
                p.isDown = r.ReadByte();
                p.isKnockedOut = r.ReadByte();
                p.roundData = r.ReadSingle();

                return p;
            }
        }
    }
}