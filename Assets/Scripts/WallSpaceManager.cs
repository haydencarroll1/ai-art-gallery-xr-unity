using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Manages wall space allocation for artwork placement.
/// Tracks openings (alcoves, doorways) and placed artwork to prevent overlaps.
/// Used by TopologyGenerator to validate and adjust placement positions.
/// </summary>
public class WallSpaceManager
{
    // Represents an occupied or blocked region on a wall
    public class WallRegion
    {
        public float start;      // Distance from wall start
        public float end;        // Distance from wall start
        public string type;      // "opening", "artwork", "doorway"
        public string id;        // Asset ID or opening ID
        
        public float Center => (start + end) / 2f;
        public float Width => end - start;
        
        public bool Overlaps(float otherStart, float otherEnd, float margin = 0f)
        {
            return otherStart - margin < end && otherEnd + margin > start;
        }
        
        public bool Contains(float position, float margin = 0f)
        {
            return position >= start - margin && position <= end + margin;
        }
    }
    
    // Track regions per wall (key = "roomId_wallName")
    private Dictionary<string, List<WallRegion>> wallRegions = new Dictionary<string, List<WallRegion>>();
    
    // Minimum spacing between artwork pieces
    public float MinArtSpacing { get; set; } = 0.5f;
    
    // Margin from wall edges
    public float WallEdgeMargin { get; set; } = 0.3f;
    
    // Margin from openings (alcoves, doorways)
    public float OpeningMargin { get; set; } = 0.4f;
    
    private static string GetWallKey(string roomId, string wallName) => $"{roomId}_{wallName}";
    
    /// <summary>
    /// Register an opening (alcove, doorway) on a wall.
    /// </summary>
    public void RegisterOpening(string roomId, string wallName, float start, float end, string openingId = null)
    {
        string key = GetWallKey(roomId, wallName);
        if (!wallRegions.ContainsKey(key))
            wallRegions[key] = new List<WallRegion>();
        
        wallRegions[key].Add(new WallRegion
        {
            start = start,
            end = end,
            type = "opening",
            id = openingId ?? $"opening_{wallRegions[key].Count}"
        });
        
        // Keep sorted by position
        wallRegions[key].Sort((a, b) => a.start.CompareTo(b.start));
    }
    
    /// <summary>
    /// Register multiple openings at once (from WallInfo.openings list).
    /// </summary>
    public void RegisterOpenings(string roomId, string wallName, List<(float start, float end)> openings)
    {
        if (openings == null) return;
        
        int index = 0;
        foreach (var opening in openings)
        {
            RegisterOpening(roomId, wallName, opening.start, opening.end, $"{wallName}_opening_{index}");
            index++;
        }
    }
    
    /// <summary>
    /// Register placed artwork to prevent future overlaps.
    /// </summary>
    public void RegisterArtwork(string roomId, string wallName, float centerPosition, float artworkWidth, string assetId)
    {
        string key = GetWallKey(roomId, wallName);
        if (!wallRegions.ContainsKey(key))
            wallRegions[key] = new List<WallRegion>();
        
        float halfWidth = artworkWidth / 2f;
        wallRegions[key].Add(new WallRegion
        {
            start = centerPosition - halfWidth,
            end = centerPosition + halfWidth,
            type = "artwork",
            id = assetId
        });
        
        wallRegions[key].Sort((a, b) => a.start.CompareTo(b.start));
    }
    
    /// <summary>
    /// Check if a position is valid for placing artwork of the given width.
    /// </summary>
    public bool IsPositionValid(string roomId, string wallName, float centerPosition, float artworkWidth, float wallLength)
    {
        float halfWidth = artworkWidth / 2f;
        float artStart = centerPosition - halfWidth;
        float artEnd = centerPosition + halfWidth;
        
        // Check wall bounds
        if (artStart < WallEdgeMargin || artEnd > wallLength - WallEdgeMargin)
            return false;
        
        string key = GetWallKey(roomId, wallName);
        if (!wallRegions.ContainsKey(key))
            return true;
        
        foreach (var region in wallRegions[key])
        {
            float margin = region.type == "opening" ? OpeningMargin : MinArtSpacing;
            if (region.Overlaps(artStart, artEnd, margin))
                return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Find the best valid position for artwork, starting from the requested position.
    /// Returns the adjusted position, or null if no valid position exists.
    /// </summary>
    public float? FindValidPosition(string roomId, string wallName, float requestedPosition, float artworkWidth, float wallLength)
    {
        // First check if requested position is already valid
        if (IsPositionValid(roomId, wallName, requestedPosition, artworkWidth, wallLength))
            return requestedPosition;
        
        float halfWidth = artworkWidth / 2f;
        string key = GetWallKey(roomId, wallName);
        
        // Get all blocked regions
        List<WallRegion> regions = wallRegions.ContainsKey(key) 
            ? wallRegions[key].ToList() 
            : new List<WallRegion>();
        
        // Find available gaps
        List<(float start, float end)> gaps = FindAvailableGaps(regions, wallLength);
        
        if (gaps.Count == 0)
        {
            Debug.LogWarning($"[WallSpaceManager] No valid positions available on {roomId}/{wallName}");
            return null;
        }
        
        // Find the gap closest to the requested position that fits the artwork
        float minArtWidth = artworkWidth + MinArtSpacing * 2;
        float bestPosition = requestedPosition;
        float bestDistance = float.MaxValue;
        
        foreach (var gap in gaps)
        {
            float gapWidth = gap.end - gap.start;
            if (gapWidth < minArtWidth) continue;
            
            // Calculate valid range within this gap
            // Note: gap bounds already account for WallEdgeMargin (from FindAvailableGaps)
            float validStart = gap.start + halfWidth;
            float validEnd = gap.end - halfWidth;
            
            if (validEnd < validStart) continue;
            
            // Find closest point in this gap to the requested position
            float closestInGap = Mathf.Clamp(requestedPosition, validStart, validEnd);
            float distance = Mathf.Abs(closestInGap - requestedPosition);
            
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestPosition = closestInGap;
            }
        }
        
        // Verify the best position is actually valid
        if (IsPositionValid(roomId, wallName, bestPosition, artworkWidth, wallLength))
            return bestPosition;
        
        Debug.LogWarning($"[WallSpaceManager] Could not find valid position for {artworkWidth}m artwork at {requestedPosition} on {roomId}/{wallName}");
        return null;
    }
    
    /// <summary>
    /// Find all available gaps between blocked regions.
    /// </summary>
    private List<(float start, float end)> FindAvailableGaps(List<WallRegion> regions, float wallLength)
    {
        List<(float start, float end)> gaps = new List<(float, float)>();
        
        if (regions.Count == 0)
        {
            gaps.Add((WallEdgeMargin, wallLength - WallEdgeMargin));
            return gaps;
        }
        
        // Sort regions by start position
        var sortedRegions = regions.OrderBy(r => r.start).ToList();
        
        // Check gap before first region
        float firstStart = sortedRegions[0].start;
        if (firstStart > WallEdgeMargin + OpeningMargin)
        {
            gaps.Add((WallEdgeMargin, firstStart - OpeningMargin));
        }
        
        // Check gaps between regions
        for (int i = 0; i < sortedRegions.Count - 1; i++)
        {
            float currentEnd = sortedRegions[i].end;
            float nextStart = sortedRegions[i + 1].start;
            
            float margin = sortedRegions[i].type == "artwork" ? MinArtSpacing : OpeningMargin;
            float nextMargin = sortedRegions[i + 1].type == "artwork" ? MinArtSpacing : OpeningMargin;
            
            float gapStart = currentEnd + margin;
            float gapEnd = nextStart - nextMargin;
            
            if (gapEnd > gapStart)
            {
                gaps.Add((gapStart, gapEnd));
            }
        }
        
        // Check gap after last region
        float lastEnd = sortedRegions[sortedRegions.Count - 1].end;
        float margin2 = sortedRegions[sortedRegions.Count - 1].type == "artwork" ? MinArtSpacing : OpeningMargin;
        
        if (lastEnd + margin2 < wallLength - WallEdgeMargin)
        {
            gaps.Add((lastEnd + margin2, wallLength - WallEdgeMargin));
        }
        
        return gaps;
    }
    
    // Removed: ClearRoom() — dead method, only ClearAll() is used

    /// <summary>
    /// Clear all registered regions.
    /// </summary>
    public void ClearAll()
    {
        wallRegions.Clear();
    }

    // Removed: GetDebugInfo() — dead method, never called
}
