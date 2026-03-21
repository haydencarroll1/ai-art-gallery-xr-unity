using UnityEngine;
using UnityEngine.Networking;
using GLTFast;
using System.Collections;

// Displays a 3D model from a GLB URL on top of a pedestal.
//
// ArtworkPlacer creates a pedestal with a SculptureAnchor transform on top,
// then attaches this component. When GalleryOrchestrator calls LoadSculpture(),
// this downloads the GLB file, parses it with glTFast, scales it to fit
// targetSize, and centers it on the anchor point.

public class SculptureDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [Tooltip("Scale the model to fit within this size (in units)")]
    public float targetSize = 1f;

    [Tooltip("Center the model at this transform's position")]
    public bool centerModel = true;

    [Header("Position Reference")]
    [Tooltip("Where to place the sculpture. If null, uses this transform.")]
    public Transform sculptureAnchor;

    [Header("Placeholder (Optional)")]
    [Tooltip("Object to show while loading - will be hidden when model loads")]
    public GameObject loadingPlaceholder;

    [Tooltip("Object to show if loading fails")]
    public GameObject errorPlaceholder;

    [Header("Debug")]
    public bool debugMode = false;

    // the currently loaded model root
    private GameObject currentModel;

    // Fix: store GltfImport so it can be disposed — it holds native mesh/texture memory
    private GltfImport _gltfImport;

    // current state
    private string currentUrl;
    private string currentPrompt;
    private bool isLoading = false;

    public string CurrentPrompt => currentPrompt;
    public bool IsLoading => isLoading;
    public GameObject CurrentModel => currentModel;

    void Awake()
    {
        SetPlaceholderState(loading: true, error: false);
    }

    // Downloads a GLB file from the given URL, parses it with glTFast,
    // scales it to fit targetSize, and parents it to the sculpture anchor.
    public void LoadSculpture(string url, string prompt, System.Action<bool> onComplete = null)
    {
        if (string.IsNullOrEmpty(url))
        {
            Debug.LogWarning($"SculptureDisplay on {gameObject.name}: Empty URL provided");
            SetPlaceholderState(loading: false, error: true);
            onComplete?.Invoke(false);
            return;
        }

        currentUrl = url;
        currentPrompt = prompt;

        StartCoroutine(LoadSculptureCoroutine(url, onComplete));
    }

    private IEnumerator LoadSculptureCoroutine(string url, System.Action<bool> onComplete)
    {
        isLoading = true;
        SetPlaceholderState(loading: true, error: false);

        // Clean up any previously loaded model
        if (currentModel != null)
        {
            Destroy(currentModel);
            currentModel = null;
        }

        if (debugMode) Debug.Log($"[SculptureDisplay] {gameObject.name}: Starting download from {url}");

        // Step 1: Download the raw GLB bytes
        byte[] glbData = null;

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 30;
            yield return request.SendWebRequest();

            // Guard against the GameObject being destroyed during the download
            if (this == null)
            {
                isLoading = false;
                yield break;
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"SculptureDisplay on {gameObject.name}: Download failed - {request.error}");
                SetPlaceholderState(loading: false, error: true);
                isLoading = false;
                onComplete?.Invoke(false);
                yield break;
            }

            glbData = request.downloadHandler.data;

            if (debugMode) Debug.Log($"SculptureDisplay on {gameObject.name}: Downloaded {glbData.Length / 1024}KB");
        }

        // Step 2: Parse the GLB with glTFast (handles all mesh/material setup)
        // Dispose previous import to free native mesh/texture memory
        _gltfImport?.Dispose();
        _gltfImport = new GltfImport();

        var loadTask = _gltfImport.Load(glbData);
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadTask.IsFaulted || !loadTask.Result)
        {
            Debug.LogError($"SculptureDisplay on {gameObject.name}: GLB parse failed");
            SetPlaceholderState(loading: false, error: true);
            isLoading = false;
            onComplete?.Invoke(false);
            yield break;
        }

        // Step 3: Instantiate the model into a container parented to the anchor
        currentModel = new GameObject("LoadedSculpture");

        Transform parentTransform = sculptureAnchor != null ? sculptureAnchor : transform;
        currentModel.transform.SetParent(parentTransform);
        currentModel.transform.localPosition = Vector3.zero;
        currentModel.transform.localRotation = Quaternion.identity;
        currentModel.transform.localScale = Vector3.one;

        var instantiateTask = _gltfImport.InstantiateSceneAsync(currentModel.transform);
        yield return new WaitUntil(() => instantiateTask.IsCompleted);

        if (instantiateTask.IsFaulted || !instantiateTask.Result)
        {
            Debug.LogError($"SculptureDisplay on {gameObject.name}: Failed to instantiate model");
            Destroy(currentModel);
            currentModel = null;
            SetPlaceholderState(loading: false, error: true);
            isLoading = false;
            onComplete?.Invoke(false);
            yield break;
        }

        // Step 4: Scale and center so it sits nicely on the pedestal
        NormalizeModel(currentModel);

        SetPlaceholderState(loading: false, error: false);

        if (debugMode) Debug.Log($"[SculptureDisplay] {gameObject.name}: Model loaded and positioned at {currentModel.transform.position}");

        isLoading = false;
        onComplete?.Invoke(true);
    }

    // Scales the model uniformly so its largest dimension equals targetSize,
    // then positions it so the bottom of the model sits on the anchor point
    // and it's centered horizontally.
    private void NormalizeModel(GameObject model)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
        {
            Debug.LogWarning($"SculptureDisplay on {gameObject.name}: No renderers in loaded model");
            return;
        }

        // Combine all renderer bounds to get the total bounding box
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer r in renderers)
        {
            bounds.Encapsulate(r.bounds);
        }

        float maxDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);

        if (maxDimension <= 0)
        {
            Debug.LogWarning($"SculptureDisplay on {gameObject.name}: Model has zero size");
            return;
        }

        // Scale so the biggest axis matches targetSize
        float scale = targetSize / maxDimension;
        model.transform.localScale = Vector3.one * scale;

        if (debugMode) Debug.Log($"SculptureDisplay on {gameObject.name}: Scaled to {scale:F2}x (original size: {maxDimension:F2})");

        if (centerModel)
        {
            // Recalculate bounds after scaling
            bounds = renderers[0].bounds;
            foreach (Renderer r in renderers)
            {
                bounds.Encapsulate(r.bounds);
            }

            Transform targetTransform = sculptureAnchor != null ? sculptureAnchor : transform;

            // Position the model so its bottom sits on the anchor and it's
            // centered on X and Z
            Vector3 targetPos = targetTransform.position;
            Vector3 offset = new Vector3(
                targetPos.x - bounds.center.x,
                targetPos.y - bounds.min.y,
                targetPos.z - bounds.center.z
            );
            model.transform.position += offset;

            if (debugMode) Debug.Log($"SculptureDisplay on {gameObject.name}: Centered at {targetPos}, offset by {offset}");
        }
    }

    private void SetPlaceholderState(bool loading, bool error)
    {
        if (loadingPlaceholder != null)
        {
            loadingPlaceholder.SetActive(loading);
        }

        if (errorPlaceholder != null)
        {
            errorPlaceholder.SetActive(error);
        }
    }
    // Fix: dispose GltfImport on destroy to free native mesh/texture memory
    private void OnDestroy()
    {
        _gltfImport?.Dispose();
        _gltfImport = null;
    }
}
