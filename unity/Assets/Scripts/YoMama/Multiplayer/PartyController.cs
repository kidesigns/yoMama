// Drives the Party ("Roast Room") UI from server room state. The server runs the
// game; this only renders what it's told and sends intents. See docs/05 §6.
using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YoMama.Core;
using YoMama.Paywall;
using YoMama.Swipe;

namespace YoMama.Multiplayer
{
    [RequireComponent(typeof(RoomClient))]
    public class PartyController : MonoBehaviour
    {
        [Header("Entry")]
        [SerializeField] GameObject entryPanel;
        [SerializeField] TMP_InputField nameInput;
        [SerializeField] TMP_InputField codeInput;
        [SerializeField] Button hostButton;          // Pro only (shows paywall otherwise)
        [SerializeField] Button joinButton;          // free

        [Header("Lobby")]
        [SerializeField] GameObject lobbyPanel;
        [SerializeField] TMP_Text codeLabel;
        [SerializeField] TMP_Text playersLabel;
        [SerializeField] Button startButton;         // host only

        [Header("Round")]
        [SerializeField] GameObject roundPanel;
        [SerializeField] RectTransform cardRoot;
        [SerializeField] SwipeCard cardPrefab;
        [SerializeField] TMP_Text roundLabel;
        [SerializeField] TMP_Text timerLabel;
        [SerializeField] TMP_Text progressLabel;

        [Header("Reveal / Podium")]
        [SerializeField] GameObject revealPanel;
        [SerializeField] TMP_Text revealLabel;
        [SerializeField] Button nextButton;          // host only
        [SerializeField] GameObject podiumPanel;
        [SerializeField] TMP_Text podiumLabel;

        [Header("Misc")]
        [SerializeField] TMP_Text statusLabel;
        [SerializeField] Button leaveButton;

        RoomClient _room;
        RoomState _state;
        SwipeCard _card;
        int _votedRound = -1;
        DateTime? _deadline;

        AppServices S => AppServices.Instance;
        bool IAmHost => _state != null && _state.HostId == S.Auth.UserId;

        void Awake()
        {
            _room = GetComponent<RoomClient>();
            _room.StateReceived += OnState;
            _room.RoundStarted += OnRoundStart;
            _room.VoteProgressed += p => progressLabel.text = $"{p.Voted}/{p.Total} voted";
            _room.RoundEnded += OnRoundResult;
            _room.GameEnded += OnGameOver;
            _room.ErrorReceived += e => statusLabel.text = e.Message;
            _room.ConnectionChanged += up => statusLabel.text = up ? "" : "Reconnecting…";

            hostButton.onClick.AddListener(OnHost);
            joinButton.onClick.AddListener(OnJoin);
            startButton.onClick.AddListener(() => _room.StartGame());
            nextButton.onClick.AddListener(() => _room.Next());
            leaveButton.onClick.AddListener(OnLeave);

            ShowPanel(entryPanel);
        }

        void Update()
        {
            if (_deadline == null) return;
            // Server clock is the authority; small device skew only affects the label.
            var remaining = Math.Max(0, (_deadline.Value - DateTime.UtcNow).TotalSeconds);
            timerLabel.text = Mathf.CeilToInt((float)remaining).ToString();
        }

        // ---- Entry ----------------------------------------------------------

        async void OnHost()
        {
            if (!S.Entitlements.IsPro)
            {
                PaywallController.Instance?.Show("Hosting a Party is a Pro feature");
                return;
            }
            try
            {
                var ticket = await S.Api.CreateRoom();
                _room.Connect(ticket.WsUrl, DisplayName());
            }
            catch (ApiException e) when (e.IsProRequired)
            {
                // Server disagrees with our cached state (e.g. just expired) — refresh and show the wall.
                await S.Entitlements.RefreshAsync();
                PaywallController.Instance?.Show("Hosting a Party is a Pro feature");
            }
            catch (ApiException e)
            {
                statusLabel.text = e.Message;
            }
        }

        async void OnJoin()
        {
            var code = codeInput.text.Trim().ToUpperInvariant();
            if (code.Length != 5) { statusLabel.text = "Room codes are 5 letters."; return; }
            try
            {
                var ticket = await S.Api.JoinRoom(code);
                _room.Connect(ticket.WsUrl, DisplayName());
            }
            catch (ApiException e)
            {
                statusLabel.text = e.Code switch
                {
                    "room_not_found" => "No room with that code.",
                    "room_full" => "That room is full.",
                    "room_in_progress" => "That game already started.",
                    _ => e.Message,
                };
            }
        }

        async void OnLeave()
        {
            await _room.Leave();
            _state = null;
            _deadline = null;
            ClearCard();
            ShowPanel(entryPanel);
        }

        string DisplayName()
        {
            var n = nameInput.text.Trim();
            return string.IsNullOrEmpty(n) ? "Player" : n.Substring(0, Math.Min(16, n.Length));
        }

        // ---- Server events --------------------------------------------------

        void OnState(RoomState s)
        {
            _state = s;
            codeLabel.text = s.Code;
            playersLabel.text = string.Join("\n", s.Players.Select(p =>
                $"{p.Name}{(p.Id == s.HostId ? " (host)" : "")}{(p.Connected ? "" : " (away)")}  {p.Score}"));
            startButton.gameObject.SetActive(IAmHost);
            startButton.interactable = s.Players.Count(p => p.Connected) >= 2;
            nextButton.gameObject.SetActive(IAmHost);

            switch (s.Phase)
            {
                case RoomPhase.Lobby:
                    ShowPanel(lobbyPanel);
                    break;
                case RoomPhase.RoundOpen when s.Joke != null && s.Deadline != null:
                    // Reconnected mid-round: rebuild the card from the snapshot.
                    OnRoundStart(new RoundStart { Round = s.Round, Joke = s.Joke, Deadline = s.Deadline.Value });
                    break;
                case RoomPhase.Reveal:
                    ShowPanel(revealPanel);
                    break;
                case RoomPhase.GameOver:
                    ShowPanel(podiumPanel);
                    break;
            }
        }

        void OnRoundStart(RoundStart r)
        {
            ShowPanel(roundPanel);
            roundLabel.text = $"Round {r.Round}/{_state?.TotalRounds}";
            progressLabel.text = "";
            _deadline = r.Deadline;

            ClearCard();
            _card = Instantiate(cardPrefab, cardRoot);
            _card.SetText(r.Joke.Body);
            _card.SetHome(Vector2.zero);
            _card.Interactable = _votedRound != r.Round;
            _card.Swiped += (card, dir) =>
            {
                _votedRound = r.Round;
                _room.Vote(r.Round, dir);
                ClearCard();
                progressLabel.text = "Waiting for everyone…";
            };
        }

        void OnRoundResult(RoundResult r)
        {
            _deadline = null;
            ClearCard();
            ShowPanel(revealPanel);

            var total = r.Loves + r.Nopes;
            var verdict = r.Majority == "right" ? "The room LOVED it"
                        : r.Majority == "left" ? "The room said NOPE"
                        : "Split room";
            var mine = r.Scores.FirstOrDefault(x => x.Id == S.Auth.UserId);
            revealLabel.text = $"{verdict}\n{r.Loves}/{total} loved it" +
                               (mine != null ? $"\n\n+{mine.Delta}  (you: {mine.Score})" : "");
        }

        void OnGameOver(GameOver g)
        {
            _deadline = null;
            ClearCard();
            ShowPanel(podiumPanel);
            podiumLabel.text = string.Join("\n", g.Podium.Select((p, i) =>
                $"{i + 1}. {p.Name} — {p.Score}"));
        }

        // ---- Helpers --------------------------------------------------------

        void ClearCard()
        {
            if (_card != null) Destroy(_card.gameObject);
            _card = null;
        }

        void ShowPanel(GameObject panel)
        {
            entryPanel.SetActive(panel == entryPanel);
            lobbyPanel.SetActive(panel == lobbyPanel);
            roundPanel.SetActive(panel == roundPanel);
            revealPanel.SetActive(panel == revealPanel);
            podiumPanel.SetActive(panel == podiumPanel);
            leaveButton.gameObject.SetActive(panel != entryPanel);
        }
    }
}
