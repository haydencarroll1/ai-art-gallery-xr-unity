using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

// Handles loading gallery manifests from three sources:
//   - Remote URL (production: Cloudflare Worker API)
//   - Local JSON file (testing: StreamingAssets folder)
//   - Direct JSON string (unit testing / embedded test data)
//
// Uses Unity's JsonUtility for parsing. The backend sends layout_plan as a
// JSON array of RoomLayoutEntry objects, which JsonUtility handles natively.

public class ManifestLoader : MonoBehaviour
{
    [Header("Loading Options")]
    [Tooltip("Timeout for network requests in seconds")]
    public float networkTimeout = 30f;

    [Header("Debug")]
    public bool debugMode = false;

    // Wraps the result of a load operation: either a parsed manifest or an error string.
    public class LoadResult
    {
        public bool success;
        public GalleryManifest manifest;
        public string error;

        public static LoadResult Success(GalleryManifest manifest)
        {
            return new LoadResult { success = true, manifest = manifest };
        }

        public static LoadResult Failure(string error)
        {
            return new LoadResult { success = false, error = error };
        }
    }

    // Starts a coroutine to fetch the manifest from a URL and calls onComplete when done.
    public void LoadFromUrl(string url, Action<LoadResult> onComplete)
    {
        StartCoroutine(LoadFromUrlCoroutine(url, onComplete));
    }

    // Downloads JSON from a URL, parses it into a GalleryManifest, and returns the result.
    public IEnumerator LoadFromUrlCoroutine(string url, Action<LoadResult> onComplete)
    {
        if (string.IsNullOrEmpty(url))
        {
            onComplete?.Invoke(LoadResult.Failure("URL is empty"));
            yield break;
        }

        if (debugMode) Debug.Log($"[ManifestLoader] Fetching from: {url}");

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = (int)networkTimeout;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string error = $"Network error: {request.error}";
                Debug.LogError($"[ManifestLoader] {error}");
                onComplete?.Invoke(LoadResult.Failure(error));
                yield break;
            }

            string json = request.downloadHandler.text;

            if (debugMode)
            {
                Debug.Log($"[ManifestLoader] Received {json.Length} bytes");
                Debug.Log($"[ManifestLoader] Preview: {json.Substring(0, Mathf.Min(200, json.Length))}...");
            }

            var result = ParseManifestJson(json);
            onComplete?.Invoke(result);
        }
    }

    // Loads a manifest from the StreamingAssets folder. On Android/WebGL this
    // still uses UnityWebRequest because those platforms don't support direct
    // file access to StreamingAssets.
    public void LoadFromStreamingAssets(string filename, Action<LoadResult> onComplete)
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, filename);
        StartCoroutine(LoadFromLocalPathCoroutine(path, onComplete));
    }

    private IEnumerator LoadFromLocalPathCoroutine(string path, Action<LoadResult> onComplete)
    {
        if (debugMode) Debug.Log($"[ManifestLoader] Loading from: {path}");

        // On Android and WebGL, StreamingAssets is inside the APK/bundle
        // so we have to use UnityWebRequest even for "local" files.
        #if UNITY_ANDROID || UNITY_WEBGL
        string url = path;
        if (!path.StartsWith("file://") && !path.StartsWith("http"))
        {
            url = "file://" + path;
        }

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onComplete?.Invoke(LoadResult.Failure($"Failed to load file: {request.error}"));
                yield break;
            }

            string json = request.downloadHandler.text;
            var result = ParseManifestJson(json);
            onComplete?.Invoke(result);
        }
        #else
        // On Editor and Standalone we can just read the file directly
        try
        {
            if (!System.IO.File.Exists(path))
            {
                onComplete?.Invoke(LoadResult.Failure($"File not found: {path}"));
                yield break;
            }

            string json = System.IO.File.ReadAllText(path);
            var result = ParseManifestJson(json);
            onComplete?.Invoke(result);
        }
        catch (Exception e)
        {
            onComplete?.Invoke(LoadResult.Failure($"File read error: {e.Message}"));
        }

        yield return null;
        #endif
    }

    // Parses a raw JSON string into a GalleryManifest. This is the core parsing
    // method - all the load methods above end up calling this.
    //
    // Two steps:
    //   1. JsonUtility.FromJson to parse all fields (layout_plan is now an array)
    //   2. Validate the result (all required fields present, all placements
    //      reference real assets, etc.)
    public LoadResult ParseManifestJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return LoadResult.Failure("JSON string is empty");
        }

        try
        {
            // Parse the full manifest in one shot — layout_plan is now a
            // List<RoomLayoutEntry> so JsonUtility handles it directly
            GalleryManifest manifest = JsonUtility.FromJson<GalleryManifest>(json);

            if (manifest == null)
            {
                return LoadResult.Failure("Failed to parse JSON - result is null");
            }

            if (debugMode)
            {
                Debug.Log($"[ManifestLoader] Parsed manifest: {manifest.gallery_id}");
                Debug.Log($"[ManifestLoader] Topology: {manifest.locked_constraints?.topology}");
                Debug.Log($"[ManifestLoader] Gallery style: {manifest.GetGalleryStyle()}");
                Debug.Log($"[ManifestLoader] Rooms: {manifest.locked_constraints?.rooms?.Count ?? 0}");
                Debug.Log($"[ManifestLoader] Layout entries: {manifest.layout_plan?.Count ?? 0}");
                Debug.Log($"[ManifestLoader] Placements: {manifest.placement_plan?.Count ?? 0}");
                Debug.Log($"[ManifestLoader] Assets: {manifest.assets?.Count ?? 0}");
            }

            // Check everything the rest of the system needs is present
            string validationError = manifest.Validate();
            if (validationError != null)
            {
                return LoadResult.Failure($"Validation failed: {validationError}");
            }

            return LoadResult.Success(manifest);
        }
        catch (Exception e)
        {
            Debug.LogError($"[ManifestLoader] Parse error: {e.Message}");
            return LoadResult.Failure($"JSON parse error: {e.Message}");
        }
    }
}
