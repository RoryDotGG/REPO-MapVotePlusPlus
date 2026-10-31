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
    private const float Pad = 10f, MaxPitch = 24f, MaxNameSize = 18f;

    private sealed class Row
    {
        public string Choice = "";
        public REPOButton Button = null!;
        public TextMeshProUGUI Voters = null!, Count = null!;
    }

    private static readonly List<NativeBoard> Live = new();
    private static REPOPopupPage? _truckPage;

    private MenuPage? _page;
    private bool _lobby;
    private TMP_FontAsset? _font;
    private TextMeshProUGUI _status = null!;
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

    /// <summary>MenuLib calls this every time the lobby page is built.</summary>
    public static void BuildLobby(Transform page)
    {
        if (!Plugin.VoteInLobbyMenu.Value) return;

        var options = Options();
        float pitch = Mathf.Min(MaxPitch, (LobbyTop - LobbyBottom - LobbyHeader - Pad) / options.Count);
        var root = Ui.Rect("MapVotePlusPlus", page);
        root.pivot = new Vector2(0f, 1f);
        root.localPosition = new Vector2(LobbyLeft, LobbyTop);
        root.sizeDelta = new Vector2(LobbyWidth, LobbyHeader + options.Count * pitch + Pad);
        Ui.Image("Panel", root, Color.black.WithAlpha(0.62f)).rectTransform.Fill();

        var board = root.gameObject.AddComponent<NativeBoard>();
        board._page = page.GetComponent<MenuPage>();
        board._lobby = true;
        board._font = MenuFont(page);

        float inner = LobbyWidth - 2 * Pad;
        board.Label("Title", root, 24f, Ui.Amber, new Vector2(Pad, -Pad - 28f), new Vector2(inner, 28f)).text = "MAP VOTE";
        board._status = board.Label("Status", root, 11f, Ui.Muted, new Vector2(Pad, -Pad - 46f), new Vector2(inner, 16f));
        var line = Ui.Image("Line", root, Ui.Amber.WithAlpha(0.8f)).rectTransform;
        line.pivot = Vector2.zero;
        line.localPosition = new Vector2(Pad, -LobbyHeader + 3f);
        line.sizeDelta = new Vector2(inner, 1.5f);

        float y = -LobbyHeader;
        foreach (var (choice, level) in options)
        {
            y -= pitch;
            board.BuildRow(root, choice, level, new Vector2(Pad, y), inner, pitch);
        }
        board.Refresh();
    }

    // ---- truck ---------------------------------------------------------------------------

    public static void ToggleTruckPage()
    {
        if (IsOpen(_truckPage))
        {
            CloseTruckPage();
            return;
        }
        if (!MenuManager.instance || MenuManager.instance.currentMenuPage) return; // the escape menu has the screen

        var page = MenuAPI.CreateREPOPopupPage("MAP VOTE", REPOPopupPage.PresetSide.Right, shouldCachePage: false, pageDimmerVisibility: true);
        var board = page.gameObject.AddComponent<NativeBoard>();
        board._page = page.menuPage;
        board._font = MenuFont(page.transform);

        float width = page.maskRectTransform.sizeDelta.x - Pad;
        page.AddElementToScrollView(scroller =>
        {
            board._status = board.Label("Status", scroller, 12f, Ui.Muted, Vector2.zero, new Vector2(width, 20f));
            return board._status.rectTransform;
        }, bottomPadding: 4f);
        // The scroll view stacks the rows (it sets y); x = 0 is its left edge.
        foreach (var (choice, level) in Options())
            page.AddElementToScrollView(scroller => board.BuildRow(scroller, choice, level, Vector2.zero, width, MaxPitch));

        page.OpenPage(openOnTop: false);
        board.Refresh();
        _truckPage = page;
    }

    public static void CloseTruckPage()
    {
        if (IsOpen(_truckPage)) _truckPage!.ClosePage(closePagesAddedOnTop: true);
        _truckPage = null;
    }

    private static bool IsOpen(REPOPopupPage? page) =>
        page && page!.menuPage.currentPageState is not (MenuPage.PageState.Closing or MenuPage.PageState.Closed);

    // ---- rows ----------------------------------------------------------------------------

    private static List<(string Choice, Level? Level)> Options() =>
        new[] { (VoteLogic.RandomChoice, (Level?)null) }.Concat(VoteSession.Pool.Select(l => (l.name, (Level?)l))).ToList();

    /// <summary>A map's colour tab and thumbnail, its name as a game menu button, and who voted for it.</summary>
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

        float size = Mathf.Min(MaxNameSize, pitch * 0.75f), nameX = thumbWidth + 12f, votersX = width * 0.62f;
        var button = MenuAPI.CreateREPOButton(choice, () => Vote(choice), rect);
        var name = button.labelTMP;
        name.fontSize = size;
        name.text = Fit(name, MapInfo.DisplayName(choice), votersX - nameX - 4f);
        // Game buttons size to their text from the bottom-left, so centre the text's height in the row.
        button.rectTransform.localPosition = new Vector2(nameX, Mathf.Max(0f, (pitch - name.GetPreferredValues("Ag").y) / 2f));
        _normal = button.menuButton.colorNormal;

        var voters = Label("Voters", rect, size * 0.62f, Ui.Muted, new Vector2(votersX, 0f), new Vector2(width - votersX - 34f, pitch),
            TextAlignmentOptions.MidlineRight);
        var count = Label("Count", rect, size, Ui.Text, new Vector2(width - 28f, 0f), new Vector2(28f, pitch), TextAlignmentOptions.MidlineRight);
        _rows.Add(new Row { Choice = choice, Button = button, Voters = voters, Count = count });
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

    private static void Vote(string choice)
    {
        if (VoteSession.Phase == Phase.Open) VoteSession.Cast(choice);
    }

    /// <summary>1-9 / R from the keyboard, with the click the button would have made.</summary>
    public void QuickVote(int index)
    {
        if (index >= _rows.Count) return;
        if (VoteSession.Phase != Phase.Open)
        {
            Ui.Click(MenuManager.MenuClickEffectType.Dud);
            return;
        }
        VoteSession.Cast(_rows[index].Choice);
        Ui.Click(MenuManager.MenuClickEffectType.Confirm);
    }

    // ---- state ---------------------------------------------------------------------------

    private void Update()
    {
        // Esc closes the truck vote; don't let the game also open its escape menu on the same press.
        if (!_lobby && GameDirector.instance) GameDirector.instance.disableEscMenuTimer = 0.2f;
        Refresh();
    }

    private void Refresh()
    {
        var phase = VoteSession.Phase;
        bool open = phase == Phase.Open;
        string? winner = VoteUi.Instance ? VoteUi.Instance!.RevealedWinner : VoteSession.Winner;
        string? mine = VoteSession.MyVote;
        _status.text = Status(phase, winner);

        // MenuButton paints its text with colorNormal whenever it isn't hovered or pressed.
        foreach (var row in _rows)
        {
            bool picked = open ? row.Choice == mine : row.Choice == winner;
            row.Button.menuButton.colorNormal = picked ? Ui.Amber : !open && winner != null ? _normal.WithAlpha(0.45f) : _normal;
        }

        if (VoteSession.Revision == _seenRevision) return;
        _seenRevision = VoteSession.Revision;
        foreach (var row in _rows)
        {
            var voters = VoteSession.Votes.Where(v => v.Value == row.Choice).Select(v => v.Key).OrderBy(a => a).ToList();
            row.Count.text = voters.Count > 0 ? voters.Count.ToString() : "";
            row.Voters.text = string.Join(" ",
                voters.Select(a => $"<color=#{Ui.HexOf(Ui.PlayerColor(a))}>{VoteSession.PlayerName(a).ToUpperInvariant()}</color>"));
        }
    }

    private string Status(Phase phase, string? winner)
    {
        string tally = $"{VoteSession.Votes.Count}/{VoteSession.PlayerCount} VOTED";
        float left = VoteSession.SecondsLeft;
        return phase switch
        {
            Phase.Open when left >= 0f => $"{tally}   CLOSES IN {Mathf.CeilToInt(left)}S",
            Phase.Open when _lobby => $"{tally}   ROLLS WHEN THE HOST STARTS",
            Phase.Open => $"{tally}   NO VOTES = RANDOM",
            _ when winner != null => $"NEXT MAP  <color=#{Ui.HexOf(Ui.Amber)}>{MapInfo.DisplayName(winner)}</color>",
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
