using BluePrinceNotes.Util;
using Il2Cpp;
using UnityEngine;

namespace BluePrinceNotes.Game;

/// <summary>State of the current day, from the Global Manager FSM.</summary>
internal static class Estate
{
    private const string GlobalManager = "Global Manager";
    private const string SynkaVar = "SYNKA PROJECT";
    // Global Manager: holding numpad Enter for 5 s in "hold keypad enter" enters RECORDING MODE, which turns off
    // __SYSTEM/RUN ENGINE/Running Engine for the rest of the day. "0" is the state's own key-up exit.
    private const string HoldState = "hold keypad enter";
    private const string HoldCancel = "0";

    private static PlayMakerFSM _gm;
    private static GameObject _synka;
    private static float _nextSearch;

    public static void Reset()
    {
        _gm = null;
        _synka = null;
        _nextSearch = 0f;
    }

    private static PlayMakerFSM Gm
    {
        get
        {
            if (_gm != null) return _gm;
            if (Time.unscaledTime < _nextSearch) return null;
            _nextSearch = Time.unscaledTime + 1f;
            return _gm = FsmUtil.Find(GlobalManager);
        }
    }

    /// <summary>
    /// A day is running: the in-game clock (Global Manager var "SYNKA PROJECT", the DAY/SYNKA PROJECT object)
    /// only becomes active once the player has control.
    /// </summary>
    public static bool DayRunning()
    {
        if (_synka == null)
        {
            var vars = Gm?.FsmVariables;
            var v = vars != null && vars.Contains(SynkaVar) ? vars.FindFsmGameObject(SynkaVar) : null;
            _synka = v?.Value;
            if (_synka == null) return false;
        }
        return _synka.activeInHierarchy;
    }

    /// <summary>
    /// Numpad Enter types a newline in the notepad, but the Global Manager also counts a 5 s hold of it.
    /// Ends that hold the way releasing the key would. Call every frame while the notepad has the keyboard.
    /// </summary>
    public static void CancelRecordingHold()
    {
        var gm = Gm;
        if (gm != null && gm.ActiveStateName == HoldState) gm.SendEvent(HoldCancel);
    }
}
