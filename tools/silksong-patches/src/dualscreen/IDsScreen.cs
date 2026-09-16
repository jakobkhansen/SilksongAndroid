// IDsScreen — the contract every second-screen page implements.
//
// "Add another screen" is the whole point of this design, so the contract is
// kept small enough that adding one is a single file and a single Register
// call. A screen knows about its own RectTransform and nothing else: not the
// camera, not the display, not the shell, not that a second screen exists at
// all. That is what makes them testable in isolation and what stops the map
// screen's peculiarities leaking into the crest screen.
//
// Rules that keep the modularity real rather than nominal:
//
//   * Build is called ONCE, lazily, the first time the screen is shown. An
//     unused screen therefore costs nothing, and a screen that throws while
//     building is disabled rather than fatal.
//   * Tick runs only while visible, on unscaled time -- the game holds
//     timeScale at zero whenever its own menu is open.
//   * Screens are READ-ONLY. None of them may write player state; that is
//     deliberately not in the interface yet.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A screen whose header title is not simply its name.
///
/// The Map is the one that has this: the designs show the AREA there --
/// "Choral Chambers" -- rather than the word MAP, because which tab you are on
/// is already obvious from the caret in the strip below, while where you are
/// is the thing you opened the map to find out.
/// </summary>
public interface IDsHeaderTitle
{
    /// <summary>Null or empty falls back to the screen's Title.</summary>
    string HeaderTitle { get; }
}

/// <summary>
/// One button in a screen's replacement tab strip.
/// </summary>
public struct DsStripItem
{
    public Sprite Icon;
    /// <summary>A count beside the icon, e.g. how many pins are left. Null for none.</summary>
    public string Badge;
    public bool Selected;
    /// <summary>Drawn dim; still tappable, so it can explain itself.</summary>
    public bool Dim;
}

/// <summary>
/// A screen that takes the tab strip over for a mode of its own.
///
/// The map does this for marker mode: the v3 notes ask for the tabs to be
/// replaced by the unlocked marker icons while placing pins, which also stops
/// the player leaving the mode by accident through a tab they meant to tap a
/// pin with.
/// </summary>
public interface IDsTabStrip
{
    /// <summary>False leaves the ordinary tabs alone.</summary>
    bool StripOverride { get; }
    void CollectStrip(List<DsStripItem> into);
    void OnStripSelect(int index);
}

public interface IDsScreen
{
    /// <summary>Stable identifier, used for persistence. Not shown.</summary>
    string Id { get; }

    /// <summary>Fallback label while the native tab icon is unavailable.</summary>
    string Title { get; }

    /// <summary>Eligibility for Next navigation. Native unlock filtering is deferred.</summary>
    bool Available { get; }

    /// <summary>Build the UI under <paramref name="host"/>. Called once.</summary>
    void Build(RectTransform host);

    void OnShow();
    void OnHide();

    /// <summary>Per-frame while visible. dt is unscaled.</summary>
    void Tick(float dt);

    /// <summary>A gesture on this panel, in panel pixels.</summary>
    void OnGesture(DsGesture g);
}
#endif
