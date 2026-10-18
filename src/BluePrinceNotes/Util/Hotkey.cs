using System;
using UnityEngine;

namespace BluePrinceNotes.Util;

/// <summary>A key with optional modifiers, written like "F2" or "Ctrl+F2".</summary>
internal readonly struct Hotkey
{
    private readonly KeyCode _key;
    private readonly bool _shift, _ctrl, _alt;

    public Hotkey(KeyCode key, bool shift = false, bool ctrl = false, bool alt = false)
    {
        _key = key; _shift = shift; _ctrl = ctrl; _alt = alt;
    }

    public KeyCode Key => _key;

    /// <summary>"F2", "Ctrl+F2", "Ctrl+Alt+N".</summary>
    public static Hotkey Parse(string text, Hotkey fallback, string feature)
    {
        bool shift = false, ctrl = false, alt = false;
        KeyCode key = KeyCode.None;
        foreach (var raw in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "shift": shift = true; break;
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                default:
                    if (!Enum.TryParse(raw, true, out key)) key = KeyCode.None;
                    break;
            }
        }
        if (key == KeyCode.None)
        {
            Core.Log.Warning($"[{feature}] can't parse hotkey '{text}', using the default");
            return fallback;
        }
        return new Hotkey(key, shift, ctrl, alt);
    }

    /// <summary>
    /// Pressed this frame. Listed modifiers must be held; unlisted ones are ignored, so plain "F2" also fires
    /// while Shift (the game's stroll button) is held.
    /// </summary>
    public bool Down()
    {
        if (_key == KeyCode.None || !Input.GetKeyDown(_key)) return false;
        if (_shift && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) return false;
        if (_ctrl && !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return false;
        if (_alt && !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))) return false;
        return true;
    }

    /// <summary>The main key is held down (modifiers not checked).</summary>
    public bool Held() => _key != KeyCode.None && Input.GetKey(_key);
}
