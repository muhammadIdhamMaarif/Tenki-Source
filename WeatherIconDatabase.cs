using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(
    fileName = "WeatherIconDatabase",
    menuName = "Weather/Weather Icon Database",
    order = 0)]
public class WeatherIconDatabase : ScriptableObject
{
    [Serializable]
    public struct Entry
    {
        [Tooltip("Weather code, e.g., 1003")]
        public int code;

        [Tooltip("Sprite for daytime")]
        public Sprite day;

        [Tooltip("Sprite for nighttime")]
        public Sprite night;
    }

    [SerializeField]
    private List<Entry> entries = new List<Entry>();

    // Fast lookup at runtime
    private Dictionary<int, Entry> _lookup;

    void OnEnable()
    {
        BuildLookup();
    }

    private void BuildLookup()
    {
        if (_lookup != null) return;

        _lookup = new Dictionary<int, Entry>(entries.Count);
        foreach (var e in entries)
        {
            // Last one wins if duplicates exist
            _lookup[e.code] = e;
        }
    }

    /// <summary>
    /// Returns the Sprite for (code, isDay). Returns null if not found.
    /// </summary>
    public Sprite GetSprite(int code, bool isDay)
    {
        if (_lookup == null) BuildLookup();

        if (_lookup.TryGetValue(code, out var e))
        {
            var sprite = isDay ? e.day : e.night;
            if (sprite == null)
            {
                Debug.LogWarning($"WeatherIconDatabase: Sprite missing for code {code} (isDay={isDay}).");
            }
            return sprite;
        }

        Debug.LogWarning($"WeatherIconDatabase: No entry for code {code}.");
        return null;
    }

    /// <summary>
    /// Try-get variant that avoids warnings and returns boolean success.
    /// </summary>
    public bool TryGetSprite(int code, bool isDay, out Sprite sprite)
    {
        if (_lookup == null) BuildLookup();

        if (_lookup.TryGetValue(code, out var e))
        {
            sprite = isDay ? e.day : e.night;
            return sprite != null;
        }

        sprite = null;
        return false;
    }
}
