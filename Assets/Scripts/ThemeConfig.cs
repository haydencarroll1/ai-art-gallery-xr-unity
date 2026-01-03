using UnityEngine;

// ============================================================================
// THEME CONFIG - ScriptableObject for Custom Gallery Themes (OPTIONAL)
// ============================================================================
// This is an OPTIONAL way to define themes with asset references.
// The ThemeManager has built-in default themes that work without any assets.
// Use this only if you want to create custom themes with specific materials.
//
// Create new themes via: Assets > Create > Gallery > Theme Config
// Place in Resources/Themes/ folder for auto-loading
// ============================================================================

[CreateAssetMenu(fileName = "NewTheme", menuName = "Gallery/Theme Config")]
public class ThemeConfig : ScriptableObject
{
    [Header("Theme Identity")]
    public string themeName;
    
    [Tooltip("Optional style hint: contemporary, classical, or industrial")]
    public string galleryStyle = "contemporary";
    
    [Header("Materials (Optional)")]
    [Tooltip("If null, uses procedurally generated material with theme colors")]
    public Material floorMaterial;
    public Material wallMaterial;
    public Material ceilingMaterial;
    
    [Header("Prefabs (Optional)")]
    [Tooltip("If null, uses procedurally generated frames")]
    public GameObject framePrefab;
    public GameObject pedestalPrefab;
    public GameObject[] decorationPrefabs;
    
    [Header("Lighting")]
    public Color ambientLightColor = Color.white;
    public float lightIntensity = 1f;
    
    [Header("Layout Settings")]
    public float frameSpacing = 2f;
    public float pedestalSpacing = 3f;
}
