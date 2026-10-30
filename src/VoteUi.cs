using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MapVote;

/// <summary>
/// Three pieces, all built in code on one overlay canvas:
///  - Board: the vote list (mouse, 1-9 / R hotkeys, wheel scroll).
///  - Chip: a small HUD pill that says what is going on and which key opens the board.
///  - Reveal: a slot-machine reel that lands on the winner for everyone at the same time.
/// </summary>
internal sealed class VoteUi : MonoBehaviour
{
    private const float BoardWidth = 600f, HeaderHeight = 120f, FooterHeight = 50f, RowHeight = 70f, RowGap = 8f, Side = 20f;
    private const int MaxVisibleRows = 7;
    private const float ThumbWidth = 112f, ThumbHeight = RowHeight - 12f, TextLeft = 132f;
    private const float CellWidth = 250f, SpinSeconds = 3.3f, HoldSeconds = 2.8f;
    private const float CursorSize = 52f; // matches the game's menu cursor at 1080p
    // The game's lobby panel, measured at 16:9 in 1080p units. The board lines up with its top, sits as far from the
    // right edge as the panel is from the left, and grows to the panel's height (capped).
    private const float LobbyTop = 254f, LobbyBottom = 1018f, LobbyMargin = 91f, LobbyMaxScale = 1.2f;
    private static readonly List<InputKey> KeysKeptWhileVoting = new() { InputKey.PushToTalk };

    private sealed class RowView
    {
        public string Choice = "";
        public RectTransform Rect = null!, Inner = null!;
        public CanvasGroup Group = null!;
        public Image Bg = null!, Border = null!, SelectBar = null!, OddsFill = null!;
        public TextMeshProUGUI Name = null!, Voters = null!, Count = null!, Percent = null!;
        public Color Accent;
        public float Hover, Selected, Odds, OddsTarget, Pulse;
        public int VoteCount;
    }

    private RectTransform _root = null!;
    private Canvas _canvas = null!;

    private RectTransform _board = null!, _viewport = null!, _content = null!;
    private CanvasGroup _boardGroup = null!;
    private TextMeshProUGUI _subtitle = null!, _voted = null!, _timer = null!, _footer = null!;
    private RectTransform _timerRoot = null!, _votedCaption = null!;
    private Image _timerRing = null!;
    private readonly List<RowView> _rows = new();
    private string _poolKey = "";
    private bool _boardOpen;
    private float _boardT, _scroll, _scrollTarget, _poolCheck;
    private int _hovered = -1;

    private RectTransform _chip = null!;
    private CanvasGroup _chipGroup = null!;
    private Image _chipBorder = null!;
    private TextMeshProUGUI _chipKey = null!, _chipTitle = null!, _chipSub = null!;
    private float _chipT;

    private RectTransform _reveal = null!, _window = null!, _reel = null!;
    private CanvasGroup _revealGroup = null!;
    private Image _revealCenter = null!;
    private TextMeshProUGUI _revealTitle = null!;
    private readonly List<TextMeshProUGUI> _cells = new();
    private string? _reelFor;
    private int _reelWinner, _lastTickCell;
    private float _reelStart = -999f, _punch;
    private bool _landed;

    private RectTransform _cursor = null!;
    private VoteContext _context, _lastContext;
    private int _seenRevision = -1;
    private float _fontRetry;

    private void Awake() => Build();

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
        bool visible = _context != VoteContext.None && Ui.FontReady && !GameMenuBlocking();
        _canvas.enabled = visible;
        if (!visible)
        {
            if (_context == VoteContext.Truck) _boardOpen = false;
            return;
        }

        if ((_poolCheck -= Time.unscaledDeltaTime) <= 0f) { RebuildRowsIfPoolChanged(); _poolCheck = 2f; }
        if (VoteSession.Revision != _seenRevision) OnStateChanged();

        HandleInput();
        AnimateBoard();
        AnimateChip();
        AnimateReveal();
        AnimateCursor();
    }

    private void LateUpdate()
    {
        // The truck locks the cursor every frame; keep it free while our board is up.
        if (_canvas.enabled && _boardOpen && _context == VoteContext.Truck)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = false; // we draw our own so it matches the board
        }
    }

    private bool GameMenuBlocking()
    {
        var page = MenuManager.instance ? MenuManager.instance.currentMenuPage : null;
        return _context == VoteContext.LobbyMenu
            ? !page || page!.menuPageIndex != MenuPageIndex.Lobby // settings, cosmetics, etc. sit on top of the lobby
            : (bool)page; // escape menu in the truck
    }

    private void OnContextChanged()
    {
        _lastContext = _context;
        _boardOpen = _context == VoteContext.LobbyMenu; // docked open in the lobby, opt-in in the truck
        _scrollTarget = _scroll = 0f;
        _reelFor = null;
        _reelStart = -999f;
        _poolKey = "";
        RebuildRowsIfPoolChanged();
        Place();
    }

    // ---- state ---------------------------------------------------------------------------

    private void OnStateChanged()
    {
        _seenRevision = VoteSession.Revision;
        var pool = VoteSession.PoolNames;
        var odds = VoteLogic.Odds(pool, VoteSession.Votes.Values, VoteSession.PreviousLevel);

        foreach (var row in _rows)
        {
            var voters = VoteSession.Votes.Where(v => v.Value == row.Choice).Select(v => v.Key).OrderBy(a => a).ToList();
            if (voters.Count > row.VoteCount) row.Pulse = Mathf.Max(row.Pulse, 0.6f); // someone just joined this pick
            row.VoteCount = voters.Count;
            row.OddsTarget = odds.TryGetValue(row.Choice, out var o) ? o : 0f;
            row.Count.text = voters.Count.ToString();
            row.Percent.text = $"{Mathf.RoundToInt(row.OddsTarget * 100f)}%";
            row.Voters.text = voters.Count == 0
                ? $"<color=#{Ui.HexOf(Ui.Muted)}>{IdleLine(row.Choice)}</color>"
                : string.Join("   ", voters.Select(a => $"<color=#{Ui.HexOf(Ui.PlayerColor(a))}>{VoteSession.PlayerName(a).ToUpperInvariant()}</color>"));
        }

        if (VoteSession.Phase == Phase.Revealing && VoteSession.Winner != null && _reelFor != VoteSession.Winner)
            StartReel(VoteSession.Winner, odds);
    }

    /// <summary>The winner, but only once this client's reel has landed on it, so the list can't spoil the reveal.</summary>
    private string? RevealedWinner =>
        VoteSession.Phase == Phase.Revealing && (!_landed || _reelFor != VoteSession.Winner) ? null : VoteSession.Winner;

    private static string IdleLine(string choice)
    {
        if (choice == VoteLogic.RandomChoice) return "LET FATE DECIDE";
        return choice == VoteSession.PreviousLevel ? "LAST PLAYED" : "NO VOTES";
    }

    private void RebuildRowsIfPoolChanged()
    {
        var pool = VoteSession.Pool;
        var key = string.Join("|", pool.Select(l => l.name));
        if (key == _poolKey) return;
        _poolKey = key;

        foreach (var row in _rows) Destroy(row.Rect.gameObject);
        _rows.Clear();
        _rows.Add(BuildRow(VoteLogic.RandomChoice, null, 0));
        for (int i = 0; i < pool.Count; i++) _rows.Add(BuildRow(pool[i].name, pool[i], i + 1));

        _content.sizeDelta = new Vector2(0, _rows.Count * (RowHeight + RowGap));
        int visible = Mathf.Min(_rows.Count, MaxVisibleRows);
        _board.sizeDelta = new Vector2(BoardWidth, HeaderHeight + FooterHeight + visible * (RowHeight + RowGap) + 4f);
        _seenRevision = -1;
    }

    // ---- input ---------------------------------------------------------------------------

    private void HandleInput()
    {
        bool typing = !SemiFunc.NoTextInputsActive() || (ChatManager.instance && ChatManager.instance.chatActive);
        bool truck = _context == VoteContext.Truck;

        if (!typing && Ui.KeyDown(Plugin.ToggleKey.Value)) SetBoardOpen(!_boardOpen);
        if (truck && _boardOpen && Ui.KeyDown(Key.Escape)) SetBoardOpen(false);

        var mouse = Mouse.current;
        if (!_boardOpen)
        {
            if (!truck && mouse != null && mouse.leftButton.wasPressedThisFrame && Ui.Hovered(_chip)) SetBoardOpen(true);
            _hovered = -1;
            return;
        }

        if (truck)
        {
            if (InputManager.instance) InputManager.instance.DisableControlsExcept(0.15f, KeysKeptWhileVoting);
            if (CursorManager.instance) CursorManager.instance.Unlock(0.15f);
            if (GameDirector.instance) GameDirector.instance.disableEscMenuTimer = 0.2f;
        }

        // Hover only counts inside the scroll viewport so clipped rows can't be clicked.
        int hovered = -1;
        if (Ui.Hovered(_viewport))
            for (int i = 0; i < _rows.Count; i++)
                if (Ui.Hovered(_rows[i].Rect)) { hovered = i; break; }
        if (hovered != _hovered && hovered >= 0 && VoteSession.Phase == Phase.Open) Ui.Sound(m => m.MenuEffectHover());
        _hovered = hovered;

        if (mouse != null)
        {
            if (mouse.leftButton.wasPressedThisFrame && _hovered >= 0) Vote(_rows[_hovered]);
            if (mouse.rightButton.wasPressedThisFrame && VoteSession.MyVote != null)
            {
                VoteSession.ClearMyVote();
                Ui.Click(MenuManager.MenuClickEffectType.Deny);
            }
            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f) _scrollTarget -= Mathf.Sign(wheel) * (RowHeight + RowGap);
        }

        if (!typing)
        {
            if (Ui.KeyDown(Key.R)) Vote(_rows[0]);
            for (int i = 0; i < 9 && i + 1 < _rows.Count; i++)
                if (Ui.KeyDown(Key.Digit1 + i) || Ui.KeyDown(Key.Numpad1 + i)) Vote(_rows[i + 1]);
        }

        float max = Mathf.Max(0f, _content.sizeDelta.y - _viewport.rect.height);
        _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, max);
    }

    private void Vote(RowView row)
    {
        if (VoteSession.Phase != Phase.Open)
        {
            Ui.Click(MenuManager.MenuClickEffectType.Dud);
            return;
        }
        VoteSession.Cast(row.Choice);
        row.Pulse = 1f;
        Ui.Click(MenuManager.MenuClickEffectType.Confirm);
    }

    private void SetBoardOpen(bool open)
    {
        if (open == _boardOpen) return;
        _boardOpen = open;
        Ui.Sound(m => { if (open) m.MenuEffectPopUpOpen(); else m.MenuEffectPopUpClose(); });
    }

    // ---- animation -----------------------------------------------------------------------

    private void AnimateBoard()
    {
        _boardT = Ui.Damp(_boardT, _boardOpen ? 1f : 0f, 14f);
        bool show = _boardT > 0.01f;
        if (_board.gameObject.activeSelf != show) _board.gameObject.SetActive(show);
        if (!show) return;

        _boardGroup.alpha = _boardT;
        _board.localScale = Vector3.one * (BoardScale() * (0.96f + 0.04f * _boardT));
        _board.anchoredPosition = BoardAnchor() + new Vector2(0, -(1f - _boardT) * 18f);

        var phase = VoteSession.Phase;
        float left = VoteSession.SecondsLeft;
        int voted = VoteSession.Votes.Count, players = VoteSession.PlayerCount;
        _voted.text = $"{voted}<size=60%><color=#{Ui.HexOf(Ui.Muted)}>/{players}</color></size>";
        _subtitle.text = phase switch
        {
            Phase.Open when left >= 0f => voted >= players ? "EVERYONE'S IN. LOCKING..." : "VOTING CLOSES SOON",
            Phase.Open when _context == VoteContext.LobbyMenu => "OPEN UNTIL THE HOST PRESSES START",
            Phase.Open => "PICK THE NEXT MAP. NO VOTES = RANDOM",
            Phase.Revealing => "ROLLING...",
            _ when VoteSession.Winner != null => $"NEXT MAP  <color=#{Ui.HexOf(Ui.Amber)}>{MapInfo.DisplayName(VoteSession.Winner)}</color>",
            _ => "WAITING FOR HOST",
        };

        // Countdown ring drains from full; idle (no timer yet) shows a dim full ring.
        float total = Mathf.Max(1f, Plugin.VoteSeconds.Value);
        float fill = phase == Phase.Open && left >= 0f ? Mathf.Clamp01(left / total) : phase == Phase.Open ? 1f : 0f;
        _timerRing.fillAmount = Ui.Damp(_timerRing.fillAmount, fill, 10f);
        bool urgent = left >= 0f && left <= 5f;
        _timerRing.color = Color.Lerp(Ui.Amber, Ui.Hot, urgent ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 12f) : 0f)
            .WithAlpha(phase == Phase.Open && left < 0f ? 0.25f : 1f);
        _timer.text = phase == Phase.Open && left >= 0f ? Mathf.CeilToInt(left).ToString() : phase == Phase.Open ? "--" : "OK";

        bool truck = _context == VoteContext.Truck;
        _footer.text = phase == Phase.Open
            ? $"CLICK  VOTE     1-9 / R  QUICK VOTE     RMB  CLEAR{(truck ? $"     {KeyName()} / ESC  CLOSE" : $"     {KeyName()}  HIDE")}"
            : truck ? $"{KeyName()} / ESC  CLOSE" : $"{KeyName()}  HIDE";

        _scroll = Ui.Damp(_scroll, _scrollTarget, 16f);
        _content.anchoredPosition = new Vector2(0, _scroll);

        string? mine = VoteSession.MyVote;
        bool open = phase == Phase.Open;
        string? winner = RevealedWinner;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool isWinner = !open && winner != null && row.Choice == winner;
            row.Hover = Ui.Damp(row.Hover, open && i == _hovered ? 1f : 0f, 18f);
            row.Selected = Ui.Damp(row.Selected, mine == row.Choice ? 1f : 0f, 14f);
            row.Odds = Ui.Damp(row.Odds, row.OddsTarget, 8f);
            row.Pulse = Ui.Damp(row.Pulse, 0f, 9f);

            row.Group.alpha = open || isWinner ? 1f : 0.4f;
            row.Bg.color = Color.Lerp(Color.Lerp(Ui.Row, Ui.RowHover, row.Hover), Ui.Amber.WithAlpha(1f) * 0.28f + Ui.Row * 0.72f,
                isWinner ? 1f : 0f);
            row.Border.color = (isWinner ? Ui.Hot : Ui.Amber).WithAlpha(Mathf.Max(row.Selected * 0.95f, isWinner ? 1f : 0f));
            row.SelectBar.color = Ui.Amber.WithAlpha(row.Selected);
            row.Inner.anchoredPosition = new Vector2(row.Hover * 5f, 0);
            row.Inner.localScale = Vector3.one * (1f + row.Pulse * 0.025f);
            row.Count.color = row.VoteCount > 0 ? (mine == row.Choice ? Ui.Amber : Ui.Text) : Ui.Muted.WithAlpha(0.6f);
            row.OddsFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(row.Odds), 1f);
        }
    }

    private void AnimateChip()
    {
        var phase = VoteSession.Phase;
        bool needsMe = phase == Phase.Open && VoteSession.MyVote == null;
        bool show = !_boardOpen && (_reelStart < 0f || Time.unscaledTime - _reelStart > SpinSeconds + HoldSeconds);
        _chipT = Ui.Damp(_chipT, show ? 1f : 0f, 12f);
        _chipGroup.alpha = _chipT;
        _chip.anchoredPosition = ChipAnchor() + new Vector2((1f - _chipT) * 40f, 0);
        if (_chipT < 0.01f) return;

        _chipKey.text = KeyName();
        float left = VoteSession.SecondsLeft;
        string status = $"{VoteSession.Votes.Count}/{VoteSession.PlayerCount} VOTED{(left >= 0f ? $"  /  {Mathf.CeilToInt(left)}S" : "")}";
        string muted = $"<color=#{Ui.HexOf(Ui.Muted)}>";
        string amber = $"<color=#{Ui.HexOf(Ui.Amber)}>";
        switch (phase)
        {
            case Phase.Open:
                _chipTitle.text = needsMe ? "VOTE FOR THE NEXT MAP" : $"YOU PICKED {amber}{MapInfo.DisplayName(VoteSession.MyVote)}</color>";
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

    private void AnimateCursor()
    {
        // The game draws its menu cursor into its own render texture, underneath this overlay, so draw a matching
        // one on top: whenever the truck board is open, and in the lobby while the pointer is over the board.
        bool show = _boardT > 0.2f && (_context == VoteContext.Truck ? _boardOpen : Ui.Hovered(_board));
        if (_cursor.gameObject.activeSelf != show) _cursor.gameObject.SetActive(show);
        if (!show) return;
        _cursor.SetAsLastSibling();
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, Ui.Mouse, null, out var local))
            _cursor.anchoredPosition = local;
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
        if (_context == VoteContext.Truck) SetBoardOpen(false);

        // Fill the reel with names in proportion to their odds so it "feels" like the vote.
        var weighted = VoteSession.PoolNames.Where(n => odds.TryGetValue(n, out var o) && o > 0f).ToList();
        if (weighted.Count == 0) weighted = VoteSession.PoolNames;
        const int cellCount = 34;
        _reelWinner = cellCount - 4;
        foreach (var c in _cells) Destroy(c.gameObject);
        _cells.Clear();
        string? prev = null;
        for (int i = 0; i < cellCount; i++)
        {
            string name = i == _reelWinner ? winner : PickWeighted(weighted, odds, prev);
            prev = name;
            var cell = Ui.Label("Cell", _reel, 30f, Ui.Text, TextAlignmentOptions.Center);
            cell.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CellWidth - 24f, 60f));
            cell.text = MapInfo.DisplayName(name);
            cell.enableAutoSizing = true;
            cell.fontSizeMin = 18f;
            cell.fontSizeMax = 30f;
            _cells.Add(cell);
        }
        _revealTitle.text = "NEXT MAP";
        _revealTitle.color = Ui.Muted;
        Ui.Sound(m => m.MenuEffectPopUpOpen());
    }

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
        _revealGroup.alpha = Ui.Damp(_revealGroup.alpha, show ? 1f : 0f, show ? 14f : 6f);
        bool active = _revealGroup.alpha > 0.01f;
        if (_reveal.gameObject.activeSelf != active) _reveal.gameObject.SetActive(active);
        if (!active || _cells.Count == 0) return;

        float p = Mathf.Clamp01(elapsed / SpinSeconds);
        float eased = 1f - Mathf.Pow(1f - p, 4f);
        float offset = eased * _reelWinner * CellWidth;

        int centre = Mathf.RoundToInt(offset / CellWidth);
        if (centre != _lastTickCell && !_landed)
        {
            _lastTickCell = centre;
            Ui.Tick(0.85f + 0.35f * p);
        }

        if (p >= 1f && !_landed)
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
            if (cell.gameObject.activeSelf != near) cell.gameObject.SetActive(near);
            if (!near) continue;
            float d = Mathf.Clamp01(Mathf.Abs(x) / (CellWidth * 1.7f));
            cell.rectTransform.anchoredPosition = new Vector2(x, 0);
            cell.rectTransform.localScale = Vector3.one * (1f - 0.2f * d + (i == _reelWinner ? _punch * 0.12f : 0f));
            var colour = _landed && i == _reelWinner ? Ui.Amber : Ui.Text;
            cell.color = colour.WithAlpha(1f - d * 0.85f);
        }

        _punch = Ui.Damp(_punch, 0f, 6f);
        _window.localScale = Vector3.one * (1f + _punch * 0.05f);
        _revealCenter.color = Ui.Amber.WithAlpha(_landed ? 0.16f + _punch * 0.3f : 0.07f);
    }

    // ---- layout --------------------------------------------------------------------------

    private string KeyName()
    {
        var key = Plugin.ToggleKey.Value;
        var control = Keyboard.current?[key];
        return (control?.displayName is { Length: > 0 } d ? d : key.ToString()).ToUpperInvariant();
    }

    private Vector2 BoardAnchor() => _context == VoteContext.LobbyMenu ? new Vector2(-LobbyMargin, -LobbyTop) : Vector2.zero;
    private Vector2 ChipAnchor() => _context == VoteContext.LobbyMenu ? new Vector2(-LobbyMargin, -LobbyTop) : new Vector2(-36f, 36f);

    private float BoardScale() =>
        _context == VoteContext.LobbyMenu ? Mathf.Min(LobbyMaxScale, (LobbyBottom - LobbyTop) / _board.sizeDelta.y) : 1f;

    private void Place()
    {
        var boardAnchor = _context == VoteContext.LobbyMenu ? new Vector2(1f, 1f) : new Vector2(0.5f, 0.5f);
        _board.anchorMin = _board.anchorMax = _board.pivot = boardAnchor;
        var chipAnchor = _context == VoteContext.LobbyMenu ? new Vector2(1f, 1f) : new Vector2(1f, 0f);
        _chip.anchorMin = _chip.anchorMax = _chip.pivot = chipAnchor;

        // The lobby has no timer, so the vote count takes the ring's place.
        bool lobby = _context == VoteContext.LobbyMenu;
        _timerRoot.gameObject.SetActive(!lobby);
        _voted.rectTransform.anchoredPosition = new Vector2(lobby ? -28f : -106f, -20f);
        _votedCaption.anchoredPosition = new Vector2(lobby ? -30f : -108f, -66f);
    }

    private void Build()
    {
        var canvasGo = new GameObject("MapVotePlusPlusCanvas");
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 900;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 1f;
        _root = (RectTransform)canvasGo.transform;

        BuildBoard();
        BuildChip();
        BuildReveal();

        _cursor = Ui.Rect("Cursor", _root).Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CursorSize, CursorSize));
        _cursor.pivot = Ui.ArrowTip; // the tip sits on the mouse
        var cursorShadow = Ui.Image("Shadow", _cursor, Ui.Ink.WithAlpha(0.6f), Ui.Arrow).rectTransform.Fill();
        cursorShadow.anchoredPosition = new Vector2(2f, -3f);
        Ui.Image("Arrow", _cursor, Ui.Pointer, Ui.Arrow).rectTransform.Fill();
        _cursor.gameObject.SetActive(false);
        _canvas.enabled = false;
    }

    private static Image Shadow(Transform parent, float spread)
    {
        var shadow = Ui.Image("Shadow", parent, Color.black.WithAlpha(0.5f), Ui.Rounded);
        shadow.rectTransform.Fill(-spread, -spread * 0.6f, -spread, -spread * 1.4f);
        return shadow;
    }

    private void BuildBoard()
    {
        _board = Ui.Rect("Board", _root);
        _board.sizeDelta = new Vector2(BoardWidth, 600f);
        _boardGroup = _board.gameObject.AddComponent<CanvasGroup>();
        Shadow(_board, 22f);
        Ui.Image("Edge", _board, Ui.Faint, Ui.Rounded).rectTransform.Fill(-1, -1, -1, -1);
        Ui.Image("Bg", _board, Ui.Panel, Ui.Rounded).rectTransform.Fill();

        var title = Ui.Label("Title", _board, 46f, Ui.Amber, TextAlignmentOptions.TopLeft);
        title.rectTransform.Place(new Vector2(0, 1), new Vector2(28, -20), new Vector2(360, 52));
        title.text = "MAP VOTE++";
        title.characterSpacing = 6f;

        _subtitle = Ui.Label("Subtitle", _board, 16f, Ui.Muted, TextAlignmentOptions.TopLeft);
        _subtitle.rectTransform.Place(new Vector2(0, 1), new Vector2(30, -74), new Vector2(400, 22));
        _subtitle.characterSpacing = 2f;

        var ring = _timerRoot = Ui.Rect("Timer", _board).Place(new Vector2(1, 1), new Vector2(-26, -26), new Vector2(66, 66));
        Ui.Image("Track", ring, Ui.Faint, Ui.Ring).rectTransform.Fill();
        _timerRing = Ui.Image("Fill", ring, Ui.Amber, Ui.Ring);
        _timerRing.rectTransform.Fill();
        _timerRing.type = Image.Type.Filled;
        _timerRing.fillMethod = Image.FillMethod.Radial360;
        _timerRing.fillOrigin = (int)Image.Origin360.Top;
        _timerRing.fillClockwise = false;
        _timer = Ui.Label("Seconds", ring, 22f, Ui.Text, TextAlignmentOptions.Center);
        _timer.rectTransform.Fill();

        _voted = Ui.Label("Voted", _board, 36f, Ui.Text, TextAlignmentOptions.BottomRight);
        _voted.rectTransform.Place(new Vector2(1, 1), new Vector2(-106, -20), new Vector2(120, 44));
        var votedCaption = Ui.Label("VotedCaption", _board, 12f, Ui.Muted, TextAlignmentOptions.TopRight);
        _votedCaption = votedCaption.rectTransform.Place(new Vector2(1, 1), new Vector2(-108, -66), new Vector2(120, 16));
        votedCaption.text = "VOTED";
        votedCaption.characterSpacing = 4f;

        var hazard = Ui.Image("Hazard", _board, Ui.Amber.WithAlpha(0.85f), Ui.Hazard);
        hazard.type = Image.Type.Tiled;
        hazard.pixelsPerUnitMultiplier = 3f;
        var hz = hazard.rectTransform;
        hz.anchorMin = new Vector2(0, 1);
        hz.anchorMax = new Vector2(1, 1);
        hz.pivot = new Vector2(0.5f, 1);
        hz.offsetMin = new Vector2(Side, -HeaderHeight + 14f);
        hz.offsetMax = new Vector2(-Side, -HeaderHeight + 19f);

        _viewport = Ui.Rect("Viewport", _board).Fill(Side - 6f, HeaderHeight - 4f, Side - 6f, FooterHeight);
        _viewport.gameObject.AddComponent<RectMask2D>();
        _content = Ui.Rect("Content", _viewport);
        _content.anchorMin = new Vector2(0, 1);
        _content.anchorMax = new Vector2(1, 1);
        _content.pivot = new Vector2(0.5f, 1);
        _content.offsetMin = new Vector2(6f, 0);
        _content.offsetMax = new Vector2(-6f, 0);

        var line = Ui.Image("FooterLine", _board, Ui.Faint).rectTransform;
        line.anchorMin = Vector2.zero;
        line.anchorMax = new Vector2(1, 0);
        line.pivot = new Vector2(0.5f, 0);
        line.offsetMin = new Vector2(Side, FooterHeight);
        line.offsetMax = new Vector2(-Side, FooterHeight + 1f);

        _footer = Ui.Label("Footer", _board, 13f, Ui.Muted, TextAlignmentOptions.Center);
        _footer.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(BoardWidth - 40, 22));
        _footer.characterSpacing = 2f;
        _board.gameObject.SetActive(false);
    }

    private RowView BuildRow(string choice, Level? level, int index)
    {
        var row = new RowView { Choice = choice };
        row.Rect = Ui.Rect(choice, _content);
        row.Rect.anchorMin = new Vector2(0, 1);
        row.Rect.anchorMax = new Vector2(1, 1);
        row.Rect.pivot = new Vector2(0.5f, 1);
        row.Rect.sizeDelta = new Vector2(0, RowHeight);
        row.Rect.anchoredPosition = new Vector2(0, -index * (RowHeight + RowGap));
        row.Group = row.Rect.gameObject.AddComponent<CanvasGroup>();
        row.Inner = Ui.Rect("Inner", row.Rect).Fill();

        row.Border = Ui.Image("Border", row.Inner, Ui.Amber.WithAlpha(0f), Ui.Rounded);
        row.Border.rectTransform.Fill(-2, -2, -2, -2);
        row.Bg = Ui.Image("Bg", row.Inner, Ui.Row, Ui.Rounded);
        row.Bg.rectTransform.Fill();

        // Thumbnail: the level's own loading art, else its fog colour, else hazard stripes for Random.
        var info = level ? MapInfo.Get(level!) : null;
        row.Accent = info?.Color ?? Ui.Amber;
        var thumbRect = Ui.Rect("Thumb", row.Inner).Place(new Vector2(0, 0.5f), new Vector2(6, 0), new Vector2(ThumbWidth, ThumbHeight));
        var thumb = thumbRect.gameObject.AddComponent<RawImage>();
        thumb.raycastTarget = false;
        if (info?.Art)
        {
            thumb.texture = info!.Art;
            thumb.uvRect = MapInfo.Crop(info.ArtUv, info.Art!, ThumbWidth / ThumbHeight);
            thumb.color = Color.white;
        }
        else if (level)
        {
            thumb.color = Color.Lerp(Ui.Ink, row.Accent, 0.55f);
            var letter = Ui.Label("Initial", thumbRect, 34f, Ui.Ink.WithAlpha(0.75f), TextAlignmentOptions.Center);
            letter.rectTransform.Fill();
            letter.text = info!.Name.Length > 0 ? info.Name.Substring(0, 1) : "?";
        }
        else
        {
            thumb.texture = Ui.Hazard.texture;
            thumb.uvRect = new Rect(0, 0, ThumbWidth / 14f, ThumbHeight / 14f);
            thumb.color = Ui.Amber.WithAlpha(0.22f);
            var q = Ui.Label("Dice", thumbRect, 40f, Ui.Amber, TextAlignmentOptions.Center);
            q.rectTransform.Fill();
            q.text = "?";
        }
        var fade = Ui.Image("Fade", row.Inner, Ui.Row, Ui.FadeRight);
        fade.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(6 + ThumbWidth * 0.55f, 0), new Vector2(ThumbWidth * 0.45f + 1f, ThumbHeight));

        var badge = Ui.Image("Key", row.Inner, Ui.Ink.WithAlpha(0.88f), Ui.RoundedSmall);
        badge.rectTransform.Place(new Vector2(0, 1), new Vector2(11, -11), new Vector2(22, 22));
        var badgeText = Ui.Label("KeyText", badge.rectTransform, 13f, Ui.Amber, TextAlignmentOptions.Center);
        badgeText.rectTransform.Fill();
        badgeText.text = index == 0 ? "R" : index <= 9 ? index.ToString() : "";
        badge.gameObject.SetActive(badgeText.text.Length > 0);

        row.SelectBar = Ui.Image("Selected", row.Inner, Ui.Amber.WithAlpha(0f), Ui.RoundedSmall);
        row.SelectBar.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(-3, 0), new Vector2(5, RowHeight - 26));

        row.Name = Ui.Label("Name", row.Inner, 23f, Ui.Text, TextAlignmentOptions.BottomLeft);
        Span(row.Name.rectTransform, top: 10f, height: 28f);
        row.Name.text = level ? info!.Name : "RANDOM";
        row.Name.characterSpacing = 1.5f;

        row.Voters = Ui.Label("Voters", row.Inner, 14f, Ui.Muted, TextAlignmentOptions.TopLeft);
        Span(row.Voters.rectTransform, top: 40f, height: 18f);
        row.Voters.characterSpacing = 1f;

        row.Count = Ui.Label("Count", row.Inner, 30f, Ui.Muted, TextAlignmentOptions.BottomRight);
        row.Count.rectTransform.Place(new Vector2(1, 1), new Vector2(-18, -6), new Vector2(80, 36));
        row.Count.text = "0";
        row.Percent = Ui.Label("Percent", row.Inner, 13f, Ui.Muted, TextAlignmentOptions.TopRight);
        row.Percent.rectTransform.Place(new Vector2(1, 1), new Vector2(-18, -44), new Vector2(80, 16));

        var oddsTrack = Ui.Image("OddsTrack", row.Inner, Ui.Faint, Ui.RoundedSmall).rectTransform;
        oddsTrack.anchorMin = Vector2.zero;
        oddsTrack.anchorMax = new Vector2(1, 0);
        oddsTrack.pivot = Vector2.zero;
        oddsTrack.offsetMin = new Vector2(TextLeft, 9f);
        oddsTrack.offsetMax = new Vector2(-110f, 12f);
        row.OddsFill = Ui.Image("OddsFill", oddsTrack, row.Accent, Ui.RoundedSmall);
        var fillRect = row.OddsFill.rectTransform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = new Vector2(0, 1);
        fillRect.offsetMin = fillRect.offsetMax = Vector2.zero;
        return row;
    }

    /// <summary>Text column between the thumbnail and the vote count.</summary>
    private static void Span(RectTransform r, float top, float height)
    {
        r.anchorMin = new Vector2(0, 1);
        r.anchorMax = new Vector2(1, 1);
        r.pivot = new Vector2(0, 1);
        r.offsetMin = new Vector2(TextLeft, -top - height);
        r.offsetMax = new Vector2(-110f, -top);
    }

    private void BuildChip()
    {
        _chip = Ui.Rect("Chip", _root);
        _chip.sizeDelta = new Vector2(420, 70);
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
        _reveal = Ui.Rect("Reveal", _root).Place(new Vector2(0.5f, 1), new Vector2(0, -110), new Vector2(780, 160));
        _revealGroup = _reveal.gameObject.AddComponent<CanvasGroup>();
        _revealGroup.alpha = 0f;

        _revealTitle = Ui.Label("Title", _reveal, 18f, Ui.Muted, TextAlignmentOptions.Center);
        _revealTitle.rectTransform.Place(new Vector2(0.5f, 1), Vector2.zero, new Vector2(780, 24));
        _revealTitle.characterSpacing = 10f;

        _window = Ui.Rect("Window", _reveal).Place(new Vector2(0.5f, 1), new Vector2(0, -34), new Vector2(780, 104));
        Shadow(_window, 20f);
        Ui.Image("Frame", _window, Ui.Amber.WithAlpha(0.9f), Ui.Rounded).rectTransform.Fill(-2, -2, -2, -2);
        Ui.Image("Bg", _window, Ui.Panel, Ui.Rounded).rectTransform.Fill();
        _revealCenter = Ui.Image("Centre", _window, Ui.Amber.WithAlpha(0.07f), Ui.RoundedSmall);
        _revealCenter.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(CellWidth - 10f, 84f));

        var mask = Ui.Rect("Mask", _window).Fill(8, 8, 8, 8);
        mask.gameObject.AddComponent<RectMask2D>();
        _reel = Ui.Rect("Reel", mask).Place(new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

        // Edge fades so names slide in and out instead of being cut.
        var leftFade = Ui.Image("FadeL", _window, Ui.Panel, Ui.FadeRight).rectTransform;
        leftFade.Place(new Vector2(0, 0.5f), new Vector2(8, 0), new Vector2(150, 88));
        leftFade.localScale = new Vector3(-1, 1, 1);
        leftFade.pivot = new Vector2(1, 0.5f);
        var rightFade = Ui.Image("FadeR", _window, Ui.Panel, Ui.FadeRight).rectTransform;
        rightFade.Place(new Vector2(1, 0.5f), new Vector2(-8, 0), new Vector2(150, 88));

        foreach (var (anchor, y) in new[] { (new Vector2(0.5f, 1), 7f), (new Vector2(0.5f, 0), -7f) })
        {
            var marker = Ui.Image("Marker", _window, Ui.Amber, Ui.RoundedSmall).rectTransform;
            marker.anchorMin = marker.anchorMax = anchor;
            marker.pivot = new Vector2(0.5f, 0.5f);
            marker.anchoredPosition = new Vector2(0, y);
            marker.sizeDelta = new Vector2(16, 16);
            marker.localRotation = Quaternion.Euler(0, 0, 45);
        }
        _reveal.gameObject.SetActive(false);
    }
}
