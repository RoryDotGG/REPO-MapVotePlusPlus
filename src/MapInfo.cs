using System;
using System.Collections.Generic;
using UnityEngine;

namespace MapVote;

/// <summary>Display data derived from a Level asset, so custom maps get a name and colour for free.</summary>
internal sealed class MapInfo
{
    public string Name = "";
    public Color Color = Ui.Amber;

    private static readonly Dictionary<string, MapInfo> Cache = new();

    public static string DisplayName(string? levelName)
    {
        if (string.IsNullOrEmpty(levelName)) return "";
        if (levelName == VoteLogic.RandomChoice) return "RANDOM";
        foreach (var level in VoteSession.Pool)
            if (level.name == levelName) return Get(level).Name;
        return Strip(levelName!);
    }

    public static MapInfo Get(Level level)
    {
        if (Cache.TryGetValue(level.name, out var info)) return info;
        info = new MapInfo { Name = NameOf(level), Color = ColorOf(level) };
        Cache[level.name] = info;
        return info;
    }

    private static string Strip(string name) =>
        (name.StartsWith("Level - ", StringComparison.OrdinalIgnoreCase) ? name.Substring(8) : name).Trim().ToUpperInvariant();

    private static string NameOf(Level level)
    {
        try
        {
            var localized = level.NarrativeNameLocalized
                ? Compat.Call(level.NarrativeNameLocalized, nameof(LocalizedAsset.GetLocalizedString)) as string
                : null;
            if (!string.IsNullOrWhiteSpace(localized)) return localized!.Trim().ToUpperInvariant();
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"No localized name for {level.name}: {e.Message}");
        }
        return !string.IsNullOrWhiteSpace(level.NarrativeName) ? level.NarrativeName.Trim().ToUpperInvariant() : Strip(level.name);
    }

    /// <summary>Each level's fog colour, pushed to a vivid accent. Grey fog falls back to a name-hashed hue.</summary>
    private static Color ColorOf(Level level)
    {
        Color.RGBToHSV(level.FogColor, out float h, out float s, out _);
        if (s < 0.15f)
        {
            // string.GetHashCode is not guaranteed stable across processes; every client must agree.
            uint hash = 2166136261;
            foreach (char c in level.name) hash = (hash ^ c) * 16777619;
            h = (hash & 0xFFFF) / 65535f;
        }
        return Color.HSVToRGB(h, Mathf.Max(s, 0.55f), 0.95f);
    }
}
