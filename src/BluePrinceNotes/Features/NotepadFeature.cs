using System;
using BluePrinceNotes.Game;
using BluePrinceNotes.UI;
using BluePrinceNotes.Util;
using Il2CppBluePrince.UI;
using MelonLoader;
using UnityEngine;

namespace BluePrinceNotes.Features;

/// <summary>
/// The notepad: a hotkey opens the notebook (list of notes and editor, see Notebook) during a day. This class
/// owns opening, closing and the player; Notebook owns the notes and the keys while open.
///
/// Typed keys always reach the game too (legacy Input has no way to consume a key), so while the notepad is
/// open the player is frozen the way the pause menu does it (QuickFreeze, see Player), which makes movement,
/// looking around, world clicks and the game's menu keys do nothing. The game's secret-code listeners are
/// switched off or held back at the same time (CodeGuard).
///
/// Opening needs a running day, a free player, no game menu, and the player free for a moment: the game sends
/// some freezes and unfreezes with a delay of up to 1 s, which must not land on the notepad's freeze.
///
/// While open, a watchdog checks the player FSM every frame. If something else unfreezes the player (Blue Prince
/// QoL's emergency unfreeze key, a delayed QuickUnfreeze), the notepad freezes again. If the game freezes the
/// player for its own reasons (a cutscene, a card) or opens a menu, the notepad closes and leaves the player to
/// the game.
///
/// Closing (the hotkey anywhere, Esc on the list, a click outside the page) hides the panel at once and unfreezes
/// once those keys and the mouse buttons are released, so the press can't also open the pause menu or click the
/// world. The emergency key closes and unfreezes no matter what. When the hotkey can't open the notepad, a short
/// message says so. While open, the game's own mouse pointer is hidden and drawn on top of the page instead.
/// </summary>
public class NotepadFeature : Feature
{
    public override string Name => "Notepad";
    protected override string Description => "In-game notepad, opened with a hotkey during a day";

    private const float FreeBeforeOpen = 1.1f;   // longer than every delayed freeze/unfreeze the game sends
    private const float MinUnfreezeDelay = 0.15f;
    private const float MaxUnfreezeDelay = 1f;
    private const int MaxRefreezes = 5;          // within RefreezeWindow, then give the player back
    private const float RefreezeWindow = 3f;

    private MelonPreferences_Entry<string> _keyPref, _emergencyPref, _fontPref;
    private MelonPreferences_Entry<float> _fontScale;
    private MelonPreferences_Entry<bool> _log, _ime;
    private Hotkey _key, _emergency;

    private enum Phase { Closed, Open, Closing }
    private static Phase _phase;

    /// <summary>The panel is open and has the keyboard.</summary>
    internal static bool IsOpen => _phase == Phase.Open;
    /// <summary>The notepad holds the player (open, or closed and waiting to unfreeze).</summary>
    internal static bool Active => _phase != Phase.Closed;
    /// <summary>Dev builds turn on the detailed log.</summary>
    internal static bool Verbose;

    private bool _ownFreeze;
    private float _freeSince = -1f;     // the player has been free (or held by the notepad) since then
    private float _ownUnfreezeAt = -1f;
    private bool _clean;                // nothing else touched the player while the notepad held it
    private int _openedFrame;
    private float _closeRequestedAt;
    private int _releasedFrames;
    private int _refreezes;
    private float _refreezeWindowStart;
    private bool _menuCheckBroken;
    private bool _imeChanged;
    private IMECompositionMode _imeBefore;

    private readonly NotepadView _view = new();
    private readonly Notebook _book = new();
    private readonly Toast _toast = new();
    private bool _wasFocused = true;

    protected override void CreatePrefs()
    {
        _keyPref = Core.Prefs.CreateEntry("Notepad_Key", "F2", "Notepad: hotkey",
            "Opens and closes the notepad (e.g. F2, F3, Ctrl+F3)");
        _emergencyPref = Core.Prefs.CreateEntry("Notepad_EmergencyKey", "Ctrl+F2", "Notepad: emergency key",
            "Closes the notepad and unfreezes the player, whatever the game is doing");
        _fontPref = Core.Prefs.CreateEntry("Notepad_Font", "HandCAVE SDF - GENERIC", "Notepad: font",
            "Name of a game font (TMP font asset)");
        _fontScale = Core.Prefs.CreateEntry("Notepad_FontScale", 0.03f, "Notepad: font size",
            "Text size, as a fraction of the screen height");
        _ime = Core.Prefs.CreateEntry("Notepad_InputMethod", true, "Notepad: input method",
            "Turns the input method on while typing. Needed for accents typed with dead keys (ibus under Proton) and for IME languages");
        _log = Core.Prefs.CreateEntry("Notepad_Log", false, "Notepad: log", "Debug: log what the notepad does");
        ParseKeys();
        _keyPref.OnEntryValueChanged.Subscribe((_, _) => ParseKeys());
        _emergencyPref.OnEntryValueChanged.Subscribe((_, _) => ParseKeys());
    }

    private void ParseKeys()
    {
        _key = Hotkey.Parse(_keyPref.Value, new Hotkey(KeyCode.F2), Name);
        _emergency = Hotkey.Parse(_emergencyPref.Value, new Hotkey(KeyCode.F2, ctrl: true), Name);
    }

    public override void OnSceneLoaded(string sceneName)
    {
        // The player FSM and the code listeners belong to the old scene: forget them, send nothing.
        if (_phase != Phase.Closed) Debug($"scene '{sceneName}' loaded while active; forgetting the freeze");
        try { _book.Closed(); }
        catch (Exception e) { Core.Log.Warning($"[{Name}] saving notes on scene load: {e.Message}"); }
        _phase = Phase.Closed;
        _ownFreeze = false;
        _freeSince = -1f;
        CodeGuard.Forget();
        InputMethodRestore();
        GamePointer.Restore();
        _toast.Destroy();
        Player.Reset();
        Estate.Reset();
        _view.Destroy();
    }

    protected override void OnTurnedOff()
    {
        if (_phase == Phase.Closed) return;
        Debug("turned off while active");
        FinishClose();
    }

    public override void OnQuit() => _book.Closed();

    public override void OnUpdate()
    {
        // Saves: a pending one, and right away when the game loses focus (alt-tab, or about to be closed).
        _book.Tick();
        _toast.Tick();
        bool focused = Application.isFocused;
        if (_wasFocused && !focused) _book.FocusLost();
        if (!_wasFocused && focused) _book.FocusGained();
        _wasFocused = focused;

        try { Step(); }
        catch
        {
            FailSafe();
            throw;
        }
    }

    public override void OnLateUpdate()
    {
        if (_phase != Phase.Closed) CodeGuard.Tick(record: true);
    }

    private void Step()
    {
        if (_emergency.Down())
        {
            Emergency();
            return;
        }
        switch (_phase)
        {
            case Phase.Closed:
                TrackFree();
                if (_key.Down()) TryOpen();
                break;
            case Phase.Open:
                WhileOpen();
                break;
            case Phase.Closing:
                WhileClosing();
                break;
        }
    }

    // ------------------------------------------------------------------ open

    private void TrackFree()
    {
        bool free = Estate.DayRunning() && Player.IsFree() && !MenuOpen();
        if (free)
        {
            if (_freeSince < 0f) _freeSince = Time.unscaledTime;
            return;
        }
        // The notepad's own unfreeze passes through "State 8"/"UNFROZEN" for about 0.1 s: not the game's doing.
        if (_freeSince >= 0f && Time.unscaledTime - _ownUnfreezeAt < 0.5f && Player.IsUnfreezing(Player.State)) return;
        _freeSince = -1f;
    }

    /// <summary>Why the notepad can't open now (for the log), and what to tell the player; null when it can.</summary>
    private string CantOpenReason(out string message)
    {
        message = "Notes can't open right now";
        if (!Estate.DayRunning())
        {
            message = "Notes only open during a day";
            return "no day is running";
        }
        if (MenuOpen())
        {
            message = "Close the menu to open your notes";
            return "a game menu is open";
        }
        if (!Player.IsFree()) return $"the player is busy (state '{Player.State}')";
        if (_freeSince < 0f || Time.unscaledTime - _freeSince < FreeBeforeOpen) return "the player only just got free";
        message = null;
        return null;
    }

    private void TryOpen()
    {
        var why = CantOpenReason(out var message);
        if (why != null)
        {
            Core.Log.Msg($"[{Name}] can't open now: {why}");
            _toast.Show(message, _fontPref.Value);
            return;
        }
        _toast.Hide();
        if (!Player.Send("QuickFreeze")) return;
        _ownFreeze = true;
        _phase = Phase.Open;
        _openedFrame = Time.frameCount;
        _refreezes = 0;
        _clean = true;
        CodeGuard.Engage();
        InputMethodOn();
        if (GamePointer.Texture != null) GamePointer.Hide();
        _view.ResetPointer();
        _book.Opened();
        Debug($"opened, player FSM in '{Player.State}', frozen={Player.Frozen}; code guard: {CodeGuard.Summary}");
        Render();
    }

    // ------------------------------------------------------------------ open: every frame

    private void WhileOpen()
    {
        CodeGuard.Tick();
        Estate.CancelRecordingHold();
        if (GamePointer.Texture != null) GamePointer.Hide();  // without our pointer art, keep the game's
        if (!Watchdog()) return;

        // The hotkey closes; the notebook asks to close on Esc. The frame that opened the notepad is skipped: its keys
        // were meant for the game.
        if (Time.frameCount != _openedFrame)
        {
            if (_key.Down() || _book.HandleInput(_view))
            {
                BeginClose();
                return;
            }
        }
        Render();
    }

    /// <summary>False when the notepad had to give the player back.</summary>
    private bool Watchdog()
    {
        if (Player.Fsm == null) return Drop("the player is gone");
        if (MenuOpen()) return Drop("a game menu opened");

        var state = Player.State;
        var frozen = Player.Frozen;
        if (Player.IsQuickFrozen(state) && frozen == true) return true;

        if (frozen == false || Player.IsFreeState(state) || Player.IsUnfreezing(state))
        {
            // Something else unfroze the player: freeze again, unless it keeps happening.
            if (Time.unscaledTime - _refreezeWindowStart > RefreezeWindow)
            {
                _refreezeWindowStart = Time.unscaledTime;
                _refreezes = 0;
            }
            _clean = false;
            if (++_refreezes > MaxRefreezes) return Drop("the player keeps being unfrozen");
            Core.Log.Warning($"[{Name}] the player was unfrozen while the notepad is open (state '{state}'); freezing again");
            Player.Send("QuickFreeze");
            return true;
        }

        // The game froze the player its own way (cutscene, card, camera move): it owns the player now.
        return Drop($"the game took over the player (state '{state}')");
    }

    private void Render()
    {
        if (!_view.Begin(_fontPref.Value, _fontScale.Value)) return;
        _book.Render(_view, _keyPref.Value);
        _view.UpdatePointer();
    }

    // ------------------------------------------------------------------ close

    private void BeginClose()
    {
        _view.Hide();
        _phase = Phase.Closing;
        _closeRequestedAt = Time.unscaledTime;
        _releasedFrames = 0;
        Debug("closing");
    }

    private void WhileClosing()
    {
        CodeGuard.Tick();
        Estate.CancelRecordingHold();
        // Also the mouse: a click outside the page closes, and its release must not reach the unfrozen world.
        bool held = Input.GetKey(KeyCode.Escape) || _key.Held() || Input.GetMouseButton(0) || Input.GetMouseButton(1);
        _releasedFrames = held ? 0 : _releasedFrames + 1;
        float waited = Time.unscaledTime - _closeRequestedAt;
        if (waited < MaxUnfreezeDelay && (_releasedFrames < 2 || waited < MinUnfreezeDelay)) return;
        FinishClose();
    }

    /// <summary>Unfreezes only a freeze that is still the notepad's.</summary>
    private void FinishClose()
    {
        _view.Hide();
        var state = Player.State;
        bool unfroze = false;
        if (_ownFreeze && Player.IsQuickFrozen(state) && Player.Frozen == true && !MenuOpen())
        {
            unfroze = Player.Send("QuickUnfreeze");
            _ownUnfreezeAt = Time.unscaledTime;
            Debug($"closed, unfroze (player FSM now '{Player.State}')");
        }
        else
            Debug($"closed without unfreezing (state '{state}', owned={_ownFreeze})");
        // After a clean open/close nothing of the game's can be pending, so F2 can open again right away.
        Reset(keepFreeTime: unfroze && _clean);
    }

    /// <summary>Closes without touching the player, who belongs to the game now.</summary>
    private bool Drop(string reason)
    {
        _view.Hide();
        Core.Log.Warning($"[{Name}] closed: {reason}");
        Reset();
        return false;
    }

    private void Emergency()
    {
        _view.Hide();
        var state = Player.State;
        bool sent = Player.Send("QuickUnfreeze");
        Core.Log.Msg(sent
            ? $"[{Name}] emergency key: notepad closed, sent QuickUnfreeze (player FSM was in '{state}')"
            : $"[{Name}] emergency key: notepad closed (no player FSM)");
        Reset();
    }

    /// <summary>Exception while active: give everything back, then let Core log it.</summary>
    private void FailSafe()
    {
        try { _view.Hide(); } catch { /* keep going */ }
        try
        {
            if (_ownFreeze && Player.IsQuickFrozen(Player.State)) Player.Send("QuickUnfreeze");
        }
        catch { /* keep going */ }
        try { Reset(); } catch { /* keep going */ }
    }

    private void Reset(bool keepFreeTime = false)
    {
        try { _book.Closed(); }
        catch (Exception e) { Core.Log.Warning($"[{Name}] saving notes: {e.Message}"); }
        InputMethodRestore();
        GamePointer.Restore();
        CodeGuard.Release();
        _phase = Phase.Closed;
        _ownFreeze = false;
        if (!keepFreeTime) _freeSince = -1f;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Unity keeps the input method off unless one of its text fields has focus. Under Proton with ibus, dead keys
    /// are composed by ibus and arrive through the input method, so with it off "´" + "e" types a bare "e".
    /// </summary>
    private void InputMethodOn()
    {
        if (!_ime.Value || _imeChanged) return;
        _imeBefore = Input.imeCompositionMode;
        Input.imeCompositionMode = IMECompositionMode.On;
        _imeChanged = true;
        Debug($"input method on (was {_imeBefore}, selected={Input.imeIsSelected})");
    }

    private void InputMethodRestore()
    {
        if (!_imeChanged) return;
        _imeChanged = false;
        Input.imeCompositionMode = _imeBefore;
    }

    private bool MenuOpen()
    {
        if (_menuCheckBroken) return false;
        try { return UIScreen.AnyOpened; }
        catch (Exception e)
        {
            _menuCheckBroken = true;
            Core.Log.Warning($"[{Name}] menu detection failed, disabling it: {e.Message}");
            return false;
        }
    }

    private void Debug(string msg)
    {
        if (_log.Value || Verbose) Core.Log.Msg($"[{Name}] {msg}");
    }
}
