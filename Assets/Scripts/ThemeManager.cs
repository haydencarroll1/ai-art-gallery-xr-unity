using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Manages gallery themes. Works without any Inspector setup because all 16 themes
// are hardcoded below. ScriptableObject themes (from Resources/Themes/) are optional
// overrides that get loaded on top if present.
//
// Usage:
//   ThemeManager.Instance.SetTheme(theme, mood) to set theme
//   ThemeManager.Instance.GetPalette() to get current colors

// Default theme values.
public static class ThemeDefaults
{
    public const string Theme = "default";
    public const string Mood = "calm";
    public const float Variety = 0.5f;
    public const int RoomCount = 1;
    
    public static int GenerateLayoutSeed() => Random.Range(0, int.MaxValue);
}

// Valid theme identifiers matching the web app schema (schemas.js).
public static class ThemeIds
{
    public const string Classical = "classical";
    public const string Medieval = "medieval";
    public const string Gothic = "gothic";
    public const string Renaissance = "renaissance";
    public const string Asian = "asian";
    public const string Industrial = "industrial";
    public const string Modern = "modern";
    public const string Minimalist = "minimalist";
    public const string Futuristic = "futuristic";
    public const string Neon = "neon";
    public const string Nature = "nature";
    public const string Desert = "desert";
    public const string Ethereal = "ethereal";
    public const string Warm = "warm";
    public const string Cool = "cool";
    public const string Default = "default";
    
    public static readonly string[] All = new[]
    {
        Classical, Medieval, Gothic, Renaissance, Asian, Industrial,
        Modern, Minimalist, Futuristic, Neon, Nature, Desert,
        Ethereal, Warm, Cool, Default
    };
    
    public static bool IsValid(string theme) =>
        System.Array.Exists(All, t => t == theme);
}

// Valid mood identifiers matching the web app schema (schemas.js).
public static class MoodIds
{
    public const string Bright = "bright";
    public const string Dark = "dark";
    public const string Ethereal = "ethereal";
    public const string Dramatic = "dramatic";
    public const string Calm = "calm";
    
    public static readonly string[] All = new[]
    {
        Bright, Dark, Ethereal, Dramatic, Calm
    };
    
    public static bool IsValid(string mood) =>
        System.Array.Exists(All, m => m == mood);
}

// Theme palette definition used by procedural room generation.
// Contains material colors and lighting defaults that the topology
// generators use when building walls, floors, and ceilings.
[System.Serializable]
public class ThemePalette
{
    public Color primaryWall = new Color(0.9f, 0.88f, 0.85f);
    public Color secondaryWall = new Color(0.88f, 0.85f, 0.82f);
    public Color floor = new Color(0.3f, 0.25f, 0.2f);
    public Color ceiling = new Color(0.95f, 0.93f, 0.9f);
    public Color trim = new Color(0.7f, 0.6f, 0.4f);
    public Color accent = new Color(0.4f, 0.3f, 0.2f);
    public Color emissive = new Color(1f, 0.8f, 0.6f);
    public float emissiveIntensity = 0f;
    public Color lightColor = Color.white;
    public float lightIntensity = 1.2f;
    
    public ThemePalette Clone()
    {
        return new ThemePalette
        {
            primaryWall = primaryWall,
            secondaryWall = secondaryWall,
            floor = floor,
            ceiling = ceiling,
            trim = trim,
            accent = accent,
            emissive = emissive,
            emissiveIntensity = emissiveIntensity,
            lightColor = lightColor,
            lightIntensity = lightIntensity
        };
    }
}

// Singleton manager for gallery themes. Auto-creates itself if no instance
// exists in the scene, so it works with zero Inspector setup.
public class ThemeManager : MonoBehaviour
{
    private static ThemeManager _instance;
    
    public static ThemeManager Instance
    {
        get
        {
            // Already have an instance? Use it
            if (_instance != null) return _instance;
            
            // Try to find one in the scene
            _instance = FindFirstObjectByType<ThemeManager>();
            
            // Still nothing? Create one automatically!
            if (_instance == null)
            {
                GameObject go = new GameObject("ThemeManager (Auto-Created)");
                _instance = go.AddComponent<ThemeManager>();
                DontDestroyOnLoad(go);
#if UNITY_EDITOR
                Debug.Log("[ThemeManager] Auto-created instance (no Inspector setup needed)");
#endif
            }
            
            return _instance;
        }
    }
    
    // All 16 built-in theme palettes. These are always available even without
    // any ScriptableObject assets in the project.
    private static readonly Dictionary<string, ThemePalette> BuiltInThemes = new Dictionary<string, ThemePalette>
    {
        // Default - Clean museum gallery
        ["default"] = new ThemePalette
        {
            primaryWall = new Color(0.95f, 0.93f, 0.9f),
            secondaryWall = new Color(0.9f, 0.88f, 0.85f),
            floor = new Color(0.35f, 0.3f, 0.25f),
            ceiling = new Color(0.98f, 0.97f, 0.95f),
            trim = new Color(0.7f, 0.65f, 0.55f),
            accent = new Color(0.8f, 0.7f, 0.5f),
            emissive = new Color(1f, 0.9f, 0.7f),
            emissiveIntensity = 0.1f,
            lightColor = new Color(1f, 0.98f, 0.95f),
            lightIntensity = 1.2f
        },
        
        // Classical - Warm museum feel
        ["classical"] = new ThemePalette
        {
            primaryWall = new Color(0.85f, 0.78f, 0.65f),
            secondaryWall = new Color(0.75f, 0.68f, 0.55f),
            floor = new Color(0.4f, 0.25f, 0.15f),
            ceiling = new Color(0.9f, 0.85f, 0.75f),
            trim = new Color(0.6f, 0.45f, 0.2f),
            accent = new Color(0.8f, 0.6f, 0.2f),
            emissive = new Color(1f, 0.75f, 0.4f),
            emissiveIntensity = 0.3f,
            lightColor = new Color(1f, 0.95f, 0.85f),
            lightIntensity = 1.0f
        },
        
        // Medieval - Heavy stone and timber
        ["medieval"] = new ThemePalette
        {
            primaryWall = new Color(0.5f, 0.45f, 0.4f),
            secondaryWall = new Color(0.35f, 0.3f, 0.28f),
            floor = new Color(0.25f, 0.18f, 0.12f),
            ceiling = new Color(0.4f, 0.35f, 0.3f),
            trim = new Color(0.3f, 0.25f, 0.2f),
            accent = new Color(0.6f, 0.4f, 0.2f),
            emissive = new Color(1f, 0.6f, 0.2f),
            emissiveIntensity = 0.3f,
            lightColor = new Color(1f, 0.9f, 0.8f),
            lightIntensity = 0.9f
        },
        
        // Gothic - Dark and dramatic
        ["gothic"] = new ThemePalette
        {
            primaryWall = new Color(0.15f, 0.12f, 0.1f),
            secondaryWall = new Color(0.2f, 0.1f, 0.1f),
            floor = new Color(0.1f, 0.08f, 0.06f),
            ceiling = new Color(0.12f, 0.1f, 0.08f),
            trim = new Color(0.3f, 0.25f, 0.15f),
            accent = new Color(0.5f, 0.1f, 0.15f),
            emissive = new Color(1f, 0.6f, 0.2f),
            emissiveIntensity = 0.5f,
            lightColor = new Color(0.9f, 0.8f, 0.7f),
            lightIntensity = 0.8f
        },
        
        // Renaissance - Rich plaster and marble
        ["renaissance"] = new ThemePalette
        {
            primaryWall = new Color(0.88f, 0.82f, 0.72f),
            secondaryWall = new Color(0.75f, 0.65f, 0.55f),
            floor = new Color(0.5f, 0.3f, 0.2f),
            ceiling = new Color(0.92f, 0.88f, 0.8f),
            trim = new Color(0.7f, 0.6f, 0.45f),
            accent = new Color(0.8f, 0.65f, 0.3f),
            emissive = new Color(1f, 0.8f, 0.5f),
            emissiveIntensity = 0.2f,
            lightColor = new Color(1f, 0.95f, 0.85f),
            lightIntensity = 1.0f
        },
        
        // Asian - Paper and dark wood
        ["asian"] = new ThemePalette
        {
            primaryWall = new Color(0.85f, 0.82f, 0.75f),
            secondaryWall = new Color(0.45f, 0.35f, 0.25f),
            floor = new Color(0.35f, 0.25f, 0.18f),
            ceiling = new Color(0.9f, 0.88f, 0.82f),
            trim = new Color(0.3f, 0.2f, 0.15f),
            accent = new Color(0.6f, 0.2f, 0.2f),
            emissive = new Color(1f, 0.7f, 0.4f),
            emissiveIntensity = 0.15f,
            lightColor = new Color(1f, 0.95f, 0.85f),
            lightIntensity = 1.0f
        },
        
        // Industrial - Raw concrete and metal
        ["industrial"] = new ThemePalette
        {
            primaryWall = new Color(0.5f, 0.5f, 0.5f),
            secondaryWall = new Color(0.4f, 0.4f, 0.4f),
            floor = new Color(0.35f, 0.35f, 0.35f),
            ceiling = new Color(0.3f, 0.3f, 0.3f),
            trim = new Color(0.2f, 0.2f, 0.2f),
            accent = new Color(0.7f, 0.5f, 0.2f),
            emissive = new Color(1f, 0.6f, 0.2f),
            emissiveIntensity = 0.15f,
            lightColor = new Color(1f, 0.95f, 0.9f),
            lightIntensity = 1.2f
        },
        
        // Modern - Minimalist white cube
        ["modern"] = new ThemePalette
        {
            primaryWall = new Color(0.95f, 0.95f, 0.95f),
            secondaryWall = new Color(0.2f, 0.2f, 0.2f),
            floor = new Color(0.3f, 0.3f, 0.3f),
            ceiling = Color.white,
            trim = new Color(0.1f, 0.1f, 0.1f),
            accent = new Color(0.2f, 0.2f, 0.2f),
            emissive = Color.white,
            emissiveIntensity = 0.2f,
            lightColor = Color.white,
            lightIntensity = 1.4f
        },
        
        // Minimalist - High key, low contrast
        ["minimalist"] = new ThemePalette
        {
            primaryWall = new Color(0.97f, 0.97f, 0.97f),
            secondaryWall = new Color(0.9f, 0.9f, 0.9f),
            floor = new Color(0.85f, 0.85f, 0.85f),
            ceiling = Color.white,
            trim = new Color(0.9f, 0.9f, 0.9f),
            accent = new Color(0.15f, 0.15f, 0.15f),
            emissive = Color.white,
            emissiveIntensity = 0.05f,
            lightColor = Color.white,
            lightIntensity = 1.3f
        },
        
        // Neon - Cyberpunk vibes
        ["neon"] = new ThemePalette
        {
            primaryWall = new Color(0.05f, 0.05f, 0.1f),
            secondaryWall = new Color(0.1f, 0.02f, 0.15f),
            floor = new Color(0.02f, 0.02f, 0.05f),
            ceiling = new Color(0.03f, 0.03f, 0.08f),
            trim = new Color(0.1f, 0.1f, 0.2f),
            accent = new Color(0f, 1f, 0.8f),
            emissive = new Color(0.1f, 0.9f, 1f),
            emissiveIntensity = 1.0f,
            lightColor = new Color(0.8f, 0.5f, 1f),
            lightIntensity = 1.0f
        },
        
        // Nature - Forest gallery
        ["nature"] = new ThemePalette
        {
            primaryWall = new Color(0.85f, 0.8f, 0.7f),
            secondaryWall = new Color(0.4f, 0.5f, 0.35f),
            floor = new Color(0.4f, 0.3f, 0.2f),
            ceiling = new Color(0.9f, 0.92f, 0.88f),
            trim = new Color(0.5f, 0.4f, 0.3f),
            accent = new Color(0.4f, 0.6f, 0.35f),
            emissive = new Color(0.4f, 0.6f, 0.3f),
            emissiveIntensity = 0.1f,
            lightColor = new Color(1f, 0.98f, 0.9f),
            lightIntensity = 1.1f
        },
        
        // Desert - Warm sandstone
        ["desert"] = new ThemePalette
        {
            primaryWall = new Color(0.9f, 0.8f, 0.65f),
            secondaryWall = new Color(0.85f, 0.7f, 0.5f),
            floor = new Color(0.7f, 0.55f, 0.4f),
            ceiling = new Color(0.95f, 0.88f, 0.75f),
            trim = new Color(0.5f, 0.35f, 0.2f),
            accent = new Color(0.85f, 0.5f, 0.2f),
            emissive = new Color(1f, 0.7f, 0.4f),
            emissiveIntensity = 0.2f,
            lightColor = new Color(1f, 0.95f, 0.85f),
            lightIntensity = 1.3f
        },
        
        // Futuristic - Dark sci-fi with cyan accents
        ["futuristic"] = new ThemePalette
        {
            primaryWall = new Color(0.1f, 0.1f, 0.15f),
            secondaryWall = new Color(0.05f, 0.05f, 0.1f),
            floor = new Color(0.08f, 0.08f, 0.1f),
            ceiling = new Color(0.05f, 0.05f, 0.08f),
            trim = new Color(0f, 0.8f, 1f),
            accent = new Color(0f, 0.8f, 1f),
            emissive = new Color(0f, 0.8f, 1f),
            emissiveIntensity = 1.0f,
            lightColor = new Color(0.6f, 0.8f, 1f),
            lightIntensity = 1.1f
        },
        
        // Ethereal - Airy, luminous space
        ["ethereal"] = new ThemePalette
        {
            primaryWall = new Color(0.85f, 0.9f, 1f),
            secondaryWall = new Color(0.7f, 0.8f, 0.95f),
            floor = new Color(0.8f, 0.85f, 0.95f),
            ceiling = new Color(0.95f, 0.97f, 1f),
            trim = new Color(0.75f, 0.85f, 0.95f),
            accent = new Color(0.6f, 0.75f, 1f),
            emissive = new Color(0.7f, 0.85f, 1f),
            emissiveIntensity = 0.4f,
            lightColor = new Color(0.8f, 0.9f, 1f),
            lightIntensity = 1.0f
        },
        
        // Warm - Cozy, amber tones
        ["warm"] = new ThemePalette
        {
            primaryWall = new Color(0.9f, 0.8f, 0.7f),
            secondaryWall = new Color(0.8f, 0.65f, 0.5f),
            floor = new Color(0.5f, 0.35f, 0.25f),
            ceiling = new Color(0.95f, 0.88f, 0.8f),
            trim = new Color(0.6f, 0.45f, 0.3f),
            accent = new Color(0.9f, 0.6f, 0.3f),
            emissive = new Color(1f, 0.7f, 0.4f),
            emissiveIntensity = 0.2f,
            lightColor = new Color(1f, 0.95f, 0.85f),
            lightIntensity = 1.2f
        },
        
        // Cool - Clean, blue-grey
        ["cool"] = new ThemePalette
        {
            primaryWall = new Color(0.8f, 0.85f, 0.9f),
            secondaryWall = new Color(0.6f, 0.7f, 0.8f),
            floor = new Color(0.4f, 0.45f, 0.5f),
            ceiling = new Color(0.9f, 0.95f, 1f),
            trim = new Color(0.5f, 0.6f, 0.7f),
            accent = new Color(0.3f, 0.6f, 1f),
            emissive = new Color(0.3f, 0.6f, 1f),
            emissiveIntensity = 0.3f,
            lightColor = new Color(0.8f, 0.9f, 1f),
            lightIntensity = 1.1f
        }
    };
    
    [Header("Current State (Read Only in Inspector)")]
    [SerializeField] private string currentTheme = ThemeDefaults.Theme;
    [SerializeField] private string currentMood = ThemeDefaults.Mood;
    
    [Header("Optional Overrides")]
    [Tooltip("ScriptableObject themes loaded from Resources/Themes/ (optional)")]
    [SerializeField] private ThemeConfig defaultThemeAsset;
    
    // Current palette being used
    private ThemePalette activePalette;
    
    // Loaded ScriptableObject themes (optional, loaded from Resources)
    private Dictionary<string, ThemeConfig> loadedAssetThemes = new Dictionary<string, ThemeConfig>();
    
    private Light cachedMainLight;
    
    public string CurrentTheme => currentTheme;
    public string CurrentMood => currentMood;

    // Returns the active palette. Falls back to default so it never returns null.
    public ThemePalette GetPalette() => activePalette ?? BuiltInThemes[ThemeDefaults.Theme];

    private void Awake()
    {
        // Handle duplicate instances (e.g., scene reload)
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[ThemeManager] Duplicate instance destroyed");
            Destroy(gameObject);
            return;
        }
        
        _instance = this;
        
        // DontDestroyOnLoad only works on root GameObjects — detach first if parented
        if (transform.parent != null)
        {
            transform.SetParent(null);
        }
        DontDestroyOnLoad(gameObject);
        
        // Initialize with default theme
        activePalette = BuiltInThemes[ThemeDefaults.Theme].Clone();
        
        // Try to load any ScriptableObject themes from Resources (optional)
        TryLoadAssetThemes();
    }
    
    // Tries to load ThemeConfig ScriptableObjects from Resources/Themes/.
    // These are optional - the system works fine without them.
    private void TryLoadAssetThemes()
    {
        loadedAssetThemes.Clear();
        
        ThemeConfig[] themes = Resources.LoadAll<ThemeConfig>("Themes");
        
        if (themes == null || themes.Length == 0)
        {
#if UNITY_EDITOR
            Debug.Log("[ThemeManager] No ScriptableObject themes found - using built-in defaults (this is fine!)");
#endif
            return;
        }
        
        foreach (var theme in themes)
        {
            string key = theme.name.ToLowerInvariant();
            loadedAssetThemes[key] = theme;
        }
        
#if UNITY_EDITOR
        Debug.Log($"[ThemeManager] Loaded {loadedAssetThemes.Count} optional theme assets");
#endif
    }
    
    // Sets the active theme. Checks ScriptableObject overrides first, then
    // falls back to the built-in dictionary, then to default. Returns the palette.
    public ThemePalette SetTheme(string themeId, string moodId = null)
    {
        currentTheme = NormalizeThemeId(themeId);
        currentMood = NormalizeMoodId(moodId);
        
        if (!ThemeIds.IsValid(currentTheme))
        {
            Debug.LogWarning($"[ThemeManager] Unknown theme '{currentTheme}', using default");
            currentTheme = ThemeDefaults.Theme;
        }
        
        if (!MoodIds.IsValid(currentMood))
        {
            Debug.LogWarning($"[ThemeManager] Unknown mood '{currentMood}', using default");
            currentMood = ThemeDefaults.Mood;
        }
        
        string key = currentTheme.ToLowerInvariant();
        
        // Priority 1: Check for ScriptableObject asset override
        if (loadedAssetThemes.TryGetValue(key, out var assetTheme) && assetTheme != null)
        {
            // Convert ThemeConfig to ThemePalette
            activePalette = ConvertThemeConfigToPalette(assetTheme);
#if UNITY_EDITOR
            Debug.Log($"[ThemeManager] Theme '{currentTheme}' loaded from asset");
#endif
        }
        // Priority 2: Use built-in hardcoded theme
        else if (BuiltInThemes.TryGetValue(key, out var builtIn))
        {
            activePalette = builtIn.Clone();
#if UNITY_EDITOR
            Debug.Log($"[ThemeManager] Theme '{currentTheme}' loaded from built-in defaults");
#endif
        }
        // Fallback: Use default theme
        else
        {
            activePalette = BuiltInThemes[ThemeDefaults.Theme].Clone();
#if UNITY_EDITOR
            Debug.Log($"[ThemeManager] Theme '{currentTheme}' not found, using default");
#endif
        }

        // Apply mood modifications to palette
        ApplyMoodToPalette(activePalette, currentMood);

#if UNITY_EDITOR
        Debug.Log($"[ThemeManager] Applied theme: {currentTheme}, mood: {currentMood}");
#endif
        
        return activePalette;
    }
    
    // Adjusts the directional light colour and intensity based on mood.
    // Also sets ambient intensity and optional fog for ethereal mood.
    public void ApplyMoodLighting(string mood)
    {
        string resolvedMood = NormalizeMoodId(mood);
        if (!MoodIds.IsValid(resolvedMood))
        {
            resolvedMood = NormalizeMoodId(currentMood);
        }
        if (!MoodIds.IsValid(resolvedMood))
        {
            resolvedMood = ThemeDefaults.Mood;
        }
        
        ThemePalette palette = GetPalette();
        float intensity = palette != null ? palette.lightIntensity : 1f;
        Color color = palette != null ? palette.lightColor : Color.white;
        
        Light light = FindOrCreateMainLight();
        if (light == null) return;
        
        switch (resolvedMood)
        {
            case MoodIds.Dark:
                intensity *= 0.35f;
                color = Color.Lerp(color, new Color(0.8f, 0.7f, 0.6f), 0.6f);
                RenderSettings.ambientIntensity = 0.2f;
                break;
            
            case MoodIds.Bright:
                intensity *= 1.2f;
                color = Color.Lerp(color, Color.white, 0.6f);
                RenderSettings.ambientIntensity = 0.8f;
                break;
            
            case MoodIds.Ethereal:
                intensity *= 0.6f;
                color = Color.Lerp(color, new Color(0.7f, 0.8f, 1f), 0.6f);
                RenderSettings.ambientIntensity = 0.5f;
                // Gentle fog if mood wants it
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogColor = new Color(0.8f, 0.85f, 0.95f);
                RenderSettings.fogDensity = Mathf.Max(RenderSettings.fogDensity, 0.02f);
                break;
            
            case MoodIds.Dramatic:
                intensity *= 0.8f;
                color = Color.Lerp(color, new Color(1f, 0.9f, 0.7f), 0.5f);
                RenderSettings.ambientIntensity = 0.1f;
                break;
            
            case MoodIds.Calm:
            default:
                intensity *= 0.95f;
                color = Color.Lerp(color, new Color(1f, 0.98f, 0.95f), 0.3f);
                RenderSettings.ambientIntensity = 0.4f;
                break;
        }
        
        light.color = color;
        light.intensity = intensity;
        light.shadows = LightShadows.None; // Better VR performance
    }
    
    // Applies ambient light colour and fog settings. The theme sets the base
    // values, then the mood modifies them (e.g. dark mood makes things darker).
    public void ApplyAtmosphere(string themeId, string moodId)
    {
        string theme = NormalizeThemeId(themeId);
        if (!ThemeIds.IsValid(theme)) theme = ThemeDefaults.Theme;
        
        string mood = NormalizeMoodId(moodId);
        if (!MoodIds.IsValid(mood)) mood = ThemeDefaults.Mood;
        
        Color ambient = new Color(0.4f, 0.4f, 0.4f);
        bool fog = false;
        Color fogColor = new Color(0.5f, 0.5f, 0.5f);
        float fogDensity = 0.01f;
        FogMode fogMode = FogMode.Exponential;
        
        switch (theme)
        {
            case ThemeIds.Gothic:
                ambient = new Color(0.1f, 0.08f, 0.06f);
                fog = true;
                fogColor = new Color(0.05f, 0.05f, 0.08f);
                fogDensity = 0.03f;
                break;
            
            case ThemeIds.Modern:
            case ThemeIds.Minimalist:
                ambient = new Color(0.5f, 0.5f, 0.5f);
                fog = false;
                break;
            
            case ThemeIds.Nature:
                ambient = new Color(0.4f, 0.45f, 0.35f);
                fog = true;
                fogColor = new Color(0.7f, 0.8f, 0.6f);
                fogDensity = 0.01f;
                break;
            
            case ThemeIds.Neon:
            case ThemeIds.Futuristic:
                ambient = new Color(0.1f, 0.12f, 0.18f);
                fog = true;
                fogColor = new Color(0.05f, 0.08f, 0.12f);
                fogDensity = 0.02f;
                break;
            
            case ThemeIds.Industrial:
                ambient = new Color(0.25f, 0.25f, 0.25f);
                fog = true;
                fogColor = new Color(0.2f, 0.2f, 0.2f);
                fogDensity = 0.015f;
                break;
            
            case ThemeIds.Desert:
            case ThemeIds.Warm:
                ambient = new Color(0.45f, 0.35f, 0.25f);
                fog = true;
                fogColor = new Color(0.85f, 0.75f, 0.6f);
                fogDensity = 0.008f;
                break;
            
            case ThemeIds.Cool:
            case ThemeIds.Ethereal:
                ambient = new Color(0.35f, 0.4f, 0.5f);
                fog = true;
                fogColor = new Color(0.7f, 0.8f, 0.95f);
                fogDensity = 0.015f;
                break;
            
            default:
                ambient = new Color(0.4f, 0.4f, 0.4f);
                fog = false;
                break;
        }
        
        // Mood adjustments on top of theme
        switch (mood)
        {
            case MoodIds.Dark:
                ambient = Color.Lerp(ambient, Color.black, 0.3f);
                fog = true;
                fogDensity = Mathf.Max(fogDensity, 0.02f);
                break;
            
            case MoodIds.Bright:
                ambient = Color.Lerp(ambient, Color.white, 0.2f);
                if (theme == ThemeIds.Modern || theme == ThemeIds.Minimalist)
                {
                    fog = false;
                }
                break;
            
            case MoodIds.Ethereal:
                fog = true;
                fogColor = Color.Lerp(fogColor, new Color(0.8f, 0.85f, 0.95f), 0.6f);
                fogDensity = Mathf.Max(fogDensity, 0.02f);
                break;
            
            case MoodIds.Dramatic:
                ambient = Color.Lerp(ambient, Color.black, 0.2f);
                fog = true;
                fogDensity = Mathf.Max(fogDensity, 0.018f);
                break;
            
            case MoodIds.Calm:
            default:
                ambient = Color.Lerp(ambient, new Color(0.95f, 0.95f, 0.98f), 0.05f);
                break;
        }
        
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = ambient;
        
        RenderSettings.fog = fog;
        if (fog)
        {
            RenderSettings.fogMode = fogMode;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = fogDensity;
        }
    }
    
    // Converts a ThemeConfig ScriptableObject into a ThemePalette we can use.
    private ThemePalette ConvertThemeConfigToPalette(ThemeConfig config)
    {
        if (config == null)
        {
            return BuiltInThemes[ThemeDefaults.Theme].Clone();
        }
        
        string key = !string.IsNullOrEmpty(config.themeName)
            ? config.themeName.ToLowerInvariant()
            : config.name.ToLowerInvariant();
        
        ThemePalette palette = BuiltInThemes.TryGetValue(key, out var builtIn)
            ? builtIn.Clone()
            : BuiltInThemes[ThemeDefaults.Theme].Clone();
        
        if (config.wallMaterial != null)
        {
            Color wallColor = GetMaterialColor(config.wallMaterial, palette.primaryWall);
            palette.primaryWall = wallColor;
            palette.secondaryWall = wallColor;
        }
            
        if (config.floorMaterial != null)
            palette.floor = GetMaterialColor(config.floorMaterial, palette.floor);
            
        if (config.ceilingMaterial != null)
            palette.ceiling = GetMaterialColor(config.ceilingMaterial, palette.ceiling);
        
        palette.lightColor = config.ambientLightColor;
        palette.lightIntensity = config.lightIntensity;
        
        return palette;
    }
    
    // Tweaks palette colours based on mood. For example, "bright" lerps walls
    // towards white, "dark" lerps them towards black, etc.
    private void ApplyMoodToPalette(ThemePalette palette, string mood)
    {
        if (palette == null) return;
        
        switch (mood)
        {
            case MoodIds.Bright:
                palette.primaryWall = Color.Lerp(palette.primaryWall, Color.white, 0.2f);
                palette.secondaryWall = Color.Lerp(palette.secondaryWall, Color.white, 0.2f);
                palette.ceiling = Color.Lerp(palette.ceiling, Color.white, 0.25f);
                palette.floor = Color.Lerp(palette.floor, Color.white, 0.05f);
                palette.trim = Color.Lerp(palette.trim, Color.white, 0.1f);
                palette.accent = Color.Lerp(palette.accent, Color.white, 0.05f);
                palette.emissiveIntensity *= 0.9f;
                break;
                
            case MoodIds.Dark:
                palette.primaryWall = Color.Lerp(palette.primaryWall, Color.black, 0.25f);
                palette.secondaryWall = Color.Lerp(palette.secondaryWall, Color.black, 0.3f);
                palette.floor = Color.Lerp(palette.floor, Color.black, 0.2f);
                palette.ceiling = Color.Lerp(palette.ceiling, Color.black, 0.3f);
                palette.trim = Color.Lerp(palette.trim, Color.black, 0.2f);
                palette.accent = Color.Lerp(palette.accent, new Color(0.2f, 0.05f, 0.05f), 0.1f);
                palette.emissiveIntensity *= 1.2f;
                break;
                
            case MoodIds.Ethereal:
                Color etherealTint = new Color(0.8f, 0.88f, 1f);
                palette.primaryWall = Color.Lerp(palette.primaryWall, etherealTint, 0.25f);
                palette.secondaryWall = Color.Lerp(palette.secondaryWall, etherealTint, 0.2f);
                palette.ceiling = Color.Lerp(palette.ceiling, etherealTint, 0.3f);
                palette.trim = Color.Lerp(palette.trim, etherealTint, 0.2f);
                palette.accent = Color.Lerp(palette.accent, new Color(0.6f, 0.75f, 1f), 0.2f);
                palette.emissive = Color.Lerp(palette.emissive, new Color(0.7f, 0.85f, 1f), 0.3f);
                palette.emissiveIntensity *= 1.1f;
                break;
                
            case MoodIds.Dramatic:
                palette.primaryWall = Color.Lerp(palette.primaryWall, Color.black, 0.15f);
                palette.secondaryWall = Color.Lerp(palette.secondaryWall, Color.black, 0.2f);
                palette.ceiling = Color.Lerp(palette.ceiling, Color.black, 0.1f);
                palette.trim = Color.Lerp(palette.trim, new Color(0.3f, 0.2f, 0.1f), 0.2f);
                palette.accent = Color.Lerp(palette.accent, new Color(1f, 0.5f, 0.2f), 0.15f);
                palette.emissiveIntensity *= 1.25f;
                break;
                
            case MoodIds.Calm:
            default:
                palette.primaryWall = Color.Lerp(palette.primaryWall, new Color(0.95f, 0.95f, 0.98f), 0.05f);
                palette.emissiveIntensity *= 0.9f;
                break;
        }
        
        palette.emissiveIntensity = Mathf.Max(0f, palette.emissiveIntensity);
    }
    
    private Light FindOrCreateMainLight()
    {
        if (cachedMainLight != null) return cachedMainLight;
        
        Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (var light in lights)
        {
            if (light != null && light.type == LightType.Directional)
            {
                cachedMainLight = light;
                return cachedMainLight;
            }
        }
        
        GameObject lightObj = new GameObject("DirectionalFillLight");
        // Parent to the generated gallery root so the light is cleaned up on reload.
        // All TopologyGenerator subclasses name this "GeneratedGallery".
        Transform galleryRoot = GameObject.Find("GeneratedGallery")?.transform;
        if (galleryRoot != null)
        {
            lightObj.transform.SetParent(galleryRoot);
            lightObj.transform.localPosition = new Vector3(0, 3f, 0);
            lightObj.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        }
        else
        {
            lightObj.transform.position = new Vector3(0, 3f, 0);
            lightObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
        
        Light newLight = lightObj.AddComponent<Light>();
        newLight.type = LightType.Directional;
        newLight.shadows = LightShadows.None;
        
        cachedMainLight = newLight;
        return cachedMainLight;
    }
    
    private static string NormalizeThemeId(string themeId)
    {
        if (string.IsNullOrEmpty(themeId)) return ThemeDefaults.Theme;
        return themeId.ToLowerInvariant();
    }
    
    private static string NormalizeMoodId(string moodId)
    {
        if (string.IsNullOrEmpty(moodId)) return ThemeDefaults.Mood;
        return moodId.ToLowerInvariant();
    }
    
    private static Color GetMaterialColor(Material material, Color fallback)
    {
        if (material == null) return fallback;
        if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
        if (material.HasProperty("_Color")) return material.GetColor("_Color");
        return fallback;
    }
}
