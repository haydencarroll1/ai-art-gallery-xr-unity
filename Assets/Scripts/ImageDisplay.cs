using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

// Displays a single image from a URL onto a renderer (quad or plane).
//
// ArtworkPlacer creates a frame with a quad inside it, then attaches this
// component. When GalleryOrchestrator calls LoadImage(), this downloads the
// texture and applies it to the quad's material.
//
// The renderer can be set three ways:
//   1. Directly via targetRenderer (set by ArtworkPlacer)
//   2. By finding a child called "ImageSurface"
//   3. Fallback: uses the renderer on this GameObject or any child

public class ImageDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [Tooltip("Material property name for the texture (usually _MainTex or _BaseMap)")]
    public string texturePropertyName = "_BaseMap";

    [Header("Renderer Reference")]
    [Tooltip("The renderer to apply textures to. If null, searches children for ImageSurface or uses self.")]
    public Renderer targetRenderer;

    [Header("Placeholder Textures (Optional)")]
    [Tooltip("Shown while loading - leave empty for no placeholder")]
    public Texture2D loadingTexture;

    [Tooltip("Shown if loading fails - leave empty for no placeholder")]
    public Texture2D errorTexture;

    [Header("Debug")]
    public bool debugMode = false;

    private Renderer displayRenderer;
    private MaterialPropertyBlock mpb;
    private string currentUrl;
    private string currentPrompt;
    private bool isLoading = false;
    private Texture2D downloadedTexture;

    public string CurrentPrompt => currentPrompt;
    public bool IsLoading => isLoading;

    void Awake()
    {
        // Lazy init — ArtworkPlacer sets targetRenderer after AddComponent()
    }

    public void InitializeRenderer()
    {
        if (debugMode) Debug.Log($"[ImageDisplay] {gameObject.name}: InitializeRenderer called");

        if (targetRenderer != null)
        {
            displayRenderer = targetRenderer;
            if (debugMode) Debug.Log($"[ImageDisplay] {gameObject.name}: Using pre-assigned targetRenderer");
        }
        else
        {
            Transform surface = transform.Find("ImageSurface");
            if (surface != null)
            {
                displayRenderer = surface.GetComponent<Renderer>();
            }

            if (displayRenderer == null)
            {
                displayRenderer = GetComponent<Renderer>();
            }

            if (displayRenderer == null)
            {
                displayRenderer = GetComponentInChildren<Renderer>();
            }
        }

        if (displayRenderer == null)
        {
            Debug.LogError($"[ImageDisplay] {gameObject.name}: NO RENDERER FOUND - image won't display!");
            return;
        }

        Material sharedMat = displayRenderer.sharedMaterial;
        if (sharedMat == null)
        {
            Debug.LogError($"[ImageDisplay] {gameObject.name}: Renderer has NO MATERIAL!");
            return;
        }

        // URP uses _BaseMap, Standard uses _MainTex
        if (!sharedMat.HasProperty(texturePropertyName))
        {
            if (sharedMat.HasProperty("_BaseMap"))
            {
                texturePropertyName = "_BaseMap";
            }
            else if (sharedMat.HasProperty("_MainTex"))
            {
                texturePropertyName = "_MainTex";
            }
            else
            {
                Debug.LogError($"[ImageDisplay] {gameObject.name}: Material has NO texture property we recognize!");
            }
        }

        // Use MaterialPropertyBlock for per-renderer texture overrides
        // instead of cloning materials. This preserves SRP batching.
        mpb = new MaterialPropertyBlock();

        if (Application.isPlaying && loadingTexture != null)
        {
            mpb.SetTexture(texturePropertyName, loadingTexture);
            displayRenderer.SetPropertyBlock(mpb);
        }
    }

    public void LoadImage(string url, string prompt, System.Action<bool> onComplete = null)
    {
        if (debugMode) Debug.Log($"[ImageDisplay] {gameObject.name}: LoadImage called with URL: {url?.Substring(0, Mathf.Min(50, url?.Length ?? 0))}...");

        if (string.IsNullOrEmpty(url))
        {
            Debug.LogWarning($"[ImageDisplay] {gameObject.name}: Empty URL provided");
            ShowError();
            onComplete?.Invoke(false);
            return;
        }

        if (displayRenderer == null || mpb == null)
        {
            InitializeRenderer();
        }

        if (mpb == null)
        {
            Debug.LogError($"[ImageDisplay] {gameObject.name}: FAILED - No renderer after initialization!");
            onComplete?.Invoke(false);
            return;
        }

        currentUrl = url;
        currentPrompt = prompt;

        StartCoroutine(LoadImageCoroutine(url, onComplete));
    }

    private IEnumerator LoadImageCoroutine(string url, System.Action<bool> onComplete)
    {
        isLoading = true;

        if (loadingTexture != null && mpb != null)
        {
            mpb.SetTexture(texturePropertyName, loadingTexture);
            displayRenderer.SetPropertyBlock(mpb);
        }

        if (debugMode) Debug.Log($"[ImageDisplay] {gameObject.name}: Starting download from {url}");

        using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(url))
        {
            yield return request.SendWebRequest();

            if (this == null || displayRenderer == null)
            {
                isLoading = false;
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ImageDisplay] {gameObject.name}: Download FAILED - {request.error}");
                ShowError();
                isLoading = false;
                onComplete?.Invoke(false);
                yield break;
            }

            Texture2D texture = DownloadHandlerTexture.GetContent(request);

            if (texture == null)
            {
                Debug.LogError($"[ImageDisplay] {gameObject.name}: Downloaded texture is NULL");
                ShowError();
                isLoading = false;
                onComplete?.Invoke(false);
                yield break;
            }

            if (debugMode) Debug.Log($"[ImageDisplay] {gameObject.name}: Download SUCCESS - {texture.width}x{texture.height}");

            if (downloadedTexture != null)
            {
                Destroy(downloadedTexture);
            }
            downloadedTexture = texture;

            mpb.SetTexture(texturePropertyName, texture);
            displayRenderer.SetPropertyBlock(mpb);

            isLoading = false;
            onComplete?.Invoke(true);
        }
    }

    private void ShowError()
    {
        if (errorTexture != null && mpb != null && displayRenderer != null)
        {
            mpb.SetTexture(texturePropertyName, errorTexture);
            displayRenderer.SetPropertyBlock(mpb);
        }
    }

    private void OnDestroy()
    {
        if (downloadedTexture != null)
        {
            Destroy(downloadedTexture);
            downloadedTexture = null;
        }
    }
}
