// DTOs mirroring docs/02-api-spec.md and docs/06-subscriptions-and-multiplayer.md.
// Serialized with Newtonsoft + snake_case naming (see ApiClient.JsonSettings).
using System;
using System.Collections.Generic;

namespace YoMama.Core
{
    public class ApiErrorBody
    {
        public ApiErrorDetail Error;
    }

    public class ApiErrorDetail
    {
        public string Code;
        public string Message;
    }

    public class User
    {
        public string Id;
        public string Email;
        public DateTime CreatedAt;
    }

    public class AuthAnonRequest
    {
        public string DeviceId;
    }

    public class AuthAnonResponse
    {
        public string Token;
        public User User;
    }

    public class Joke
    {
        public string Id;
        public string Body;
        public int LikeCount;
        public DateTime CreatedAt;
    }

    public class JokePage
    {
        public List<Joke> Jokes = new List<Joke>();
        public string NextCursor;
    }

    public enum SwipeDirection { Left, Right }

    public class SwipeRequest
    {
        public string JokeId;
        public string Direction; // "left" | "right"
    }

    public class SwipeResponse
    {
        public string JokeId;
        public string Direction;
        public bool Favorited;
    }

    public class EntitlementLimits
    {
        public int? DailySwipes;   // null = unlimited (Pro)
        public int SwipesToday;
        public bool CanHostRooms;
        public int MaxRoomPlayers;
    }

    public class Entitlements
    {
        public bool Pro;
        public string ProductId;
        public DateTime? ExpiresAt;
        public bool WillRenew;
        public EntitlementLimits Limits = new EntitlementLimits();
    }

    public class RoomTicket
    {
        public string Code;
        public string WsUrl;
        public int MaxPlayers;
    }

    public static class SwipeDirectionExtensions
    {
        public static string ToWire(this SwipeDirection d) => d == SwipeDirection.Right ? "right" : "left";
    }
}
