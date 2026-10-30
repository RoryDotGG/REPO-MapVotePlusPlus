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
///  - Reveal: a slot-machine reel that lands on the winner for everyone at the same time.
/// Also routes the keyboard: the toggle key opens the truck vote, and 1-9 / R vote on whichever board has focus.
/// </summary>
internal sealed class VoteUi : MonoBehaviour
{
    private const float CellWidth = 250f, SpinSeconds = 3.3f, HoldSeconds = 2.8f;

    public static VoteUi? Instance { get; private set; }

    private RectTransform _root = null!;
    private Canvas _canvas = null!;

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

    private VoteContext _context, _lastContext;
    private int _seenRevision = -1;
    private float _fontRetry;

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

        bool visible = _context != VoteContext.None && Ui.FontReady && !GameMenuBlocking();
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
    }

    private void OnStateChanged()
    {
        _seenRevision = VoteSession.Revision;
        if (VoteSession.Phase == Phase.Revealing && VoteSession.Winner != null && _reelFor != VoteSession.Winner)
            StartReel(VoteSession.Winner, VoteLogic.Odds(VoteSession.PoolNames, VoteSession.Votes.Values, VoteSession.PreviousLevel));
    }

    private void HandleInput()
    {
        if (_context == VoteContext.None) return;
        bool typing = !SemiFunc.NoTextInputsActive() || (ChatManager.instance && ChatManager.instance.chatActive);
        if (typing) return;

        if (_context == VoteContext.Truck && Ui.KeyDown(Plugin.ToggleKey.Value)) NativeBoard.ToggleTruckPage();

        var board = NativeBoard.Active;
        if (!board) return;
        if (Ui.KeyDown(Key.R)) board!.QuickVote(0);
        for (int i = 0; i < 9; i++)
            if (Ui.KeyDown(Key.Digit1 + i) || Ui.KeyDown(Key.Numpad1 + i)) board!.QuickVote(i + 1);
    }

    // ---- chip ----------------------------------------------------------------------------

    private void AnimateChip()
    {
        var phase = VoteSession.Phase;
        bool needsMe = phase == Phase.Open && VoteSession.MyVote == null;
        // Truck only: in the lobby the vote list is always on screen.
        bool show = _context == VoteContext.Truck && (_reelStart < 0f || Time.unscaledTime - _reelStart > SpinSeconds + HoldSeconds);
        _chipT = Ui.Damp(_chipT, show ? 1f : 0f, 12f);
        _chipGroup.alpha = _chipT;
        _chip.anchoredPosition = new Vector2(-36f + (1f - _chipT) * 40f, 36f);
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
