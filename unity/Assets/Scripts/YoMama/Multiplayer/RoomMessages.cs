// WebSocket message DTOs for Party rooms. Protocol: docs/06 §4.
using System;
using System.Collections.Generic;

namespace YoMama.Multiplayer
{
    public static class RoomPhase
    {
        public const string Lobby = "lobby";
        public const string RoundOpen = "round_open";
        public const string Reveal = "reveal";
        public const string GameOver = "game_over";
    }

    public class RoomJoke
    {
        public string Id;
        public string Body;
    }

    public class RoomPlayer
    {
        public string Id;
        public string Name;
        public int Score;
        public bool Connected;
    }

    // ---- Server → client ----------------------------------------------------

    public class RoomState
    {
        public long Seq;
        public string Code;
        public string Phase;
        public string HostId;
        public List<RoomPlayer> Players = new List<RoomPlayer>();
        public int Round;
        public int TotalRounds;
        public RoomJoke Joke;          // present during round_open / reveal
        public DateTime? Deadline;     // server clock, UTC
    }

    public class RoundStart
    {
        public long Seq;
        public int Round;
        public RoomJoke Joke;
        public DateTime Deadline;
    }

    public class VoteProgress
    {
        public long Seq;
        public int Round;
        public int Voted;
        public int Total;
    }

    public class ScoreDelta
    {
        public string Id;
        public int Score;
        public int Delta;
    }

    public class RoundResult
    {
        public long Seq;
        public int Round;
        public int Loves;
        public int Nopes;
        public string Majority;        // "left" | "right" | "tie"
        public List<ScoreDelta> Scores = new List<ScoreDelta>();
    }

    public class PodiumEntry
    {
        public string Id;
        public string Name;
        public int Score;
    }

    public class GameOver
    {
        public long Seq;
        public List<PodiumEntry> Podium = new List<PodiumEntry>();
    }

    public class RoomError
    {
        public string Code;
        public string Message;
    }
}
