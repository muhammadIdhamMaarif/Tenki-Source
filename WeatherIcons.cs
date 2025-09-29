using UnityEngine;

/// <summary>
/// Static facade for fetching weather sprites anywhere.
/// Place a WeatherIconDatabase asset under a Resources folder,
/// named "WeatherIconDatabase" (default), or set a custom path.
/// </summary>
public static class WeatherIcons
{
    // Change if you store it elsewhere, e.g., "Data/Weather/WeatherIconDatabase"
    private const string DefaultResourcesPath = "_WeatherIconDatabase";

    private static WeatherIconDatabase _db;

    /// <summary>
    /// Assign at runtime if you don't want to use Resources.
    /// </summary>
    public static void SetDatabase(WeatherIconDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// Get a Sprite by weather code and day/night flag.
    /// Usage: Sprite s = WeatherIcons.GetSprite(1003, true);
    /// </summary>
    public static Sprite GetSprite(int code, bool isDay)
    {
        EnsureLoaded();
        return _db != null ? _db.GetSprite(code, isDay) : null;
    }

    /// <summary>
    /// Try-get variant without warnings.
    /// </summary>
    public static bool TryGetSprite(int code, bool isDay, out Sprite sprite)
    {
        EnsureLoaded();
        if (_db == null)
        {
            sprite = null;
            return false;
        }
        return _db.TryGetSprite(code, isDay, out sprite);
    }

    private static void EnsureLoaded()
    {
        if (_db != null) return;

        // Lazy-load from Resources (simple and reliable for small datasets)
        _db = Resources.Load<WeatherIconDatabase>(DefaultResourcesPath);

        if (_db == null)
        {
            Debug.LogError(
                $"WeatherIcons: Could not load WeatherIconDatabase from Resources at '{DefaultResourcesPath}'. " +
                $"Either place the asset there, or call WeatherIcons.SetDatabase(...) at startup.");
        }
    }
}