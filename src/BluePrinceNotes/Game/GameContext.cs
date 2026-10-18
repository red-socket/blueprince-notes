using System;
using System.Globalization;
using BluePrinceNotes.Notes;
using BluePrinceNotes.Util;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace BluePrinceNotes.Game;

/// <summary>
/// Reads where and when the player is, for a new note. Read-only: only finds FSM variables, never creates or
/// sets them, and every source can be missing (the field then keeps its default).
/// - Day: Global Manager int "DAY" (set once a day by DAY "Day Compare"), else Global Persistent "DAY".
/// - Date: day 1 is Sat Nov 7, 2020 (the DAY object's "Dates" list).
/// - Time: 8:00 AM + DAY/SYNKA PROJECT float "TEMP HOUR" (hours; else "SYNKA CLOCK" seconds). Only valid once the
///   clock object is active, which is when the player gets control.
/// - Room and area: the HUD's own labels. Inside the house "Rank Text" reads "Rank N" and "Room Text" is the room;
///   outside, the grounds and underground areas write "The Grounds" / "Underground" / " " and the area's name.
/// - Tile: __SYSTEM/THE GRID FSM "GRID DATA" int "Current Tile" (stale outside the house, so only used inside).
/// - Save: Global Persistent "SaveSlot" and "RANDOMSEED", DAY/GAME CLOCK float "GAME CLOCK LOAD" (saved play time
///   when the day started).
/// </summary>
internal static class GameContext
{
    private const string GlobalManager = "Global Manager";
    private const string GlobalPersistent = "Global Persitent Manager";  // sic
    private const string HudPath = "__SYSTEM/HUD";
    private const string GridPath = "__SYSTEM/THE GRID";
    private const string GameClockPath = "DAY/GAME CLOCK";
    private static readonly DateTime DayZero = new(2020, 11, 6);

    public static NoteContext Capture()
    {
        var c = new NoteContext();
        var gm = FsmUtil.Find(GlobalManager);
        var gp = FsmUtil.Find(GlobalPersistent);

        Try(() =>
        {
            int day = Int(gm, "DAY");
            if (day <= 0) day = Int(gp, "DAY");
            if (day > 0)
            {
                c.Day = day;
                c.Date = DayZero.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        });

        Try(() =>
        {
            var synka = Go(gm, "SYNKA PROJECT");
            if (synka == null || !synka.activeInHierarchy) return;
            var fsm = FsmUtil.Get(synka);
            float hours = Has(fsm, "TEMP HOUR") ? fsm.FsmVariables.FindFsmFloat("TEMP HOUR").Value
                : Has(fsm, "SYNKA CLOCK") ? fsm.FsmVariables.FindFsmFloat("SYNKA CLOCK").Value / 3600f
                : float.NaN;
            if (!float.IsFinite(hours)) return;
            long minutes = (long)Math.Floor((8.0 + hours) * 60.0);
            c.Minute = (int)((minutes % 1440 + 1440) % 1440);
        });

        Try(() =>
        {
            var hud = Go(gm, "HUD") ?? GameObject.Find(HudPath);
            string room = Label(hud, "Room Text"), rank = Label(hud, "Rank Text");
            c.Room = room ?? "";
            if (rank == null) return;
            if (rank.StartsWith("Rank ", StringComparison.Ordinal) &&
                int.TryParse(rank.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
            {
                c.Area = "House";
                c.Rank = r;
                var grid = FsmUtil.Get(Go(gm, "THE GRID") ?? GameObject.Find(GridPath), "GRID DATA");
                int tile = Int(grid, "Current Tile");
                if (tile >= 1 && tile <= 46) c.Tile = tile;
            }
            else if (rank == "Underground") c.Area = "Underground";
            else c.Area = "Grounds";  // "The Grounds", or blank on the Front Steps
        });

        Try(() =>
        {
            c.SaveSlot = Int(gp, "SaveSlot");
            c.Seed = Int(gp, "RANDOMSEED");
            var clock = FsmUtil.Find(GameClockPath);
            float start = Has(clock, "GAME CLOCK LOAD") ? clock.FsmVariables.FindFsmFloat("GAME CLOCK LOAD").Value : -1f;
            if (float.IsFinite(start) && start >= 0f) c.DayStartClock = start;
        });

        return c;
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception e) { Core.Log.Warning($"[Notepad] reading game context: {e.Message}"); }
    }

    private static string Label(GameObject hud, string child)
    {
        var t = hud != null ? hud.transform.Find(child) : null;
        var tmp = t != null ? t.GetComponent<TMP_Text>() : null;
        return tmp != null ? (tmp.text ?? "").Trim() : null;
    }

    private static bool Has(PlayMakerFSM fsm, string name) =>
        fsm != null && fsm.FsmVariables != null && fsm.FsmVariables.Contains(name);

    private static int Int(PlayMakerFSM fsm, string name) =>
        Has(fsm, name) ? fsm.FsmVariables.FindFsmInt(name).Value : 0;

    private static GameObject Go(PlayMakerFSM fsm, string name)
    {
        if (!Has(fsm, name)) return null;
        var go = fsm.FsmVariables.FindFsmGameObject(name)?.Value;
        return go != null ? go : null;  // a destroyed object becomes a real null
    }

    // ------------------------------------------------------------------ display

    /// <summary>"Day 12 · Wed Nov 18 · 3:45 PM · Parlor (Rank 4)", skipping what's unknown.</summary>
    public static string Describe(NoteContext c, bool use24Hour)
    {
        if (c == null) return "";
        var parts = new System.Collections.Generic.List<string>(4);
        if (c.Day > 0) parts.Add($"Day {c.Day}");
        if (DateTime.TryParseExact(c.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            parts.Add(d.ToString("ddd MMM d", CultureInfo.InvariantCulture));
        if (c.Minute >= 0) parts.Add(Clock(c.Minute, use24Hour));
        var place = c.Room ?? "";
        var area = c.Area ?? "";
        if (area == "House" && c.Rank > 0) place = place.Length > 0 ? $"{place} (Rank {c.Rank})" : $"Rank {c.Rank}";
        else if (place.Length == 0 && area.Length > 0) place = area == "Grounds" ? "The Grounds" : area;
        if (place.Length > 0) parts.Add(place);
        return string.Join(" · ", parts);
    }

    public static string Clock(int minute, bool use24Hour)
    {
        int h = minute / 60, m = minute % 60;
        if (use24Hour) return $"{h:00}:{m:00}";
        return $"{(h % 12 == 0 ? 12 : h % 12)}:{m:00} {(h < 12 ? "AM" : "PM")}";
    }
}
