using System.Collections.Generic;
using Il2Cpp;
using Il2CppSummertimeMadness;
using UnityEngine;

namespace BluePrinceNotes.Game;

/// <summary>
/// Secret codes the game matches against typed text (Input.inputString), which typing a note must not trigger:
/// - SummertimeMadness.SteamHelper "steamresetpls" (DAY/SAVE SYSTEM/Steam Helper, and one on the main menu) calls
///   Steam's ResetAllStats with achievements. Disabled while the notepad has the keyboard: it has no
///   OnEnable/OnDisable and its Update only checks the code.
/// - StatsDisplay "info", UITradingPost and UIEventManager "givemeinfopls" show debug displays. They stay enabled
///   (UIEventManager also runs UI input), but their match index is put back to 0 in every Update and LateUpdate,
///   so a code can only advance by the characters typed in one frame.
/// </summary>
internal static class CodeGuard
{
    private static readonly List<SteamHelper> Disabled = new();
    private static readonly List<SteamHelper> Steam = new();
    private static readonly List<StatsDisplay> Stats = new();
    private static readonly List<UITradingPost> Trading = new();
    private static readonly List<UIEventManager> Events = new();

    public static bool Engaged { get; private set; }

    /// <summary>What the last Engage found (debug log).</summary>
    public static string Summary { get; private set; } = "";

    /// <summary>Highest match index seen before zeroing (debug: shows the guard at work).</summary>
    public static int MaxIndexSeen;

    public static void Engage()
    {
        if (Engaged) return;
        Collect();
        foreach (var s in Steam)
        {
            if (s == null || !s.enabled) continue;
            s.enabled = false;
            Disabled.Add(s);
        }
        Engaged = true;
        Zero(false);
        Summary = $"SteamHelper {Count(Steam)} (disabled {Disabled.Count}), StatsDisplay {Count(Stats)}, " +
                  $"UITradingPost {Count(Trading)}, UIEventManager {Count(Events)}";
    }

    /// <summary>"total/active", active = its Update runs.</summary>
    private static string Count<T>(List<T> list) where T : Behaviour
    {
        int active = 0;
        foreach (var b in list)
            if (b != null && b.isActiveAndEnabled) active++;
        return $"{list.Count}/{active} active";
    }

    /// <summary>Call every Update and LateUpdate while engaged.</summary>
    public static void Tick(bool record = false)
    {
        if (Engaged) Zero(record);
    }

    public static void Release()
    {
        if (!Engaged) return;
        Zero(false);
        foreach (var s in Disabled)
            if (s != null) s.enabled = true;
        Disabled.Clear();
        ClearLists();
        Engaged = false;
    }

    /// <summary>Scene change: the objects are gone, so there is nothing to restore.</summary>
    public static void Forget()
    {
        Disabled.Clear();
        ClearLists();
        Engaged = false;
    }

    private static void Collect()
    {
        ClearLists();
        // Scene objects only (FindObjectsOfTypeAll also returns prefabs), active or not.
        foreach (var o in Resources.FindObjectsOfTypeAll<SteamHelper>()) if (InScene(o)) Steam.Add(o);
        foreach (var o in Resources.FindObjectsOfTypeAll<StatsDisplay>()) if (InScene(o)) Stats.Add(o);
        foreach (var o in Resources.FindObjectsOfTypeAll<UITradingPost>()) if (InScene(o)) Trading.Add(o);
        foreach (var o in Resources.FindObjectsOfTypeAll<UIEventManager>()) if (InScene(o)) Events.Add(o);
    }

    private static bool InScene(Component c) => c != null && c.gameObject.scene.IsValid();

    private static void Zero(bool record)
    {
        foreach (var s in Steam)
            if (s != null) { Note(record, s._resetCodeIndex); s._resetCodeIndex = 0; }
        foreach (var s in Stats)
            if (s != null) { Note(record, s._resetCodeIndex); s._resetCodeIndex = 0; }
        foreach (var t in Trading)
            if (t != null) { Note(record, t._debugCodeIndex); t._debugCodeIndex = 0; }
        foreach (var e in Events)
            if (e != null) { Note(record, e._debugCodeIndex); e._debugCodeIndex = 0; }
    }

    private static void Note(bool record, int index)
    {
        if (record && index > MaxIndexSeen) MaxIndexSeen = index;
    }

    private static void ClearLists()
    {
        Steam.Clear();
        Stats.Clear();
        Trading.Clear();
        Events.Clear();
    }
}
