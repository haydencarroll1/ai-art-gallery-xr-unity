using UnityEngine;
using UnityEngine.Rendering;

// Applies gallery-wide lighting and atmosphere based on the gallery_style
// from the manifest. Three styles: clean, warm, dark.

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

public class ThemeManager : MonoBehaviour
{
    private static ThemeManager _instance;

    public static ThemeManager Instance
    {
        get
        {
            if (_instance != null) return _instance;

            _instance = FindFirstObjectByType<ThemeManager>();

            if (_instance == null)
            {
                GameObject go = new GameObject("ThemeManager (Auto-Created)");
                _instance = go.AddComponent<ThemeManager>();
                DontDestroyOnLoad(go);
            }

            return _instance;
        }
    }

    private static readonly ThemePalette CleanPalette = new ThemePalette
    {
        primaryWall = new Color(0.95f, 0.95f, 0.95f),
        secondaryWall = new Color(0.9f, 0.9f, 0.9f),
        floor = new Color(0.3f, 0.3f, 0.3f),
        ceiling = Color.white,
        trim = new Color(0.1f, 0.1f, 0.1f),
        accent = new Color(0.2f, 0.2f, 0.2f),
        emissive = Color.white,
        emissiveIntensity = 0.2f,
        lightColor = Color.white,
        lightIntensity = 1.4f
    };

    private static readonly ThemePalette WarmPalette = new ThemePalette
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
    };

    private static readonly ThemePalette DarkPalette = new ThemePalette
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
    };

    [SerializeField] private string currentStyle = GalleryStyleIds.Clean;

    private ThemePalette activePalette;
    private Light cachedMainLight;

    public string CurrentStyle => currentStyle;
    public ThemePalette GetPalette() => activePalette ?? CleanPalette;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;

        if (transform.parent != null)
        {
            transform.SetParent(null);
        }
        DontDestroyOnLoad(gameObject);

        activePalette = CleanPalette.Clone();
    }

    // Sets the gallery style and applies all lighting/atmosphere in one shot.
    public ThemePalette SetStyle(string galleryStyle)
    {
        currentStyle = NormalizeStyle(galleryStyle);

        switch (currentStyle)
        {
            case GalleryStyleIds.Warm:
                activePalette = WarmPalette.Clone();
                break;
            case GalleryStyleIds.Dark:
                activePalette = DarkPalette.Clone();
                break;
            case GalleryStyleIds.Clean:
            default:
                activePalette = CleanPalette.Clone();
                break;
        }

        ApplyLightingAndAtmosphere();

        return activePalette;
    }

    private void ApplyLightingAndAtmosphere()
    {
        Light light = FindOrCreateMainLight();
        if (light == null) return;

        light.color = activePalette.lightColor;
        light.intensity = activePalette.lightIntensity;
        light.shadows = LightShadows.None;

        RenderSettings.ambientMode = AmbientMode.Flat;

        switch (currentStyle)
        {
            case GalleryStyleIds.Clean:
                RenderSettings.ambientIntensity = 0.8f;
                RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f);
                RenderSettings.fog = false;
                break;

            case GalleryStyleIds.Warm:
                RenderSettings.ambientIntensity = 0.4f;
                RenderSettings.ambientLight = new Color(0.45f, 0.35f, 0.25f);
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogColor = new Color(0.85f, 0.75f, 0.6f);
                RenderSettings.fogDensity = 0.008f;
                break;

            case GalleryStyleIds.Dark:
                RenderSettings.ambientIntensity = 0.15f;
                RenderSettings.ambientLight = new Color(0.1f, 0.08f, 0.06f);
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogColor = new Color(0.05f, 0.05f, 0.08f);
                RenderSettings.fogDensity = 0.025f;
                break;
        }
    }

    private Light FindOrCreateMainLight()
    {
        if (cachedMainLight != null) return cachedMainLight;

        Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (var l in lights)
        {
            if (l != null && l.type == LightType.Directional)
            {
                cachedMainLight = l;
                return cachedMainLight;
            }
        }

        GameObject lightObj = new GameObject("DirectionalFillLight");
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

    private static string NormalizeStyle(string style)
    {
        if (string.IsNullOrEmpty(style)) return GalleryStyleIds.Clean;
        string normalized = style.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "clean":
            case "contemporary":
            case "modern":
            case "minimalist":
            case "white_cube":
            case "whitecube":
            case "moma":
            case "cool":
            case "default":
                return GalleryStyleIds.Clean;

            case "warm":
            case "classical":
            case "renaissance":
            case "asian":
            case "nature":
            case "desert":
            case "medieval":
                return GalleryStyleIds.Warm;

            case "dark":
            case "industrial":
            case "gothic":
            case "futuristic":
            case "neon":
            case "ethereal":
            case "dramatic":
                return GalleryStyleIds.Dark;

            default:
                return GalleryStyleIds.Clean;
        }
    }
}
