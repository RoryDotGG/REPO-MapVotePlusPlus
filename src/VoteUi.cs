using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MapVote;

/// <summary>
/// The overlay pieces, built in code on one canvas. The vote list itself is a game menu: see <see cref="NativeBoard"/>.
///  - Chip: a small HUD pill in the truck that says what is going on and which key opens the vote.
///  - Reveal: a slot-machine reel of map cards that lands on the winner for everyone at the same time, then says how
///    it won.
/// Also routes the keyboard: the toggle key opens the truck vote, and 1-9 / R vote on whichever board has focus.
/// And opens the truck vote by itself on arrival (ShowVoteOnArrival), so each player picks a map or skips first.
/// </summary>
internal sealed class VoteUi : MonoBehaviour
{
    private const float CellWidth = 340f, SpinSeconds = 3.3f, HoldSeconds = 2.8f;
    private const float WindowWidth = 1080f, WindowHeight = 180f;
    /// <summary>Each map's card on the reel: its art with its name over it, with a gap to the next card.</summary>
    private const float CardWidth = CellWidth - 24f, CardHeight = WindowHeight - 40f;
    /// <summary>How dark the wash over each card's art is, so the name on it stays readable over bright art.</summary>
    private const float WashAlpha = 0.62f;
    /// <summary>Where the vote line sits under the window, mirroring the title above it.</summary>
    private const float VotesY = -66f;
    /// <summary>How long after arriving in the truck the vote opens by itself, so the player sees where they are first.</summary>
    private const float PromptDelay = 1f;

    public static VoteUi? Instance { get; private set; }

    private RectTransform _root = null!;
    private Canvas _canvas = null!;

    private RectTransform _chip = null!;
    private CanvasGroup _chipGroup = null!;
    private Image _chipBorder = null!;
    private TextMeshProUGUI _chipKey = null!, _chipTitle = null!, _chipSub = null!;
    private float _chipT;

    private RectTransform _reveal = null!, _revealBody = null!, _window = null!, _reel = null!;
    private CanvasGroup _revealGroup = null!;
    private Image _revealCenter = null!;
    private TextMeshProUGUI _revealTitle = null!, _revealVotes = null!;
    private readonly List<ReelCell> _cells = new();
    private Image? _winnerFrame;
    private string? _reelFor;
    private int _reelWinner, _lastTickCell;
    private float _reelStart = -999f, _punch, _votesT;
    private bool _landed;

    /// <summary>One card on the reel. Its group fades the art and the name together.</summary>
    private sealed class ReelCell
    {
        public RectTransform Rect = null!;
        public CanvasGroup Group = null!;
        public TextMeshProUGUI Name = null!;
    }

    private VoteContext _context, _lastContext;
    private int _seenRevision = -1;
    private float _fontRetry;

    // The arrival prompt: waiting to open it, it is up, or this player closed it without a pick.
    private bool _promptPending, _prompting, _skipped;
    private float _arrivedAt;

    /// <summary>The winner, but only once this client's reel has landed on it, so the vote list can't spoil the reveal.</summary>
    public string? RevealedWinner =>
        VoteSession.Phase == Phase.Revealing && (!_landed || _reelFor != VoteSession.Winner) ? null : VoteSession.Winner;

    private void Awake()
    {
        Instance = this;
        Build();
    }

    private void Update()
    {
        VoteSession.Tick();

        if (!Ui.FontReady && (_fontRetry -= Time.unscaledDeltaTime) <= 0f)
        {
            Ui.TryResolveFont(_root);
            _fontRetry = 1f;
        }

        _context = VoteSession.Context;
        if (_context != _lastContext) OnContextChanged();
        // Ahead of the visibility check: the reveal has to start (and close the truck vote) even while it is covered.
        if (VoteSession.Revision != _seenRevision) OnStateChanged();
        HandleInput();
        UpdatePrompt();

        // A reveal that is still up when the level changes under it (the truck driving off) fades out rather than vanishing.
        bool visible = (_context != VoteContext.None || _revealGroup.alpha > 0.01f) && Ui.FontReady && !GameMenuBlocking();
        _canvas.enabled = visible;
        if (!visible) return;

        AnimateChip();
        AnimateReveal();
    }

    private bool GameMenuBlocking()
    {
        var page = MenuManager.instance ? MenuManager.instance.currentMenuPage : null;
        return _context == VoteContext.LobbyMenu
            ? !page || page!.menuPageIndex != MenuPageIndex.Lobby // settings, cosmetics, etc. sit on top of the lobby
            : (bool)page; // the escape menu or the vote itself in the truck
    }

    private void OnContextChanged()
    {
        _lastContext = _context;
        _reelFor = null;
        _reelStart = -999f;
        _seenRevision = -1;
        if (_context != VoteContext.Truck) NativeBoard.CloseTruckPage();

        _promptPending = _context == VoteContext.Truck && Plugin.ShowVoteOnArrival.Value;
        _prompting = _skipped = false;
        _arrivedAt = Time.unscaledTime;
    }

    private void OnStateChanged()
    {
        _seenRevision = VoteSession.Revision;
        if (VoteSession.Phase == Phase.Revealing && VoteSession.Winner != null && _reelFor != VoteSession.Winner)
            StartReel(VoteSession.Winner, VoteLogic.Odds(VoteSession.Candidates, VoteSession.Votes.Values, VoteSession.PreviousLevel));
    }

    private static bool Typing() => !SemiFunc.NoTextInputsActive() || (ChatManager.instance && ChatManager.instance.chatActive);

    private void HandleInput()
    {
        if (_context == VoteContext.None || Typing()) return;

        if (_context == VoteContext.Truck && Ui.KeyDown(Plugin.ToggleKey.Value)) NativeBoard.ToggleTruckPage();

        var board = NativeBoard.Active;
        if (!board) return;
        if (Ui.KeyDown(Key.R)) board!.QuickVote(0);
        for (int i = 0; i < 9; i++)
            if (Ui.KeyDown(Key.Digit1 + i) || Ui.KeyDown(Key.Numpad1 + i)) board!.QuickVote(i + 1);
    }

    // ---- arrival prompt ------------------------------------------------------------------

    /// <summary>
    /// Shortly after arriving in the truck, opens the vote by itself so this player picks a map or skips before they
    /// get going. It waits for the host's vote to reach this player, and for the screen to be free of other menus.
    /// </summary>
    private void UpdatePrompt()
    {
        if (_prompting)
        {
            if (NativeBoard.PromptOpen) return;
            // Closed by a pick, by Skip, or by Esc or the toggle key: anything but a pick is a skip.
            _prompting = false;
            _skipped = VoteSession.MyVote == null;
            return;
        }
        if (!_promptPending) return;

        // Nothing to ask once the vote is rolling or over, or once this player has voted or opened it themselves.
        var phase = VoteSession.Phase;
        if (!Plugin.ShowVoteOnArrival.Value || phase is Phase.Revealing or Phase.Locked || VoteSession.MyVote != null
            || NativeBoard.TruckPageOpen)
        {
            _promptPending = false;
            return;
        }
        if (phase != Phase.Open || HostCheck.Support != HostSupport.Ok || Time.unscaledTime - _arrivedAt < PromptDelay || Typing()) return;
        if (!NativeBoard.OpenTruckPage(prompt: true)) return; // the escape menu has the screen; try again once it closes

        _promptPending = false;
        _prompting = true;
    }

    // ---- chip ----------------------------------------------------------------------------

    private void AnimateChip()
    {
        var phase = VoteSession.Phase;
        var problem = HostCheck.Problem;
        bool needsMe = phase == Phase.Open && VoteSession.MyVote == null && !_skipped && problem == null;
        // Truck only: in the lobby the vote list is always on screen.
        bool show = _context == VoteContext.Truck && (_reelStart < 0f || Time.unscaledTime - _reelStart > SpinSeconds + HoldSeconds);
        _chipT = Ui.Damp(_chipT, show ? 1f : 0f, 12f);
        _chipGroup.alpha = _chipT;
        _chip.anchoredPosition = new Vector2(-36f + (1f - _chipT) * 40f, 36f);
        if (_chipT < 0.01f) return;

        _chipKey.text = KeyName();
        string status = $"{VoteSession.Votes.Count}/{VoteSession.PlayerCount} VOTED  /  ROLLS ON DEPARTURE";
        string muted = $"<color=#{Ui.HexOf(Ui.Muted)}>";
        string amber = $"<color=#{Ui.HexOf(Ui.Amber)}>";
        // Ahead of the phase: a stale winner from the lobby would otherwise read as the next map.
        if (problem != null)
        {
            _chipTitle.text = "NO MAP VOTE";
            _chipSub.text = $"{muted}{problem.Value.Status}</color>";
        }
        else switch (phase)
        {
            case Phase.Open:
                _chipTitle.text = VoteSession.MyVote is { } mine ? $"YOU PICKED {amber}{MapInfo.DisplayName(mine)}</color>"
                    : _skipped ? "YOU SKIPPED" : "VOTE FOR THE NEXT MAP";
                _chipSub.text = $"{muted}{status}</color>";
                break;
            case Phase.Revealing:
                _chipTitle.text = "ROLLING...";
                _chipSub.text = $"{muted}NEXT MAP INCOMING</color>";
                break;
            default:
                _chipTitle.text = VoteSession.Winner != null ? "NEXT MAP" : "MAP VOTE";
                _chipSub.text = VoteSession.Winner != null ? $"{amber}{MapInfo.DisplayName(VoteSession.Winner)}</color>" : $"{muted}RANDOM MAP</color>";
                break;
        }

        // Breathe the border while this player still has a vote to cast.
        float glow = needsMe ? 0.45f + 0.45f * Mathf.Sin(Time.unscaledTime * 3.2f) : 0.15f;
        _chipBorder.color = Ui.Amber.WithAlpha(glow);
    }

    private string KeyName()
    {
        var key = Plugin.ToggleKey.Value;
        var control = Keyboard.current?[key];
        return (control?.displayName is { Length: > 0 } d ? d : key.ToString()).ToUpperInvariant();
    }

    // ---- reveal --------------------------------------------------------------------------

    private void StartReel(string winner, Dictionary<string, float> odds)
    {
        _reelFor = winner;
        _landed = false;
        _lastTickCell = 0;
        // Line up with the host: the reveal began RevealSeconds before the host's deadline.
        float left = VoteSession.SecondsLeft;
        _reelStart = Time.unscaledTime - (left >= 0f ? VoteSession.RevealSeconds - left : 0f);
        if (_context == VoteContext.Truck) NativeBoard.CloseTruckPage();

        // Fill the reel with names in proportion to their odds so it "feels" like the vote.
        var candidates = VoteSession.Candidates;
        var weighted = candidates.Where(n => odds.TryGetValue(n, out var o) && o > 0f).ToList();
        if (weighted.Count == 0) weighted = candidates.ToList();
        const int cellCount = 34;
        _reelWinner = cellCount - 4;
        foreach (var c in _cells) Destroy(c.Rect.gameObject);
        _cells.Clear();
        string? prev = null;
        for (int i = 0; i < cellCount; i++)
        {
            string name = i == _reelWinner ? winner : PickWeighted(weighted, odds, prev);
            prev = name;
            _cells.Add(BuildCell(name, i == _reelWinner));
        }
        _revealTitle.text = "NEXT MAP";
        _revealTitle.color = Ui.Muted;
        // Counted once, here: the ballots are frozen while the reel spins.
        _revealVotes.text = VoteLine(winner, candidates);
        _votesT = 0f;
        Ui.Sound(m => m.MenuEffectPopUpOpen());
    }

    /// <summary>A map's card: its art under a dark wash, its name on top. The winner's has a frame that lights when it lands.</summary>
    private ReelCell BuildCell(string name, bool winner)
    {
        var rect = Ui.Rect("Cell", _reel).Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CardWidth, CardHeight));
        if (winner)
        {
            _winnerFrame = Ui.Image("Frame", rect, Ui.Amber.WithAlpha(0f));
            _winnerFrame.rectTransform.Fill(-3, -3, -3, -3);
        }
        CardArt(rect, name);
        Ui.Image("Wash", rect, Ui.Panel.WithAlpha(WashAlpha)).rectTransform.Fill();

        var label = Ui.Label("Name", rect, 48f, Ui.Text, TextAlignmentOptions.Center);
        label.rectTransform.Fill(10, 0, 10, 0);
        label.text = MapInfo.DisplayName(name);
        label.enableAutoSizing = true;
        label.fontSizeMin = 24f;
        label.fontSizeMax = 48f;
        return new ReelCell { Rect = rect, Group = rect.gameObject.AddComponent<CanvasGroup>(), Name = label };
    }

    /// <summary>
    /// The level's loading-screen art, as on the vote list: its initial on its colour when it has none, and a "?" for
    /// Random or for a map this client doesn't have.
    /// </summary>
    private static void CardArt(RectTransform card, string name)
    {
        var image = Ui.Rect("Art", card).Fill().gameObject.AddComponent<RawImage>();
        image.raycastTarget = false;
        // Random isn't a level, so it takes the "?" too.
        var info = VoteSession.TryGetPoolLevel(name, out var level) ? MapInfo.Get(level) : null;
        if (info?.Art)
        {
            image.texture = info!.Art;
            image.uvRect = MapInfo.Crop(info.ArtUv, info.Art!, CardWidth / CardHeight);
            return;
        }

        image.color = info != null ? Color.Lerp(Ui.Ink, info.Color, 0.55f) : Ui.Amber.WithAlpha(0.22f);
        var mark = Ui.Label("Mark", image.rectTransform, CardHeight * 0.8f, info != null ? Ui.Ink.WithAlpha(0.75f) : Ui.Amber,
            TextAlignmentOptions.Center, FontStyles.Normal);
        mark.rectTransform.Fill();
        mark.text = info == null ? "?" : info.Name.Length > 0 ? info.Name.Substring(0, 1) : "?";
    }

    /// <summary>
    /// How the winner won, from the ballots: "3 OF 4 VOTES", or the Random ballots it came through, or nobody voting.
    /// It states the count and nothing about how the winner was drawn, so it reads right whatever the vote rules are.
    /// </summary>
    private static string VoteLine(string winner, IReadOnlyList<string> candidates)
    {
        int total = 0, forWinner = 0, forRandom = 0;
        foreach (var ballot in VoteSession.Votes.Values)
        {
            if (ballot == winner) forWinner++;
            else if (ballot == VoteLogic.RandomChoice) forRandom++;
            else if (!candidates.Contains(ballot)) continue; // a ballot for a map that isn't up counts for nothing
            total++;
        }

        if (total == 0) return "NOBODY VOTED  /  RANDOM MAP";
        if (forWinner == 0 && forRandom > 0)
            return total == 1 ? "THE ONLY VOTE WAS RANDOM" : $"{Tally(forRandom, total)} FOR RANDOM";
        return Tally(forWinner, total);
    }

    private static string Tally(int votes, int total) =>
        votes != total ? $"{votes} OF {total} {(total == 1 ? "VOTE" : "VOTES")}"
        : total == 1 ? "THE ONLY VOTE" : $"ALL {total} VOTES";

    private static string PickWeighted(List<string> names, Dictionary<string, float> odds, string? avoid)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            float roll = Random.value * names.Sum(n => odds.TryGetValue(n, out var o) ? Mathf.Max(o, 0.05f) : 0.05f);
            foreach (var n in names)
            {
                roll -= odds.TryGetValue(n, out var o) ? Mathf.Max(o, 0.05f) : 0.05f;
                if (roll <= 0f && (n != avoid || names.Count == 1)) return n;
            }
        }
        return names[Random.Range(0, names.Count)];
    }

    private void AnimateReveal()
    {
        float elapsed = Time.unscaledTime - _reelStart;
        bool show = _reelFor != null && elapsed < SpinSeconds + HoldSeconds;
        // Quicker out once the level has changed under it, so it is gone before the loading screen.
        float speed = show ? 14f : _context == VoteContext.None ? 12f : 6f;
        _revealGroup.alpha = Ui.Damp(_revealGroup.alpha, show ? 1f : 0f, speed);
        bool active = _revealGroup.alpha > 0.01f;
        if (_reveal.gameObject.activeSelf != active) _reveal.gameObject.SetActive(active);
        if (!active || _cells.Count == 0) return;
        // Grows into place as it fades in.
        _revealBody.localScale = Vector3.one * (0.9f + 0.1f * _revealGroup.alpha);

        float p = Mathf.Clamp01(elapsed / SpinSeconds);
        float eased = 1f - Mathf.Pow(1f - p, 4f);
        float offset = eased * _reelWinner * CellWidth;

        // Only a reel that is still showing ticks and lands: one cut short just fades.
        int centre = Mathf.RoundToInt(offset / CellWidth);
        if (show && centre != _lastTickCell && !_landed)
        {
            _lastTickCell = centre;
            Ui.Tick(0.85f + 0.35f * p);
        }

        if (show && p >= 1f && !_landed)
        {
            _landed = true;
            _punch = 1f;
            _revealTitle.color = Ui.Amber;
            Ui.Click(MenuManager.MenuClickEffectType.Confirm, 0.9f);
        }

        for (int i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            float x = i * CellWidth - offset;
            bool near = Mathf.Abs(x) < CellWidth * 2.6f;
            var go = cell.Rect.gameObject;
            if (go.activeSelf != near) go.SetActive(near);
            if (!near) continue;
            // The whole card, art and name, shrinks and fades away from the centre.
            float d = Mathf.Clamp01(Mathf.Abs(x) / (CellWidth * 1.7f));
            cell.Rect.anchoredPosition = new Vector2(x, 0);
            cell.Rect.localScale = Vector3.one * (1f - 0.2f * d + (i == _reelWinner ? _punch * 0.12f : 0f));
            cell.Group.alpha = 1f - d * 0.85f;
            cell.Name.color = _landed && i == _reelWinner ? Ui.Amber : Ui.Text;
        }

        _punch = Ui.Damp(_punch, 0f, 6f);
        _window.localScale = Vector3.one * (1f + _punch * 0.05f);
        _revealCenter.color = Ui.Amber.WithAlpha(_landed ? 0.16f + _punch * 0.3f : 0.07f);
        // The winner's frame flares with the punch and stays lit.
        if (_winnerFrame) _winnerFrame!.color = Ui.Amber.WithAlpha(_landed ? 0.75f + 0.25f * _punch : 0f);

        // How it won rises into place under the winner once it lands.
        _votesT = Ui.Damp(_votesT, _landed ? 1f : 0f, 8f);
        _revealVotes.color = Ui.Text.WithAlpha(_votesT);
        _revealVotes.rectTransform.anchoredPosition = new Vector2(0f, VotesY - (1f - _votesT) * 12f);
    }

    // ---- layout --------------------------------------------------------------------------

    private void Build()
    {
        var canvasGo = new GameObject("MapVotePlusPlusCanvas") { layer = 5 }; // UI
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 900;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;
        _root = (RectTransform)canvasGo.transform;

        BuildChip();
        BuildReveal();
        _canvas.enabled = false;
    }

    private static Image Shadow(Transform parent, float spread)
    {
        var shadow = Ui.Image("Shadow", parent, Color.black.WithAlpha(0.5f), Ui.Rounded);
        shadow.rectTransform.Fill(-spread, -spread * 0.6f, -spread, -spread * 1.4f);
        return shadow;
    }

    private void BuildChip()
    {
        _chip = Ui.Rect("Chip", _root).Place(new Vector2(1f, 0f), new Vector2(-36f, 36f), new Vector2(420, 70));
        _chipGroup = _chip.gameObject.AddComponent<CanvasGroup>();
        _chipGroup.alpha = 0f;
        Shadow(_chip, 12f);
        _chipBorder = Ui.Image("Border", _chip, Ui.Amber.WithAlpha(0.3f), Ui.Rounded);
        _chipBorder.rectTransform.Fill(-2, -2, -2, -2);
        Ui.Image("Bg", _chip, Ui.Panel, Ui.Rounded).rectTransform.Fill();

        var badge = Ui.Image("Key", _chip, Ui.Amber, Ui.RoundedSmall);
        badge.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(12, 0), new Vector2(46, 46));
        _chipKey = Ui.Label("KeyText", badge.rectTransform, 22f, Ui.Ink, TextAlignmentOptions.Center);
        _chipKey.rectTransform.Fill(3, 3, 3, 3);
        _chipKey.enableAutoSizing = true;
        _chipKey.fontSizeMin = 10f;
        _chipKey.fontSizeMax = 22f;

        _chipTitle = Ui.Label("Title", _chip, 19f, Ui.Text, TextAlignmentOptions.BottomLeft);
        _chipTitle.rectTransform.Place(new Vector2(0, 1), new Vector2(72, -10), new Vector2(330, 28));
        _chipTitle.characterSpacing = 2f;
        _chipSub = Ui.Label("Sub", _chip, 15f, Ui.Muted, TextAlignmentOptions.TopLeft);
        _chipSub.rectTransform.Place(new Vector2(0, 1), new Vector2(72, -39), new Vector2(330, 20));
        _chipSub.characterSpacing = 2f;
    }

    private void BuildReveal()
    {
        // Full screen, so the dim behind the reel fades in and out with it.
        _reveal = Ui.Rect("Reveal", _root).Fill();
        _revealGroup = _reveal.gameObject.AddComponent<CanvasGroup>();
        _revealGroup.alpha = 0f;
        Ui.Image("Dim", _reveal, Color.black.WithAlpha(0.72f)).rectTransform.Fill();

        // The reel window sits dead centre with its title above it.
        _revealBody = Ui.Rect("Body", _reveal).Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(WindowWidth, WindowHeight));
        _revealTitle = Ui.Label("Title", _revealBody, 30f, Ui.Muted, TextAlignmentOptions.Center);
        _revealTitle.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, 68), new Vector2(WindowWidth, 36));
        _revealTitle.characterSpacing = 12f;
        _revealVotes = Ui.Label("Votes", _revealBody, 26f, Ui.Text.WithAlpha(0f), TextAlignmentOptions.Center);
        _revealVotes.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, VotesY), new Vector2(WindowWidth, 32));
        _revealVotes.characterSpacing = 8f;

        _window = Ui.Rect("Window", _revealBody).Fill();
        Shadow(_window, 24f);
        Ui.Image("Frame", _window, Ui.Amber.WithAlpha(0.9f), Ui.Rounded).rectTransform.Fill(-2, -2, -2, -2);
        Ui.Image("Bg", _window, Ui.Panel, Ui.Rounded).rectTransform.Fill();
        // A glow round the centre card, under the reel.
        _revealCenter = Ui.Image("Centre", _window, Ui.Amber.WithAlpha(0.07f), Ui.RoundedSmall);
        _revealCenter.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CardWidth + 14f, CardHeight + 14f));

        var mask = Ui.Rect("Mask", _window).Fill(8, 8, 8, 8);
        mask.gameObject.AddComponent<RectMask2D>();
        _reel = Ui.Rect("Reel", mask).Place(new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

        // Edge fades so cards slide in and out instead of being cut.
        var leftFade = Ui.Image("FadeL", _window, Ui.Panel, Ui.FadeRight).rectTransform;
        leftFade.Place(new Vector2(0, 0.5f), new Vector2(8, 0), new Vector2(220, WindowHeight - 16f));
        leftFade.localScale = new Vector3(-1, 1, 1);
        leftFade.pivot = new Vector2(1, 0.5f);
        var rightFade = Ui.Image("FadeR", _window, Ui.Panel, Ui.FadeRight).rectTransform;
        rightFade.Place(new Vector2(1, 0.5f), new Vector2(-8, 0), new Vector2(220, WindowHeight - 16f));

        foreach (var (anchor, y) in new[] { (new Vector2(0.5f, 1), 8f), (new Vector2(0.5f, 0), -8f) })
        {
            var marker = Ui.Image("Marker", _window, Ui.Amber, Ui.RoundedSmall).rectTransform;
            marker.anchorMin = marker.anchorMax = anchor;
            marker.pivot = new Vector2(0.5f, 0.5f);
            marker.anchoredPosition = new Vector2(0, y);
            marker.sizeDelta = new Vector2(18, 18);
            marker.localRotation = Quaternion.Euler(0, 0, 45);
        }
        _reveal.gameObject.SetActive(false);
    }
}
