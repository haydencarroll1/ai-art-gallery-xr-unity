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

    public void LoadFromUrl(string url, Action<LoadResult> onComplete)
    {
        StartCoroutine(LoadFromUrlCoroutine(url, onComplete));
    }

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

    public void LoadFromStreamingAssets(string filename, Action<LoadResult> onComplete)
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, filename);
        StartCoroutine(LoadFromLocalPathCoroutine(path, onComplete));
    }

    private IEnumerator LoadFromLocalPathCoroutine(string path, Action<LoadResult> onComplete)
    {
        if (debugMode) Debug.Log($"[ManifestLoader] Loading from: {path}");

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

    public LoadResult ParseManifestJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return LoadResult.Failure("JSON string is empty");
        }

        try
        {
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
