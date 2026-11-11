using System.Collections.Generic;
using System.Linq;
using MenuLib;
using MenuLib.MonoBehaviors;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MapVote;

/// <summary>
/// The vote list, built from the game's own menu widgets (via MenuLib), so it is part of the game's menu: the same
/// font, scale and pixel look, drawn under the game's cursor, with the game's hover and click handling. It is docked
/// on the lobby page, and opens as a popup page in the truck, where the game then frees the cursor and holds the player.
/// </summary>
internal sealed class NativeBoard : MonoBehaviour
{
    // Lobby page space is the game's 720x405 menu UI with its origin bottom-left. The panel sits right of the lobby
    // panel, level with its top, and never runs below its bottom.
    private const float LobbyLeft = 380f, LobbyTop = 310f, LobbyBottom = 23f, LobbyWidth = 310f, LobbyHeader = 62f;
    private const float Pad = 10f, MaxPitch = 24f, MaxNameSize = 18f, ScrollBarWidth = 3f;
    /// <summary>Most rows the lobby panel shows at once: Random and the nine maps the number keys reach. More maps scroll.</summary>
    private const int LobbyRows = 10;
    /// <summary>Under the list, bottom-left of the right-hand popup: where REPOConfig puts its Save Changes button.</summary>
    private static readonly Vector2 SkipPosition = new(370f, 18f);
    /// <summary>The host's reminder, on the right of the status line while the vote is open. Players never see it.</summary>
    private const string HostHint = "SHIFT-CLICK TO PICK FOR EVERYONE";

    private sealed class Row
    {
        public string Choice = "";
        public RectTransform Rect = null!;
        public REPOButton Button = null!;
        public TextMeshProUGUI Voters = null!, Count = null!, Odds = null!;
    }

    private static readonly List<NativeBoard> Live = new();
    private static REPOPopupPage? _truckPage;
    private static bool _truckPrompt;

    private MenuPage? _page;
    private bool _lobby, _prompt;
    private RectTransform? _lobbyBody, _lobbyPanel, _scrollTrack, _scrollHandle;
    // The lobby list's window: the first row in view, how many rows fit, and their height.
    private int _first, _shown;
    private float _pitch;
    private int _lastHovered = -1;
    private string? _followedWinner;
    private REPOPopupPage? _popup;
    private float _rowWidth;
    private TMP_FontAsset? _font;
    private TextMeshProUGUI _status = null!, _hint = null!, _hostHint = null!;
    private REPOScrollViewElement? _hintElement;
    private readonly List<Row> _rows = new();
    private Color _normal = Color.gray;
    private int _seenRevision = -1;

    /// <summary>The board on the menu page that has focus, if any: where the quick-vote keys go.</summary>
    public static NativeBoard? Active
    {
        get
        {
            var current = MenuManager.instance ? MenuManager.instance.currentMenuPage : null;
            return current ? Live.FirstOrDefault(b => b._page == current) : null;
        }
    }

    private void Awake() => Live.Add(this);
    private void OnDestroy() => Live.Remove(this);

    // ---- lobby ---------------------------------------------------------------------------

    /// <summary>
    /// MenuLib calls this every time the lobby page is built. The panel is built even with the lobby vote switched off
    /// and shown to match the setting, so switching it in game (REPOConfig) applies without rebuilding the page.
    /// </summary>
    public static void BuildLobby(Transform page)
    {
        // The board lives on the root, which stays active so it can show and hide the body. The body spans all the
        // room the panel may use and never resizes: its children keep a new rect's centre anchors, so a resize would
        // shift the header. The backdrop is sized to the rows in view.
        var root = Ui.Rect("MapVotePlusPlus", page);
        root.pivot = new Vector2(0f, 1f);
        root.localPosition = new Vector2(LobbyLeft, LobbyTop);
        root.sizeDelta = new Vector2(LobbyWidth, LobbyTop - LobbyBottom);
        var body = Ui.Rect("Body", root);
        body.pivot = new Vector2(0f, 1f);
        body.localPosition = Vector2.zero;
        body.sizeDelta = root.sizeDelta;
        var panel = Ui.Image("Panel", body, Color.black.WithAlpha(0.62f)).rectTransform;
        panel.pivot = new Vector2(0f, 1f);
        panel.localPosition = Vector2.zero;

        var board = root.gameObject.AddComponent<NativeBoard>();
        board._page = page.GetComponent<MenuPage>();
        board._lobby = true;
        board._lobbyBody = body;
        board._lobbyPanel = panel;
        board._rowWidth = LobbyWidth - 2 * Pad;
        board._font = MenuFont(page);

        float inner = board._rowWidth;
        board.Label("Title", body, 24f, Ui.Amber, new Vector2(Pad, -Pad - 28f), new Vector2(inner, 28f)).text = "MAP VOTE";
        board._hint = board.Label("Hint", body, 11f, Ui.Muted, new Vector2(Pad, -Pad - 25f), new Vector2(inner, 16f),
            TextAlignmentOptions.MidlineRight);
        board._status = board.Label("Status", body, 11f, Ui.Muted, new Vector2(Pad, -Pad - 46f), new Vector2(inner, 16f));
        board._hostHint = board.Label("HostHint", body, 11f, Ui.Muted, new Vector2(Pad, -Pad - 46f), new Vector2(inner, 16f),
            TextAlignmentOptions.MidlineRight);
        var line = Ui.Image("Line", body, Ui.Amber.WithAlpha(0.8f)).rectTransform;
        line.pivot = Vector2.zero;
        line.localPosition = new Vector2(Pad, -LobbyHeader + 3f);
        line.sizeDelta = new Vector2(inner, 1.5f);

        // In the panel's right margin, beside the rows. Shown only when there are more maps than fit.
        var track = Ui.Image("ScrollBar", body, Ui.Muted.WithAlpha(0.25f)).rectTransform;
        track.pivot = new Vector2(0f, 1f);
        track.localPosition = new Vector2(LobbyWidth - (Pad + ScrollBarWidth) / 2f, -LobbyHeader);
        var handle = Ui.Image("Handle", track, Ui.Amber.WithAlpha(0.8f)).rectTransform;
        handle.pivot = new Vector2(0f, 1f);
        track.gameObject.SetActive(false);
        board._scrollTrack = track;
        board._scrollHandle = handle;

        // Build the rows first: game buttons set themselves up in Awake, which doesn't run under an inactive parent.
        board.Refresh();
        body.gameObject.SetActive(Plugin.VoteInLobbyMenu.Value);
    }

    // ---- truck ---------------------------------------------------------------------------

    public static bool TruckPageOpen => IsOpen(_truckPage);

    /// <summary>The truck vote opened by itself on arrival and is still up.</summary>
    public static bool PromptOpen => _truckPrompt && TruckPageOpen;

    public static void ToggleTruckPage()
    {
        if (TruckPageOpen) CloseTruckPage();
        else OpenTruckPage(prompt: false);
    }

    /// <summary>
    /// Opens the truck vote as a popup page. As the arrival <paramref name="prompt"/> it has a Skip button and closes
    /// once this player picks a map; opened with the toggle key it stays up until they close it. Returns false when
    /// it can't open now: it is already open, or another menu has the screen.
    /// </summary>
    public static bool OpenTruckPage(bool prompt)
    {
        if (TruckPageOpen || !MenuManager.instance || MenuManager.instance.currentMenuPage) return false; // the escape menu has the screen

        var page = MenuAPI.CreateREPOPopupPage("MAP VOTE", REPOPopupPage.PresetSide.Right, shouldCachePage: false, pageDimmerVisibility: true);
        var board = page.gameObject.AddComponent<NativeBoard>();
        board._page = page.menuPage;
        board._popup = page;
        board._prompt = prompt;
        board._rowWidth = page.maskRectTransform.sizeDelta.x - Pad;
        board._font = MenuFont(page.transform);

        // The page's header belongs to MenuLib, so the hint gets its own line under it, shown only for a shortlist.
        page.AddElementToScrollView(scroller =>
        {
            board._hint = board.Label("Hint", scroller, 12f, Ui.Muted, Vector2.zero, new Vector2(board._rowWidth, 18f));
            return board._hint.rectTransform;
        });
        board._hintElement = board._hint.GetComponent<REPOScrollViewElement>();
        page.AddElementToScrollView(scroller =>
        {
            board._status = board.Label("Status", scroller, 12f, Ui.Muted, Vector2.zero, new Vector2(board._rowWidth, 20f));
            // Over the status line, right-aligned, so it needs no line of its own.
            board._hostHint = board.Label("HostHint", board._status.rectTransform, 12f, Ui.Muted, Vector2.zero,
                new Vector2(board._rowWidth, 20f), TextAlignmentOptions.MidlineRight);
            return board._status.rectTransform;
        }, bottomPadding: 4f);
        board.SyncRows();
        // Closing the prompt any other way (Esc, the toggle key) counts as a skip too.
        if (prompt) page.AddElement(parent => MenuAPI.CreateREPOButton("SKIP", CloseTruckPage, parent, SkipPosition));

        page.OpenPage(openOnTop: false);
        board.Refresh();
        _truckPage = page;
        _truckPrompt = prompt;
        return true;
    }

    public static void CloseTruckPage()
    {
        if (TruckPageOpen) _truckPage!.ClosePage(closePagesAddedOnTop: true);
        _truckPage = null;
        _truckPrompt = false;
    }

    private static bool IsOpen(REPOPopupPage? page) =>
        page && page!.menuPage.currentPageState is not (MenuPage.PageState.Closing or MenuPage.PageState.Closed);

    // ---- rows ----------------------------------------------------------------------------

    /// <summary>Random, then the maps up for this vote in pool order. A map this player doesn't have is left out.</summary>
    private static List<(string Choice, Level? Level)> Options()
    {
        var pool = VoteSession.Pool;
        var maps = VoteSession.Candidates.Select(name => pool.FirstOrDefault(l => l.name == name)).Where(l => l);
        return new[] { (VoteLogic.RandomChoice, (Level?)null) }.Concat(maps.Select(l => (l.name, (Level?)l))).ToList();
    }

    /// <summary>
    /// Builds a row per option, and builds them again when the host draws a different set of maps: a player can open
    /// the list before the host's vote reaches them, and each vote can draw new maps.
    /// </summary>
    private void SyncRows()
    {
        var options = Options();
        if (options.Select(o => o.Choice).SequenceEqual(_rows.Select(r => r.Choice))) return;

        foreach (var row in _rows)
        {
            // Out from under the page now: Destroy waits for the end of the frame, and the scroll view lays out and
            // the menu tracks every element that is still there until then.
            row.Rect.gameObject.SetActive(false);
            row.Rect.SetParent(null, false);
            Destroy(row.Rect.gameObject);
        }
        _rows.Clear();

        if (_lobby)
        {
            // Up to LobbyRows rows share the panel's height, none taller than MaxPitch. A longer list (a custom map
            // pack) keeps that smallest height and scrolls, rather than shrinking its names until they can't be read.
            _shown = Mathf.Min(options.Count, LobbyRows);
            _pitch = Mathf.Min(MaxPitch, (LobbyTop - LobbyBottom - LobbyHeader - Pad) / _shown);
            _lobbyPanel!.sizeDelta = new Vector2(LobbyWidth, LobbyHeader + _shown * _pitch + Pad);
            foreach (var (choice, level) in options) BuildRow(_lobbyBody!, choice, level, Vector2.zero, _rowWidth, _pitch);
            _first = 0;
            _lastHovered = -1;
            _followedWinner = null;
            PlaceRows();
        }
        else
        {
            // The scroll view stacks the rows (it sets y); x = 0 is its left edge.
            foreach (var (choice, level) in options)
                _popup!.AddElementToScrollView(scroller => BuildRow(scroller, choice, level, Vector2.zero, _rowWidth, MaxPitch));
            _popup!.scrollView.UpdateElements();
        }
    }

    /// <summary>A map's colour tab and thumbnail, its name as a game menu button, who voted for it and its chance of winning.</summary>
    private RectTransform BuildRow(Transform parent, string choice, Level? level, Vector2 position, float width, float pitch)
    {
        var rect = Ui.Rect(choice, parent);
        rect.pivot = Vector2.zero;
        // Always place it: a new rect keeps its centre anchors, so left alone the row would start halfway across its parent.
        rect.localPosition = position;
        rect.sizeDelta = new Vector2(width, pitch);

        var info = level ? MapInfo.Get(level!) : null;
        var tab = Ui.Image("Tab", rect, info?.Color ?? Ui.Amber).rectTransform;
        tab.pivot = Vector2.zero;
        tab.localPosition = new Vector2(0f, pitch * 0.2f);
        tab.sizeDelta = new Vector2(3f, pitch * 0.6f);

        float thumbHeight = pitch - 4f, thumbWidth = thumbHeight * 1.8f;
        Thumbnail(rect, info, new Vector2(6f, 2f), new Vector2(thumbWidth, thumbHeight));

        float size = Mathf.Min(MaxNameSize, pitch * 0.75f), nameX = thumbWidth + 12f, votersX = width * 0.6f;
        var button = MenuAPI.CreateREPOButton(choice, () => Pick(choice), rect);
        var name = button.labelTMP;
        name.fontSize = size;
        name.text = Fit(name, MapInfo.DisplayName(choice), votersX - nameX - 4f);
        // Game buttons size to their text from the bottom-left, so centre the text's height in the row.
        button.rectTransform.localPosition = new Vector2(nameX, Mathf.Max(0f, (pitch - name.GetPreferredValues("Ag").y) / 2f));
        _normal = button.menuButton.colorNormal;

        // From the right: the chance of winning (room for "100%"), the vote count (two digits), then who voted.
        float oddsSize = size * 0.7f, oddsWidth = oddsSize * 2.2f, countWidth = size * 1.2f;
        float countX = width - oddsWidth - 3f - countWidth;
        var voters = Label("Voters", rect, size * 0.62f, Ui.Muted, new Vector2(votersX, 0f), new Vector2(countX - 4f - votersX, pitch),
            TextAlignmentOptions.MidlineRight);
        var count = Label("Count", rect, size, Ui.Text, new Vector2(countX, 0f), new Vector2(countWidth, pitch), TextAlignmentOptions.MidlineRight);
        var odds = Label("Odds", rect, oddsSize, Ui.Muted, new Vector2(width - oddsWidth, 0f), new Vector2(oddsWidth, pitch),
            TextAlignmentOptions.MidlineRight);
        _rows.Add(new Row { Choice = choice, Rect = rect, Button = button, Voters = voters, Count = count, Odds = odds });
        return rect;
    }

    /// <summary>The level's loading-screen art; its initial on its colour when it has none; a "?" for Random.</summary>
    private void Thumbnail(RectTransform row, MapInfo? info, Vector2 position, Vector2 size)
    {
        var rect = Ui.Rect("Thumb", row);
        rect.pivot = Vector2.zero;
        rect.localPosition = position;
        rect.sizeDelta = size;
        var image = rect.gameObject.AddComponent<RawImage>();
        image.raycastTarget = false;
        if (info?.Art)
        {
            image.texture = info!.Art;
            image.uvRect = MapInfo.Crop(info.ArtUv, info.Art!, size.x / size.y);
            return;
        }

        image.color = info != null ? Color.Lerp(Ui.Ink, info.Color, 0.55f) : Ui.Amber.WithAlpha(0.22f);
        var mark = Ui.Label("Mark", rect, size.y * 0.8f, info != null ? Ui.Ink.WithAlpha(0.75f) : Ui.Amber,
            TextAlignmentOptions.Center, FontStyles.Normal, _font);
        mark.rectTransform.Fill();
        mark.text = info == null ? "?" : info.Name.Length > 0 ? info.Name.Substring(0, 1) : "?";
    }

    private void Pick(string choice)
    {
        if (VoteSession.Phase != Phase.Open) return;
        // Shift on a click or a number key: the host picks for everyone, and the reveal plays now. Anyone else just votes.
        if (VoteSession.IsHost && Ui.ShiftHeld)
        {
            VoteSession.PickForEveryone(choice);
            return;
        }
        VoteSession.Cast(choice);
        // The arrival prompt has done its job once this player has a pick, so let them get on with the truck.
        if (_prompt && VoteSession.MyVote != null) CloseTruckPage();
    }

    /// <summary>1-9 / R from the keyboard, with the click the button would have made.</summary>
    public void QuickVote(int index)
    {
        if (index >= _rows.Count) return;
        if (VoteSession.Phase != Phase.Open || HostCheck.Support != HostSupport.Ok)
        {
            Ui.Click(MenuManager.MenuClickEffectType.Dud);
            return;
        }
        Pick(_rows[index].Choice);
        if (_lobby) ShowRow(index);
        Ui.Click(MenuManager.MenuClickEffectType.Confirm);
    }

    // ---- lobby scrolling -----------------------------------------------------------------

    /// <summary>
    /// Puts the rows in view in their slots and switches the rest off, so a row out of view can't be hovered, clicked
    /// or reached with the keyboard. Only called when the window moves or the rows are built.
    /// </summary>
    private void PlaceRows()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            bool inView = i >= _first && i < _first + _shown;
            var row = _rows[i].Rect;
            if (row.gameObject.activeSelf != inView) row.gameObject.SetActive(inView);
            if (inView) row.localPosition = new Vector2(Pad, -LobbyHeader - (i - _first + 1) * _pitch);
        }

        bool scrolls = _rows.Count > _shown;
        _scrollTrack!.gameObject.SetActive(scrolls);
        if (!scrolls) return;
        float height = _shown * _pitch;
        _scrollTrack.sizeDelta = new Vector2(ScrollBarWidth, height);
        _scrollHandle!.sizeDelta = new Vector2(ScrollBarWidth, height * _shown / _rows.Count);
        _scrollHandle.localPosition = new Vector2(0f, -height * _first / _rows.Count);
    }

    private void ScrollTo(int first)
    {
        first = Mathf.Clamp(first, 0, Mathf.Max(0, _rows.Count - _shown));
        if (first == _first) return;
        _first = first;
        PlaceRows();
    }

    /// <summary>Scrolls the least distance that brings a row into view.</summary>
    private void ShowRow(int index)
    {
        if (index < 0) return;
        if (index < _first) ScrollTo(index);
        else if (index >= _first + _shown) ScrollTo(index - _shown + 1);
    }

    /// <summary>
    /// The mouse wheel over the panel moves the list a row a notch, as the game's own scroll boxes do. A row picked
    /// with the keyboard or a gamepad at either end of the window scrolls the next one into view, since the game's
    /// navigation can't reach a row that is switched off. Each frame this only reads the wheel and the rows' hover
    /// flags: the game's cursor check, which allocates, runs only on a wheel notch or when the hovered row changes.
    /// </summary>
    private void ScrollLobby()
    {
        var menu = MenuManager.instance;
        if (_rows.Count <= _shown || !menu || menu.currentMenuPage != _page) return;

        float wheel = SemiFunc.InputScrollY();
        if (wheel != 0f && MouseOver(_lobbyPanel!)) ScrollTo(_first + (wheel > 0f ? -1 : 1));

        int hovered = -1;
        for (int i = _first; i < _first + _shown; i++)
        {
            if (!_rows[i].Button.menuButton.hovering) continue;
            hovered = i;
            break;
        }
        if (hovered == _lastHovered) return;
        _lastHovered = hovered;
        bool atTop = hovered > 0 && hovered == _first;
        bool atBottom = hovered >= 0 && hovered < _rows.Count - 1 && hovered == _first + _shown - 1;
        // The cursor hovers rows too. Scrolling under it would hover the next row, and that one the next.
        if ((atTop || atBottom) && !MouseOver(_rows[hovered].Rect)) ScrollTo(_first + (atTop ? -1 : 1));
    }

    private bool MouseOver(RectTransform rect) => SemiFunc.UIMouseHover(_page, rect, null);

    // ---- state ---------------------------------------------------------------------------

    private void Update()
    {
        if (_lobby)
        {
            bool show = Plugin.VoteInLobbyMenu.Value;
            if (_lobbyBody!.gameObject.activeSelf != show) _lobbyBody.gameObject.SetActive(show);
            if (!show) return;
        }

        // Esc closes the truck vote; don't let the game also open its escape menu on the same press.
        if (!_lobby && GameDirector.instance) GameDirector.instance.disableEscMenuTimer = 0.2f;
        Refresh();
        if (_lobby) ScrollLobby();
    }

    private void Refresh()
    {
        bool changed = VoteSession.Revision != _seenRevision;
        if (changed) SyncRows();

        var phase = VoteSession.Phase;
        // The host can't run a vote with this player (no MapVote++, a version that can't talk to ours, the vote off):
        // say so in place of the tally, and grey out every map, rather than leave a vote up that goes nowhere.
        var problem = HostCheck.Problem;
        bool open = phase == Phase.Open && problem == null;
        string? winner = VoteUi.Instance ? VoteUi.Instance!.RevealedWinner : VoteSession.Winner;
        string? mine = VoteSession.MyVote;
        _status.text = problem?.Status ?? Status(phase, winner);
        // Singleplayer has nobody else to pick for: a plain click already wins.
        _hostHint.text = open && VoteSession.IsHost && SemiFunc.IsMultiplayer() ? HostHint : "";
        if (_lobby && winner != _followedWinner)
        {
            // Bring the winner into view when the roll lands.
            _followedWinner = winner;
            if (winner != null) ShowRow(_rows.FindIndex(r => r.Choice == winner));
        }

        // MenuButton paints its text with colorNormal whenever it isn't hovered or pressed.
        foreach (var row in _rows)
        {
            bool picked = problem == null && (open ? row.Choice == mine : row.Choice == winner);
            bool faded = problem != null || (!open && winner != null);
            row.Button.menuButton.colorNormal = picked ? Ui.Amber : faded ? _normal.WithAlpha(0.45f) : _normal;
        }

        if (!changed) return;
        _seenRevision = VoteSession.Revision;

        // Without this, a short list looks like maps have gone missing, and Random looks like it covers every map.
        string hint = problem?.Hint ?? (VoteSession.Shortlisted
            ? $"{VoteSession.Candidates.Count} OF {VoteSession.MapsAllowed} MAPS, DRAWN AT RANDOM"
            : "");
        _hint.text = hint;
        if (_hintElement) _hintElement!.visibility = hint.Length > 0;

        // The maps' chances, under the host's VoteMode, show how far ahead the leader really is. Random's own row stays
        // blank: its share is already spread across the maps' chances, and showing it too would push the column past
        // 100%. Once the host has picked, the votes no longer decide, so the column goes blank. It doesn't show the host's
        // map at 100%, which would give away the reveal.
        var odds = problem == null && VoteSession.HostPick == null ? CurrentOdds() : null;
        foreach (var row in _rows)
        {
            var voters = VoteSession.Votes.Where(v => v.Value == row.Choice).Select(v => v.Key).OrderBy(a => a).ToList();
            row.Count.text = voters.Count > 0 ? voters.Count.ToString() : "";
            row.Voters.text = string.Join(" ",
                voters.Select(a => $"<color=#{Ui.HexOf(Ui.PlayerColor(a))}>{VoteSession.PlayerName(a).ToUpperInvariant()}</color>"));
            row.Odds.text = odds != null && row.Choice != VoteLogic.RandomChoice && odds.TryGetValue(row.Choice, out var chance)
                ? Percent(chance)
                : "";
        }
    }

    /// <summary>
    /// Each map's chance of winning as the votes stand, or null until a vote counts: with none, the roll is an even
    /// draw over every map, and the same share on every row tells players nothing. Called only when the vote changes.
    /// <see cref="VoteSession.Odds"/> applies the host's rules, so these match the reveal and the host's draw.
    /// </summary>
    private static Dictionary<string, float>? CurrentOdds()
    {
        if (VoteLogic.ValidBallots(VoteSession.Candidates, VoteSession.Votes.Values).Count == 0) return null;
        return VoteSession.Odds();
    }

    /// <summary>
    /// A chance as a whole percentage, blank at zero. A map that can still win never shows 0%, and one that can still
    /// lose never shows 100%.
    /// </summary>
    private static string Percent(float chance)
    {
        if (chance <= 0f) return "";
        int percent = Mathf.RoundToInt(chance * 100f);
        if (percent < 1) return "<1%";
        // Tickets are added up in floats, so a sure thing can come out a hair under 1.
        if (percent >= 100) return chance > 0.9999f ? "100%" : "99%";
        return $"{percent}%";
    }

    private string Status(Phase phase, string? winner)
    {
        string tally = $"{VoteSession.Votes.Count}/{VoteSession.PlayerCount} VOTED";
        return phase switch
        {
            Phase.Open when _lobby => $"{tally}   ROLLS WHEN THE HOST STARTS",
            Phase.Open => $"{tally}   ROLLS ON DEPARTURE",
            _ when winner != null =>
                $"{(VoteSession.HostPickedMap ? "HOST'S PICK" : "NEXT MAP")}  <color=#{Ui.HexOf(Ui.Amber)}>{MapInfo.DisplayName(winner)}</color>",
            Phase.Revealing => "ROLLING...",
            _ => "WAITING FOR HOST",
        };
    }

    // ---- helpers -------------------------------------------------------------------------

    /// <summary>The font the game's menu buttons use, so our labels match them.</summary>
    private static TMP_FontAsset? MenuFont(Transform page)
    {
        var button = page.GetComponentInChildren<MenuButton>(true);
        var text = button ? button!.GetComponentInChildren<TextMeshProUGUI>(true) : page.GetComponentInChildren<TextMeshProUGUI>(true);
        return text ? text!.font : null;
    }

    private TextMeshProUGUI Label(string name, Transform parent, float size, Color color, Vector2 position, Vector2 box,
        TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft)
    {
        var tmp = Ui.Label(name, parent, size, color, align, FontStyles.Normal, _font);
        var rect = tmp.rectTransform;
        rect.pivot = Vector2.zero;
        rect.localPosition = position;
        rect.sizeDelta = box;
        return tmp;
    }

    /// <summary>Game buttons grow to fit their text, so trim long (custom) map names to keep the columns apart.</summary>
    private static string Fit(TMP_Text label, string text, float maxWidth)
    {
        if (label.GetPreferredValues(text).x <= maxWidth) return text;
        for (int n = text.Length - 1; n > 1; n--)
        {
            var cut = text.Substring(0, n).TrimEnd() + "...";
            if (label.GetPreferredValues(cut).x <= maxWidth) return cut;
        }
        return text;
    }
}
