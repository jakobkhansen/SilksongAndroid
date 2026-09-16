// DsMarkers — the player's map pins, read and written where the game keeps them.
//
// PlayerData.placedMarkers is an array indexed by MapMarkerMenu.MarkerTypes
// (A..E), each a WrappedVector2List of positions in the GAME MAP's local space.
// GameMap.SetupMapMarkers reads exactly that to place its own pin objects, so
// writing to it and asking the map to set up again is the whole of placing a
// pin: there is no separate "place" call to find, and nothing else to keep in
// step.
//
// Nine per type is the game's own ceiling -- GameMap allocates
// spawnedMapMarkers[types, 9] and draws at most that many -- so a tenth would
// be saved and never drawn.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

public static class DsMarkers
{
    public const int TypeCount = 5;
    public const int PerType = 9;

    /// <summary>
    /// Has the player found this pin type?
    ///
    /// The flags are hasMarker_a through hasMarker_e, one per type, which is
    /// what MapMarkerButton counts to decide how many kinds the player owns.
    /// PlayerData also has a plain `hasMarker`, and taking THAT for the first
    /// type -- it sits right before the lettered five and looks like the start
    /// of the run -- shifted every type by one and offered a pin that had never
    /// been found.
    /// </summary>
    public static bool Unlocked(int type)
    {
        try
        {
            var pd = PlayerData.instance;
            switch (type)
            {
                case 0: return pd.hasMarker_a;
                case 1: return pd.hasMarker_b;
                case 2: return pd.hasMarker_c;
                case 3: return pd.hasMarker_d;
                case 4: return pd.hasMarker_e;
            }
        }
        catch { }
        return false;
    }

    public static List<Vector2> Placed(int type)
    {
        try
        {
            var pd = PlayerData.instance;
            if (pd.placedMarkers == null || type < 0 || type >= pd.placedMarkers.Length) return null;
            var wrapped = pd.placedMarkers[type];
            if (wrapped == null)
            {
                wrapped = new WrappedVector2List();
                pd.placedMarkers[type] = wrapped;
            }
            return wrapped.List;
        }
        catch { return null; }
    }

    /// <summary>How many of this type are left to place.</summary>
    public static int Remaining(int type)
    {
        var list = Placed(type);
        return list == null ? 0 : Mathf.Max(0, PerType - list.Count);
    }

    public static bool Place(int type, Vector2 local)
    {
        var list = Placed(type);
        if (list == null || list.Count >= PerType) return false;
        list.Add(local);
        return true;
    }

    public static bool RemoveAt(int type, int index)
    {
        var list = Placed(type);
        if (list == null || index < 0 || index >= list.Count) return false;
        list.RemoveAt(index);
        return true;
    }
}
#endif
