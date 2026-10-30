using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MapVote;

/// <summary>Palette, procedural sprites and tiny factory helpers. No asset bundle needed.</summary>
internal static class Ui
{
    // R.E.P.O.'s UI is warm amber on near-black with chunky uppercase type; stay in that family.
    public static readonly Color Panel = Hex("0D0D10F2");
    public static readonly Color Row = Hex("1A1A1F");
    public static readonly Color RowHover = Hex("26262D");
    public static readonly Color Text = Hex("F3EEE4");
    public static readonly Color Muted = Hex("8E887E");
    public static readonly Color Faint = Hex("FFFFFF14");
    public static readonly Color Amber = Hex("FFB31A");
    public static readonly Color Hot = Hex("FF5A1F");
    public static readonly Color Ink = Hex("141414");
    public static readonly Color Pointer = Hex("EF8E08"); // the game's menu cursor

    private static readonly Color[] PlayerColors =
    {
        Hex("FFB31A"), Hex("4FD1C5"), Hex("F56565"), Hex("9F7AEA"), Hex("68D391"), Hex("63B3ED"), Hex("F687B3"), Hex("F6E05E"),
    };

    private const int ArrowSize = 64;
    private const float ArrowPad = 2f;
    /// <summary>Where the arrow's tip sits in its sprite, for use as a pivot.</summary>
    public static readonly Vector2 ArrowTip = new(ArrowPad / ArrowSize, 1f - ArrowPad / ArrowSize);

    private static Sprite? _rounded, _roundedSmall, _ring, _dot, _fade, _hazard, _arrow;
    public static Sprite Rounded => _rounded ??= MakeRounded(64, 14);
    public static Sprite RoundedSmall => _roundedSmall ??= MakeRounded(32, 6);
    public static Sprite Ring => _ring ??= MakeCircle(128, 9);
    public static Sprite Dot => _dot ??= MakeCircle(64, 0);
    public static Sprite FadeRight => _fade ??= MakeFade(64);
    public static Sprite Hazard => _hazard ??= MakeHazard(32);
    public static Sprite Arrow => _arrow ??= MakeArrow();

    private static TMP_FontAsset? _font;
    private static readonly List<TextMeshProUGUI> Texts = new();
    public static bool FontReady => _font;

    public static Color Hex(string hex) => ColorUtility.TryParseHtmlString("#" + hex, out var c) ? c : Color.magenta;
    public static string HexOf(Color c) => ColorUtility.ToHtmlStringRGB(c);
    public static Color WithAlpha(this Color c, float a) => new(c.r, c.g, c.b, a);
    public static Color PlayerColor(int actor) => PlayerColors[(actor % PlayerColors.Length + PlayerColors.Length) % PlayerColors.Length];

    /// <summary>Frame-rate independent smoothing. Higher speed snaps faster.</summary>
    public static float Damp(float current, float target, float speed) =>
        Mathf.Lerp(current, target, 1f - Mathf.Exp(-speed * Time.unscaledDeltaTime));

    public static Vector2 Mouse => UnityEngine.InputSystem.Mouse.current?.position.ReadValue() ?? Vector2.negativeInfinity;

    public static bool Hovered(RectTransform rect) =>
        rect.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rect, Mouse, null);

    public static bool KeyDown(Key key) => Keyboard.current != null && key != Key.None && Keyboard.current[key].wasPressedThisFrame;

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5; // UI
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    public static Image Image(string name, Transform parent, Color color, Sprite? sprite = null)
    {
        var img = Rect(name, parent).gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        if (sprite)
        {
            img.sprite = sprite;
            img.type = sprite!.border != Vector4.zero ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
        }
        return img;
    }

    public static TextMeshProUGUI Label(string name, Transform parent, float size, Color color,
        TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, FontStyles style = FontStyles.Bold)
    {
        var tmp = Rect(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.fontStyle = style;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.raycastTarget = false;
        tmp.richText = true;
        // R.E.P.O.'s font has tall ascenders, and with Ellipsis TMP draws nothing at all once a line is taller than
        // its rect. Grow the text area away from the aligned edge so only horizontal overflow truncates.
        // TMP keeps the vertical part of the alignment in the high byte: Top = 0x100, Bottom = 0x400.
        int vertical = (int)align & 0xFF00;
        tmp.margin = new Vector4(0, vertical == 0x100 ? 0 : -size, 0, vertical == 0x400 ? 0 : -size);
        if (_font) tmp.font = _font;
        if (Texts.Count > 256) Texts.RemoveAll(t => !t);
        Texts.Add(tmp);
        return tmp;
    }

    /// <summary>Anchor + pivot at the same corner/edge, then offset and size.</summary>
    public static RectTransform Place(this RectTransform r, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        r.anchorMin = r.anchorMax = r.pivot = anchor;
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        return r;
    }

    /// <summary>Stretch to the parent with insets (left, top, right, bottom).</summary>
    public static RectTransform Fill(this RectTransform r, float l = 0, float t = 0, float rt = 0, float b = 0)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.pivot = new Vector2(0.5f, 0.5f);
        r.offsetMin = new Vector2(l, b);
        r.offsetMax = new Vector2(-rt, -t);
        return r;
    }

    /// <summary>Use the game's own font so we look native. Retries until a game text exists.</summary>
    public static void TryResolveFont(Transform ours)
    {
        if (_font) return;
        TMP_FontAsset? best = null;
        int bestScore = -1;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
        {
            if (!t.font || t.transform.IsChildOf(ours)) continue;
            // Prefer the HUD/menu font over debug text: it is the one used on the most labels.
            int score = t.GetComponentInParent<HUDCanvas>() || t.GetComponentInParent<MenuPage>() ? 2 : 1;
            if (score > bestScore) { best = t.font; bestScore = score; }
        }
        best ??= TMP_Settings.defaultFontAsset;
        if (!best) return;
        _font = best;
        Texts.RemoveAll(t => !t);
        foreach (var t in Texts) t.font = _font;
    }

    public static void Sound(Action<MenuManager> play)
    {
        try
        {
            if (MenuManager.instance) play(MenuManager.instance);
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Menu sound failed: {e.Message}");
        }
    }

    /// <summary>The game's menu click sound. Bound by name: its signature changes between game updates.</summary>
    public static void Click(MenuManager.MenuClickEffectType type, float pitch = -1f, float volume = -1f) =>
        Sound(m => Compat.Call(m, nameof(MenuManager.MenuEffectClick), type, null, pitch, volume, true));

    public static void Tick(float pitch = 1f) => Click(MenuManager.MenuClickEffectType.Tick, pitch, 0.6f);

    // ---- procedural sprites -------------------------------------------------------------

    private static Sprite Finish(Texture2D tex, Vector4 border)
    {
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var sprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static Sprite MakeRounded(int size, int radius)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Signed distance to a rounded square, anti-aliased over one pixel.
            float dx = Mathf.Max(Mathf.Abs(x + 0.5f - size / 2f) - (size / 2f - radius), 0);
            float dy = Mathf.Max(Mathf.Abs(y + 0.5f - size / 2f) - (size / 2f - radius), 0);
            float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
            tex.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        return Finish(tex, new Vector4(radius, radius, radius, radius));
    }

    /// <summary>Filled disc when thickness is 0, otherwise a ring (for radial countdowns).</summary>
    private static Sprite MakeCircle(int size, int thickness)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size / 2f - 1f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size / 2f, size / 2f));
            float outer = Mathf.Clamp01(r - d + 0.5f);
            float inner = thickness > 0 ? Mathf.Clamp01(d - (r - thickness) + 0.5f) : 1f;
            tex.SetPixel(x, y, new Color(1, 1, 1, outer * inner));
        }
        return Finish(tex, Vector4.zero);
    }

    private static Sprite MakeFade(int width)
    {
        var tex = new Texture2D(width, 1, TextureFormat.RGBA32, false);
        for (int x = 0; x < width; x++) tex.SetPixel(x, 0, new Color(1, 1, 1, Mathf.SmoothStep(0, 1, x / (width - 1f))));
        return Finish(tex, Vector4.zero);
    }

    /// <summary>The game's menu pointer: an arrowhead with its tip at the top-left and a notch at the back.</summary>
    private static Sprite MakeArrow()
    {
        // Traced from the in-game cursor, in its own pixels (y down from the tip).
        var outline = new[] { new Vector2(0, 0), new Vector2(50, 21), new Vector2(23, 29), new Vector2(16, 48) };
        const int size = ArrowSize;
        float scale = (size - 2 * ArrowPad) / 50f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int hits = 0;
            for (int s = 0; s < 16; s++) // 4x4 supersampling for smooth edges
            {
                var p = new Vector2(x + (s % 4 + 0.5f) / 4f - ArrowPad, size - y - (s / 4 + 0.5f) / 4f - ArrowPad) / scale;
                if (InPolygon(outline, p)) hits++;
            }
            tex.SetPixel(x, y, new Color(1, 1, 1, hits / 16f));
        }
        return Finish(tex, Vector4.zero);
    }

    private static bool InPolygon(Vector2[] poly, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if (poly[i].y > p.y != poly[j].y > p.y &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    /// <summary>Diagonal warning stripes, tiled. White/clear so the Image colour tints it.</summary>
    private static Sprite MakeHazard(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float band = Mathf.Repeat(x + y, size) / size;
            float a = Mathf.Clamp01((Mathf.Abs(band - 0.5f) - 0.25f) * size * 0.5f + 0.5f);
            tex.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        tex.Apply(false, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var sprite = UnityEngine.Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
