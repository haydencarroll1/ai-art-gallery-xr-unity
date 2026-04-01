using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;

// Main controller that coordinates the entire gallery loading pipeline.
// It runs five steps in order:
//   1. Load the manifest (from URL, local file, or embedded test data)
//   2. Apply the theme (colors, mood lighting, fog)
//   3. Pick the right topology generator and build the room geometry
//   4. Place frames and pedestals via ArtworkPlacer
//   5. Download and display all artwork assets
//
// To use: add to a GameObject, configure manifest source, call LoadGallery()
// or set loadOnStart = true.

public class GalleryOrchestrator : MonoBehaviour
{
    [Header("Manifest Source")]
    [Tooltip("Load manifest from remote URL")]
    public bool loadFromUrl = true;

    [Tooltip("API base URL (for remote loading)")]
    public string apiBaseUrl = "https://hayden-gallery.workers.dev";

    [Tooltip("Local test file in StreamingAssets (for testing)")]
    public string localTestFile = "test_manifest.json";

    [Tooltip("Load gallery automatically on Start")]
    public bool loadOnStart = true;

    [Header("Optional UI")]
    [Tooltip("Text to show loading status")]
    public TMPro.TextMeshProUGUI statusText;

    [Tooltip("Text to show errors")]
    public TMPro.TextMeshProUGUI errorText;

    [Header("Events")]
    public UnityEvent onLoadStarted;
    public UnityEvent onLoadComplete;
    public UnityEvent<string> onLoadFailed;

    [Header("Debug")]
    public bool debugMode = false;

    [Tooltip("Use embedded test manifest instead of loading from file/URL")]
    public bool useEmbeddedTestManifest = false;

    [Header("Player Positioning")]
    [Tooltip("Reference to the XR Origin (will auto-find if not set)")]
    public XROrigin xrOrigin;

    [Tooltip("How far into the corridor to spawn the player (meters from entry)")]
    public float playerSpawnDistance = 1.5f;

    [Tooltip("Player height offset from floor (0 = at floor level, XR handles actual height)")]
    public float playerHeightOffset = 0f;

    private ManifestLoader manifestLoader;
    private TopologyGenerator activeGenerator;
    private ArtworkPlacer artworkPlacer;
    private Camera fallbackCamera;
    private GalleryManifest currentManifest;
    private GalleryLoadState loadState = GalleryLoadState.Idle;
    private string lastError;

    public GalleryManifest CurrentManifest => currentManifest;
    public GalleryLoadState LoadState => loadState;
    public string LastError => lastError;

    void Awake()
    {
        // Make sure ManifestLoader and ArtworkPlacer exist on this GameObject.
        // If they're not already attached, add them automatically so the
        // orchestrator works without any manual Inspector setup.
        manifestLoader = GetComponent<ManifestLoader>();
        if (manifestLoader == null)
        {
            manifestLoader = gameObject.AddComponent<ManifestLoader>();
        }

        artworkPlacer = GetComponent<ArtworkPlacer>();
        if (artworkPlacer == null)
        {
            artworkPlacer = gameObject.AddComponent<ArtworkPlacer>();
        }

        // When XR is not active (e.g. Mac desktop build), the XR Origin camera
        // won't render. Create a simple fallback camera so the scene is visible.
        // Use XRGeneralSettings to check if an XR loader actually initialized,
        // since XRSettings.isDeviceActive (legacy API) can be unreliable.
        bool xrActive = false;
        var xrSettings = XRGeneralSettings.Instance;
        if (xrSettings != null && xrSettings.Manager != null && xrSettings.Manager.activeLoader != null)
        {
            xrActive = true;
        }

        if (!xrActive)
        {
            SetupFallbackCamera();
        }
    }

    void Start()
    {
        if (loadOnStart)
        {
            LoadGallery();
        }
    }

    public void LoadGallery()
    {
        if (loadState != GalleryLoadState.Idle && loadState != GalleryLoadState.Complete && loadState != GalleryLoadState.Failed)
        {
            Debug.LogWarning("[GalleryOrchestrator] Load already in progress");
            return;
        }

        if (useEmbeddedTestManifest)
        {
            LoadFromEmbeddedTestManifest();
        }
        else if (loadFromUrl)
        {
            LoadFromApi();
        }
        else
        {
            LoadFromLocalFile();
        }
    }

    public void LoadFromApi()
    {
        string url = $"{apiBaseUrl}/api/gallery";
        StartCoroutine(LoadGalleryCoroutine(url, isUrl: true));
    }

    public void LoadFromLocalFile()
    {
        StartCoroutine(LoadGalleryCoroutine(localTestFile, isUrl: false));
    }

    public void LoadFromEmbeddedTestManifest()
    {
        StartCoroutine(LoadFromEmbeddedCoroutine());
    }

    public void Reload()
    {
        ClearGallery();
        LoadGallery();
    }

    public void ClearGallery()
    {
        // Stop any in-flight download coroutines so callbacks don't fire on
        // destroyed objects after a reload.
        StopAllCoroutines();

        if (activeGenerator != null)
        {
            activeGenerator.ClearGenerated();
        }

        if (artworkPlacer != null)
        {
            artworkPlacer.ClearAllArtwork();
        }

        currentManifest = null;
        loadState = GalleryLoadState.Idle;
    }

    private IEnumerator LoadGalleryCoroutine(string source, bool isUrl)
    {
        loadState = GalleryLoadState.LoadingManifest;
        lastError = null;

        UpdateStatus("Loading gallery manifest...");
        onLoadStarted?.Invoke();

        if (debugMode) Debug.Log($"[GalleryOrchestrator] Loading from: {source}");

        ManifestLoader.LoadResult result = null;
        bool loadComplete = false;

        if (isUrl)
        {
            manifestLoader.LoadFromUrl(source, r => { result = r; loadComplete = true; });
        }
        else
        {
            manifestLoader.LoadFromStreamingAssets(source, r => { result = r; loadComplete = true; });
        }

        while (!loadComplete)
        {
            yield return null;
        }

        if (!result.success)
        {
            HandleError(result.error);
            yield break;
        }

        currentManifest = result.manifest;

        yield return StartCoroutine(ContinueLoadingFromManifest());
    }

    private IEnumerator LoadFromEmbeddedCoroutine()
    {
        loadState = GalleryLoadState.LoadingManifest;
        lastError = null;

        UpdateStatus("Loading test manifest...");
        onLoadStarted?.Invoke();

        ManifestLoader.LoadResult result = manifestLoader.ParseManifestJson(GetTestManifestJson());

        if (!result.success)
        {
            HandleError(result.error);
            yield break;
        }

        currentManifest = result.manifest;

        yield return StartCoroutine(ContinueLoadingFromManifest());
    }

    // Steps 2-5 of the pipeline. Runs after the manifest is successfully parsed.
    private IEnumerator ContinueLoadingFromManifest()
    {
        if (debugMode)
        {
            Debug.Log($"[GalleryOrchestrator] Manifest loaded: {currentManifest.GetGalleryId()}");
            Debug.Log($"[GalleryOrchestrator] Topology: {currentManifest.GetTopology()}");
        }

        loadState = GalleryLoadState.ApplyingTheme;
        UpdateStatus("Applying style...");

        string style = currentManifest.GetGalleryStyle();
        ThemeManager.Instance?.SetStyle(style);

        yield return null;

        loadState = GalleryLoadState.GeneratingGeometry;
        UpdateStatus("Generating gallery...");

        // The orchestrator should sit at world origin so that wall positions
        // line up with the placement calculations in TopologyGenerator.
        if (debugMode)
        {
            Debug.Log($"[GalleryOrchestrator] Orchestrator world position: {transform.position}");
            if (transform.position != Vector3.zero)
            {
                Debug.LogWarning("[GalleryOrchestrator] WARNING: Orchestrator is not at world origin! This may cause alignment issues.");
            }
        }

        if (!SetupTopologyGenerator())
        {
            HandleError($"Unsupported topology: {currentManifest.GetTopology()}");
            yield break;
        }

        activeGenerator.SetManifestContext(currentManifest);
        activeGenerator.Generate(currentManifest.locked_constraints, currentManifest.GetLayoutPlanWrapper());

        PositionPlayerAtSpawn();

        yield return null;

        loadState = GalleryLoadState.PlacingArtwork;
        UpdateStatus("Placing artwork...");

        artworkPlacer.PlaceAllArtwork(currentManifest, activeGenerator);

        yield return null;

        loadState = GalleryLoadState.LoadingAssets;
        UpdateStatus("Loading artwork...");

        yield return StartCoroutine(LoadAllArtworkAssets());

        loadState = GalleryLoadState.Complete;
        UpdateStatus("Gallery loaded!");
        ClearError();

        onLoadComplete?.Invoke();

        if (debugMode) Debug.Log("[GalleryOrchestrator] Gallery loading complete!");
    }

    private void SetupFallbackCamera()
    {
        var xrCam = FindFirstObjectByType<XROrigin>();
        if (xrCam != null)
        {
            var cam = xrCam.GetComponentInChildren<Camera>();
            if (cam != null) cam.enabled = false;
        }

        var camObj = new GameObject("FallbackCamera");
        fallbackCamera = camObj.AddComponent<Camera>();
        fallbackCamera.tag = "MainCamera";
        fallbackCamera.clearFlags = CameraClearFlags.Skybox;
        fallbackCamera.nearClipPlane = 0.01f;
        fallbackCamera.farClipPlane = 1000f;
        fallbackCamera.fieldOfView = 70f;
        camObj.AddComponent<FallbackCameraController>();
        // Start at a reasonable eye-height position; PositionPlayerAtSpawn will reposition
        camObj.transform.position = new Vector3(0f, 1.6f, 1.5f);

        if (debugMode) Debug.Log("[GalleryOrchestrator] Created fallback camera (no XR device detected)");
    }

    private void PositionPlayerAtSpawn()
    {
        Vector3 spawnPosition = new Vector3(0f, 1.6f, 1.5f);

        if (activeGenerator != null)
        {
            var rooms = activeGenerator.GetGeneratedRooms();
            if (rooms != null && rooms.Count > 0)
            {
                // Pick the first room deterministically by sorting keys.
                string firstKey = null;
                foreach (var key in rooms.Keys)
                {
                    if (firstKey == null || string.Compare(key, firstKey, System.StringComparison.Ordinal) < 0)
                        firstKey = key;
                }
                var room = rooms[firstKey];
                float roomBackZ = room.center.z - room.dimensions.length / 2f;
                spawnPosition = new Vector3(
                    room.center.x,
                    room.floorY + 1.6f + playerHeightOffset,
                    roomBackZ + playerSpawnDistance
                );
            }
        }

        if (fallbackCamera != null)
        {
            fallbackCamera.transform.position = spawnPosition;
        }
        else
        {
            if (xrOrigin == null)
            {
                xrOrigin = FindFirstObjectByType<XROrigin>();
            }
            if (xrOrigin != null)
            {
                spawnPosition.y = spawnPosition.y - 1.6f + playerHeightOffset;
                xrOrigin.transform.position = spawnPosition;
            }
            else
            {
                Debug.LogWarning("[GalleryOrchestrator] No XR Origin or fallback camera found!");
            }
        }

        if (debugMode)
        {
            Debug.Log($"[GalleryOrchestrator] Positioned player at: {spawnPosition}");
        }
    }

    private bool SetupTopologyGenerator()
    {
        string topology = currentManifest.GetTopology();

        if (activeGenerator != null)
        {
            activeGenerator.ClearGenerated();
        }

        switch (topology)
        {
            case TopologyTypes.LinearCorridor:
                activeGenerator = GetOrAddComponent<LinearCorridorGenerator>();
                break;
            case TopologyTypes.LinearWithAlcoves:
                activeGenerator = GetOrAddComponent<LinearWithAlcovesGenerator>();
                break;
            case TopologyTypes.BranchingRooms:
                activeGenerator = GetOrAddComponent<BranchingRoomsGenerator>();
                break;
            case TopologyTypes.HubAndSpoke:
                activeGenerator = GetOrAddComponent<HubAndSpokeGenerator>();
                break;
            case TopologyTypes.OpenHall:
                activeGenerator = GetOrAddComponent<OpenHallGenerator>();
                break;

            default:
                Debug.LogError($"[GalleryOrchestrator] Unsupported topology: {topology}");
                return false;
        }

        if (debugMode) Debug.Log($"[GalleryOrchestrator] Using generator: {activeGenerator.GetType().Name}");

        return true;
    }

    private T GetOrAddComponent<T>() where T : Component
    {
        T component = GetComponent<T>();
        if (component == null)
        {
            component = gameObject.AddComponent<T>();
        }
        return component;
    }

    private IEnumerator LoadAllArtworkAssets()
    {
        var imageDisplays = artworkPlacer.ImageDisplays;
        var sculptureDisplays = artworkPlacer.SculptureDisplays;

        int totalAssets = imageDisplays.Count + sculptureDisplays.Count;
        int loadedCount = 0;
        int pendingLoads = totalAssets;

        if (debugMode) Debug.Log($"[GalleryOrchestrator] Loading {totalAssets} assets...");

        for (int i = 0; i < imageDisplays.Count; i++)
        {
            ImageDisplay display = imageDisplays[i];
            if (display == null) continue;

            string assetId = display.gameObject.name;
            ArtworkAsset asset = currentManifest.GetAssetById(assetId);

            if (asset == null || string.IsNullOrEmpty(asset.url))
            {
                Debug.LogWarning($"[GalleryOrchestrator] No URL for asset: {assetId}");
                pendingLoads--;
                continue;
            }

            display.LoadImage(asset.url, asset.prompt, (success) =>
            {
                pendingLoads--;
                if (success) loadedCount++;
            });
        }

        for (int i = 0; i < sculptureDisplays.Count; i++)
        {
            SculptureDisplay display = sculptureDisplays[i];
            if (display == null) continue;

            string objName = display.gameObject.name;
            string assetId = objName.StartsWith("Pedestal_") ? objName.Substring(9) : objName;
            ArtworkAsset asset = currentManifest.GetAssetById(assetId);

            if (asset == null || string.IsNullOrEmpty(asset.url))
            {
                Debug.LogWarning($"[GalleryOrchestrator] No URL for sculpture: {assetId}");
                pendingLoads--;
                continue;
            }

            display.LoadSculpture(asset.url, asset.prompt, (success) =>
            {
                pendingLoads--;
                if (success) loadedCount++;
            });
        }

        while (pendingLoads > 0)
        {
            UpdateStatus($"Loading artwork... ({totalAssets - pendingLoads}/{totalAssets})");
            yield return new WaitForSeconds(0.1f);
        }

        if (debugMode)
        {
            Debug.Log($"[GalleryOrchestrator] Loaded {loadedCount}/{totalAssets} assets");
        }
    }

    private void HandleError(string error)
    {
        lastError = error;
        loadState = GalleryLoadState.Failed;

        Debug.LogError($"[GalleryOrchestrator] {error}");

        UpdateStatus("Failed to load gallery");
        UpdateError(error);

        onLoadFailed?.Invoke(error);
    }

    private void UpdateStatus(string text)
    {
        if (statusText != null)
        {
            statusText.text = text;
        }
    }

    private void UpdateError(string text)
    {
        if (errorText != null)
        {
            errorText.text = text;
            errorText.gameObject.SetActive(true);
        }
    }

    private void ClearError()
    {
        if (errorText != null)
        {
            errorText.text = "";
            errorText.gameObject.SetActive(false);
        }
    }

    private string GetTestManifestJson()
    {
        return @"{
  ""gallery_id"": ""test_linear_001"",
  ""schema_version"": ""2.0"",
  ""created_at"": ""2025-01-29T12:00:00Z"",
  ""locked_constraints"": {
    ""topology"": ""linear_corridor"",
    ""gallery_style"": ""clean"",
    ""rooms"": [
      { ""id"": ""main"", ""type"": ""corridor"", ""content_type"": ""2d"", ""content_count"": 6 }
    ]
  },
  ""derived_parameters"": {
    ""pacing"": 0.5,
    ""target_spacing_m"": 2.5
  },
  ""layout_plan"": [
    { ""room_id"": ""main"", ""data"": { ""length"": 15.0, ""width"": 5.0, ""height"": 2.8 } }
  ],
  ""placement_plan"": [
    { ""asset_id"": ""img_001"", ""room_id"": ""main"", ""wall"": ""left"", ""position_along_wall"": 2.5, ""height"": 1.5, ""is_hero"": false },
    { ""asset_id"": ""img_002"", ""room_id"": ""main"", ""wall"": ""left"", ""position_along_wall"": 7.5, ""height"": 1.5, ""is_hero"": true },
    { ""asset_id"": ""img_003"", ""room_id"": ""main"", ""wall"": ""left"", ""position_along_wall"": 12.5, ""height"": 1.5, ""is_hero"": false },
    { ""asset_id"": ""img_004"", ""room_id"": ""main"", ""wall"": ""right"", ""position_along_wall"": 2.5, ""height"": 1.5, ""is_hero"": false },
    { ""asset_id"": ""img_005"", ""room_id"": ""main"", ""wall"": ""right"", ""position_along_wall"": 7.5, ""height"": 1.5, ""is_hero"": false },
    { ""asset_id"": ""img_006"", ""room_id"": ""main"", ""wall"": ""right"", ""position_along_wall"": 12.5, ""height"": 1.5, ""is_hero"": false }
  ],
  ""assets"": [
    { ""id"": ""img_001"", ""url"": ""https://picsum.photos/seed/art1/800/600"", ""type"": ""2d"", ""width"": 1.2, ""height"": 0.9, ""visual_weight"": 0.5, ""prompt"": ""Abstract landscape"" },
    { ""id"": ""img_002"", ""url"": ""https://picsum.photos/seed/art2/600/800"", ""type"": ""2d"", ""width"": 0.9, ""height"": 1.2, ""visual_weight"": 0.8, ""prompt"": ""Portrait study"" },
    { ""id"": ""img_003"", ""url"": ""https://picsum.photos/seed/art3/800/800"", ""type"": ""2d"", ""width"": 1.0, ""height"": 1.0, ""visual_weight"": 0.6, ""prompt"": ""Nature scene"" },
    { ""id"": ""img_004"", ""url"": ""https://picsum.photos/seed/art4/800/600"", ""type"": ""2d"", ""width"": 1.2, ""height"": 0.9, ""visual_weight"": 0.5, ""prompt"": ""Urban exploration"" },
    { ""id"": ""img_005"", ""url"": ""https://picsum.photos/seed/art5/600/800"", ""type"": ""2d"", ""width"": 0.9, ""height"": 1.2, ""visual_weight"": 0.7, ""prompt"": ""Digital art"" },
    { ""id"": ""img_006"", ""url"": ""https://picsum.photos/seed/art6/800/800"", ""type"": ""2d"", ""width"": 1.0, ""height"": 1.0, ""visual_weight"": 0.5, ""prompt"": ""Surreal composition"" }
  ]
}";
    }
}

public enum GalleryLoadState
{
    Idle,
    LoadingManifest,
    ApplyingTheme,
    GeneratingGeometry,
    PlacingArtwork,
    LoadingAssets,
    Complete,
    Failed
}
