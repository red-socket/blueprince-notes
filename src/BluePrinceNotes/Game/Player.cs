using System.Collections.Generic;
using BluePrinceNotes.Util;
using Il2Cpp;
using Il2CppHutongGames.PlayMaker;
using UnityEngine;

namespace BluePrinceNotes.Game;

/// <summary>
/// The player FSM ("__SYSTEM/FPS Home/FPSController - Prince", FSM "FSM"), which owns freezing.
/// The notepad freezes the player the way the pause menu does:
/// - QuickFreeze -> "QUICK FROZEN" (currently frozen = true, FirstPersonController m_CanMove/m_CanRotate = false,
///   UI OVERLAY CAM becomes the main camera) -> 0.02 s -> "State 6" (cursor unlocked), where it waits.
/// - QuickUnfreeze -> "State 8" -> "UNFROZEN" (currently frozen = false, cursor locked) -> "State 7".
/// There is no freeze counter: one bool, and the last event wins. While the player is frozen, the pause menu,
/// blueprint (Tab/B), room directory (R) and Itsaspray (X) check "currently frozen" and do nothing.
/// </summary>
internal static class Player
{
    private const string Path = "__SYSTEM/FPS Home/FPSController - Prince";
    private const string CameraName = "FirstPersonCharacter";
    private const string FrozenVar = "currently frozen";

    // Free roam: the player walks and the world is clickable (State 1 = before the first freeze of the day;
    // "Unfreeze FULL" = after the global "enable", sent by the Cliff Elevator lever and the gas-flame cameras).
    private static readonly HashSet<string> FreeStates =
        new() { "State 1", "State 7", "State 14", "State 17", "Unfreeze FULL" };
    // Where QuickFreeze leaves the player.
    private static readonly HashSet<string> QuickFrozenStates = new() { "QUICK FROZEN", "State 6" };
    // On the way back to free roam after QuickUnfreeze or UnFreeze.
    private static readonly HashSet<string> UnfreezingStates = new() { "State 8", "UNFROZEN", "State 11", "UNFROZEN 2" };

    private static PlayMakerFSM _fsm;
    private static FsmBool _frozen;
    private static float _nextSearch;

    public static void Reset()
    {
        _fsm = null;
        _frozen = null;
        _nextSearch = 0f;
    }

    /// <summary>The player FSM, or null outside a day. Looked up at most once a second.</summary>
    public static PlayMakerFSM Fsm
    {
        get
        {
            if (_fsm != null) return _fsm;
            _frozen = null;
            if (Time.unscaledTime < _nextSearch) return null;
            _nextSearch = Time.unscaledTime + 1f;
            _fsm = FsmUtil.Find(Path);
            if (_fsm != null)
            {
                var vars = _fsm.FsmVariables;
                _frozen = vars != null && vars.Contains(FrozenVar) ? vars.FindFsmBool(FrozenVar) : null;
            }
            return _fsm;
        }
    }

    public static string State => Fsm?.ActiveStateName;

    /// <summary>The FSM's "currently frozen" bool, or null when it can't be read.</summary>
    public static bool? Frozen => Fsm != null && _frozen != null ? _frozen.Value : null;

    public static bool IsQuickFrozen(string state) => state != null && QuickFrozenStates.Contains(state);
    public static bool IsFreeState(string state) => state != null && FreeStates.Contains(state);
    public static bool IsUnfreezing(string state) => state != null && UnfreezingStates.Contains(state);

    /// <summary>The player walks freely and can click the world.</summary>
    public static bool IsFree()
    {
        if (!IsFreeState(State) || Frozen != false) return false;
        var cam = Camera.main;
        return cam != null && cam.gameObject.name == CameraName;
    }

    /// <summary>Sends a PlayMaker event to the player FSM; false when there is no player.</summary>
    public static bool Send(string ev)
    {
        var fsm = Fsm;
        if (fsm == null) return false;
        fsm.SendEvent(ev);
        return true;
    }
}
