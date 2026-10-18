using System;
using System.Collections.Generic;
using System.IO;
using BluePrinceNotes.Game;
using BluePrinceNotes.Notes;
using MelonLoader.Utils;
using UnityEngine;

namespace BluePrinceNotes.UI;

/// <summary>
/// What the notepad shows and does while it has the keyboard: the list of notes and the editor.
///
/// List: newest change first; the first line of a note is its title, with the day and place it was started below.
/// Typing searches (accents and case ignored), Up/Down pick, Enter opens, Ctrl+N starts a new note (Enter starts one
/// with the search text when nothing matches), Delete twice deletes. Esc clears the search, then closes.
/// Editor: arrows (Ctrl = by word), Home/End (Ctrl = whole note), PageUp/PageDown, Shift selects, Ctrl+A/C/X/V,
/// Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z), Backspace/Delete (Ctrl = word), mouse wheel scrolls. Esc goes back to the list.
/// A note left empty is deleted.
///
/// Notes are saved a moment after the last change, when the editor or the notepad closes, when the game loses
/// focus, on scene changes and on quit (see NoteStore).
/// </summary>
internal sealed class Notebook
{
    private const int MaxNoteLength = 10000;
    private const int MaxSearch = 60;
    private const float DeleteConfirmTime = 4f;

    private enum Mode { List, Editor }

    private readonly NoteStore _store;
    private bool _loaded;
    private Mode _mode = Mode.List;

    // List
    private string _search = "";
    private readonly List<Note> _shown = new();
    private readonly List<ListRow> _rows = new();
    private bool _rowsStale = true;
    private int _selected;
    private Note _deleteArmed;
    private float _deleteArmedAt;

    // Editor
    private Note _editing;
    private bool _editingAdded;
    private readonly TextBuffer _buffer = new(MaxNoteLength);
    private float _goalX = float.NaN;
    private float _blinkFrom;

    private NoteContext _openContext = new();
    private readonly KeyRepeat _repeat = new();

    public Notebook()
    {
        var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "BluePrinceNotes");
        _store = new NoteStore(dir, m => Core.Log.Msg($"[Notepad] {m}"), m => Core.Log.Warning($"[Notepad] {m}"));
    }

    private static double Now => Time.unscaledTimeAsDouble;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>The notepad opened: remember where the player is, for notes started while it's open.</summary>
    public void Opened()
    {
        if (!_loaded)
        {
            _loaded = true;
            _store.Load();
        }
        try { _openContext = GameContext.Capture(); }
        catch (Exception e)
        {
            _openContext = new NoteContext();
            Core.Log.Warning($"[Notepad] reading game context: {e.Message}");
        }
        _mode = Mode.List;
        _search = "";
        _selected = 0;
        _deleteArmed = null;
        _rowsStale = true;
        _repeat.Reset();
    }

    /// <summary>The notepad closed (for any reason): leave the editor and save.</summary>
    public void Closed()
    {
        if (_mode == Mode.Editor) LeaveEditor();
        _deleteArmed = null;
        Flush();
    }

    /// <summary>Every frame, open or not: finishes a pending save.</summary>
    public void Tick() => _store.Tick(Now);

    public void Flush()
    {
        if (_loaded) _store.Flush(Now);
    }

    /// <summary>The game lost focus: save, and stop repeating a key whose release may go to another window.</summary>
    public void FocusLost()
    {
        _repeat.Reset();
        Flush();
    }

    // ------------------------------------------------------------------ input

    /// <summary>Handles this frame's keys. True when the notepad should close (Esc on the list).</summary>
    public bool HandleInput(NotepadView view)
    {
        bool esc = Input.GetKeyDown(KeyCode.Escape);
        return _mode == Mode.List ? ListInput(esc) : EditorInput(view, esc);
    }

    private static bool Ctrl =>
        (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
        && !Input.GetKey(KeyCode.RightAlt) && !Input.GetKey(KeyCode.AltGr);  // AltGr arrives as Ctrl+RightAlt

    private static bool Shift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    private bool ListInput(bool esc)
    {
        if (esc)
        {
            if (_search.Length == 0) return true;
            SetSearch("");
            return false;
        }
        bool ctrl = Ctrl;
        if (ctrl && Input.GetKeyDown(KeyCode.N))
        {
            NewNote("");
            return false;
        }

        // Typing searches. Read before Enter, so a letter typed in the same frame as Enter still counts.
        if (!ctrl)
        {
            var typed = Input.inputString;
            if (!string.IsNullOrEmpty(typed))
            {
                var s = _search;
                foreach (var c in typed)
                {
                    if (c == '\b') { if (s.Length > 0) s = s.Substring(0, s.Length - (s.Length >= 2 && char.IsLowSurrogate(s[s.Length - 1]) ? 2 : 1)); }
                    else if (c >= 0x20 && c != 0x7F && s.Length < MaxSearch) s += c;
                }
                if (s != _search) SetSearch(s);
            }
        }

        int count = Shown.Count;
        int before = _selected;
        if (_repeat.Fire(KeyCode.UpArrow)) _selected--;
        if (_repeat.Fire(KeyCode.DownArrow)) _selected++;
        if (_repeat.Fire(KeyCode.PageUp)) _selected -= 8;
        if (_repeat.Fire(KeyCode.PageDown)) _selected += 8;
        if (Input.GetKeyDown(KeyCode.Home)) _selected = 0;
        if (Input.GetKeyDown(KeyCode.End)) _selected = count - 1;
        _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, count - 1));
        if (_selected != before && _deleteArmed != null)
        {
            _deleteArmed = null;
            _rowsStale = true;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (count > 0) OpenNote(Shown[_selected]);
            else if (_search.Trim().Length > 0) NewNote(_search.Trim());
            return false;
        }

        if (Input.GetKeyDown(KeyCode.Delete) && count > 0)
        {
            var target = Shown[_selected];
            if (_deleteArmed == target && Time.unscaledTime - _deleteArmedAt < DeleteConfirmTime)
            {
                _store.Remove(target, Now);
                _deleteArmed = null;
                _rowsStale = true;
                Core.Log.Msg($"[Notepad] deleted a note ({_store.Notes.Count} left)");
            }
            else
            {
                _deleteArmed = target;
                _deleteArmedAt = Time.unscaledTime;
                _rowsStale = true;
            }
            return false;
        }
        if (_deleteArmed != null && Time.unscaledTime - _deleteArmedAt >= DeleteConfirmTime)
        {
            _deleteArmed = null;
            _rowsStale = true;
        }
        return false;
    }

    private void SetSearch(string s)
    {
        _search = s;
        _selected = 0;
        _deleteArmed = null;
        _rowsStale = true;
    }

    private bool EditorInput(NotepadView view, bool esc)
    {
        if (esc)
        {
            LeaveEditor();
            return false;
        }
        double now = Now;
        int version = _buffer.Version, caret = _buffer.Caret, anchor = _buffer.Anchor;
        bool ctrl = Ctrl, shift = Shift;
        bool composing = !string.IsNullOrEmpty(Input.compositionString);

        if (ctrl)
        {
            if (Input.GetKeyDown(KeyCode.A)) _buffer.SelectAll();
            if (Input.GetKeyDown(KeyCode.C) && _buffer.HasSelection) Copy(_buffer.SelectedText);
            if (Input.GetKeyDown(KeyCode.X) && _buffer.HasSelection)
            {
                Copy(_buffer.SelectedText);
                _buffer.DeleteSelection(now);
            }
            if (Input.GetKeyDown(KeyCode.V)) Paste(now);
            if (Input.GetKeyDown(KeyCode.Z)) { if (shift) _buffer.Redo(); else _buffer.Undo(); }
            if (Input.GetKeyDown(KeyCode.Y)) _buffer.Redo();
        }

        if (!composing)
        {
            bool vertical = false;
            if (_repeat.Fire(KeyCode.LeftArrow)) _buffer.Left(ctrl, shift);
            if (_repeat.Fire(KeyCode.RightArrow)) _buffer.Right(ctrl, shift);
            if (_repeat.Fire(KeyCode.UpArrow)) { _buffer.MoveTo(view.Vertical(_buffer.Caret, -1, ref _goalX), shift); vertical = true; }
            if (_repeat.Fire(KeyCode.DownArrow)) { _buffer.MoveTo(view.Vertical(_buffer.Caret, 1, ref _goalX), shift); vertical = true; }
            if (_repeat.Fire(KeyCode.PageUp)) { _buffer.MoveTo(view.Page(_buffer.Caret, -1, ref _goalX), shift); vertical = true; }
            if (_repeat.Fire(KeyCode.PageDown)) { _buffer.MoveTo(view.Page(_buffer.Caret, 1, ref _goalX), shift); vertical = true; }
            if (Input.GetKeyDown(KeyCode.Home)) _buffer.MoveTo(ctrl ? 0 : view.Home(_buffer.Caret), shift);
            if (Input.GetKeyDown(KeyCode.End)) _buffer.MoveTo(ctrl ? _buffer.Length : view.End(_buffer.Caret), shift);
            if (_repeat.Fire(KeyCode.Delete)) _buffer.Delete(ctrl, now);
            // Ctrl+Backspace by key: what it puts in inputString differs between systems ('\b' or DEL), so ignore that.
            if (ctrl && _repeat.Fire(KeyCode.Backspace)) _buffer.Backspace(true, now);
            // Tab never reaches inputString under Proton.
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (!ctrl && !alt && _repeat.Fire(KeyCode.Tab)) _buffer.Insert("    ", now);
            if (!vertical && (_buffer.Caret != caret || _buffer.Version != version)) _goalX = float.NaN;
        }

        // Typed characters. Backspace comes as '\b' with the OS key repeat. While Ctrl is held only newlines count
        // (Ctrl+letters arrive as control characters; Ctrl+Backspace is handled above); tabs are handled above.
        var typed = Input.inputString;
        if (!string.IsNullOrEmpty(typed))
        {
            int start = 0;
            for (int i = 0; i <= typed.Length; i++)
            {
                char c = i < typed.Length ? typed[i] : '\0';
                bool special = i == typed.Length || c == '\b' || c == 0x7F || c == '\t' || (ctrl && c != '\r' && c != '\n');
                if (!special) continue;
                if (i > start) _buffer.Insert(typed.Substring(start, i - start), now);
                if (c == '\b' && !ctrl) _buffer.Backspace(false, now);
                start = i + 1;
            }
            _goalX = float.NaN;
        }

        float wheel = Input.mouseScrollDelta.y;
        if (wheel != 0f) view.ScrollLines(-wheel * 3f);

        if (_buffer.Version != version)
        {
            _editing.Text = _buffer.Text;
            _editing.Edited = DateTime.UtcNow;
            if (!_editingAdded && !_editing.IsBlank)
            {
                _store.Add(_editing, now);
                _editingAdded = true;
            }
            else if (_editingAdded) _store.Changed(now);
            _rowsStale = true;
        }
        if (_buffer.Version != version || _buffer.Caret != caret || _buffer.Anchor != anchor)
        {
            _blinkFrom = Time.unscaledTime;
            view.FollowCaret();
        }
        return false;
    }

    private static void Copy(string s)
    {
        try { GUIUtility.systemCopyBuffer = s; }
        catch (Exception e) { Core.Log.Warning($"[Notepad] copy failed: {e.Message}"); }
    }

    private void Paste(double now)
    {
        string clip;
        try { clip = GUIUtility.systemCopyBuffer; }
        catch (Exception e)
        {
            Core.Log.Warning($"[Notepad] paste failed: {e.Message}");
            return;
        }
        if (!string.IsNullOrEmpty(clip)) _buffer.Insert(clip, now, paste: true);
    }

    // ------------------------------------------------------------------ notes

    private void NewNote(string text)
    {
        var now = DateTime.UtcNow;
        var note = new Note { Created = now, Edited = now, Text = text, Context = Copy(_openContext) };
        _editing = note;
        _editingAdded = false;
        if (!note.IsBlank)
        {
            _store.Add(note, Now);
            _editingAdded = true;
        }
        Edit(note);
    }

    private void OpenNote(Note note)
    {
        _editing = note;
        _editingAdded = true;
        Edit(note);
    }

    private void Edit(Note note)
    {
        if (TextBuffer.Clean(note.Text, int.MaxValue).Length > MaxNoteLength)
            Core.Log.Warning($"[Notepad] this note is longer than {MaxNoteLength} characters; editing it keeps the first {MaxNoteLength}");
        _buffer.Reset(note.Text);
        _mode = Mode.Editor;
        _goalX = float.NaN;
        _blinkFrom = Time.unscaledTime;
        _deleteArmed = null;
        _resetScroll = true;
    }

    private bool _resetScroll;

    private void LeaveEditor()
    {
        if (_editing != null)
        {
            if (_editing.IsBlank)
            {
                if (_editingAdded) _store.Remove(_editing, Now);
            }
            else
            {
                // Put the selection on the note just edited.
                SetSearch("");
                _selected = Math.Max(0, Shown.IndexOf(_editing));
            }
        }
        _editing = null;
        _mode = Mode.List;
        _rowsStale = true;
        Flush();
    }

    private static NoteContext Copy(NoteContext c) => new()
    {
        Day = c.Day, Area = c.Area, Room = c.Room, Rank = c.Rank, Tile = c.Tile,
        SaveSlot = c.SaveSlot, Seed = c.Seed, DayStartClock = c.DayStartClock,
    };

    /// <summary>The notes matching the search, most recently changed first.</summary>
    private List<Note> Shown
    {
        get
        {
            if (!_rowsStale) return _shown;
            _rowsStale = false;
            _shown.Clear();
            var q = TextFold.Fold(_search.Trim());
            foreach (var n in _store.Notes)
            {
                if (q.Length > 0 && !TextFold.Fold(n.Text).Contains(q) && !TextFold.Fold(n.Context?.Room).Contains(q))
                    continue;
                _shown.Add(n);
            }
            _shown.Sort((a, b) => b.Edited.CompareTo(a.Edited));
            _rows.Clear();
            foreach (var n in _shown)
            {
                var title = n.Title;
                if (title.Length == 0) title = "(empty)";
                bool deleting = n == _deleteArmed;
                var meta = deleting ? "Press Delete again to delete this note" : GameContext.Describe(n.Context);
                _rows.Add(new ListRow(title, meta, deleting));
            }
            return _shown;
        }
    }

    // ------------------------------------------------------------------ drawing

    private string SaveWarning => _loaded && _store.NotSaving ? " · NOT SAVED (see the MelonLoader log)" : "";

    public void Render(NotepadView view, string closeKey)
    {
        if (_mode == Mode.List)
        {
            var shown = Shown;
            var header = (_store.Notes.Count == 0 ? "Notes" : $"Notes ({_store.Notes.Count})") + SaveWarning;
            var hint = $"Enter open · Ctrl+N new · Delete delete · type to search · Esc or {closeKey} close";
            var search = _search.Length > 0 ? $"Search: {_search}" : null;
            var empty = _search.Trim().Length > 0
                ? "No notes match. Press Enter to start one with this text."
                : "No notes yet. Press Ctrl+N to start one.";
            view.ShowList(header, hint, search, _rows, shown.Count > 0 ? _selected : -1, empty);
            return;
        }

        if (_resetScroll)
        {
            _resetScroll = false;
            view.ResetScroll();
        }
        var ctx = GameContext.Describe(_editing?.Context);
        bool caretOn = ((Time.unscaledTime - _blinkFrom) % 1f) < 0.55f;
        string composing = "";
        try { composing = Input.compositionString ?? ""; } catch { /* no input method */ }
        view.ShowEditor((ctx.Length > 0 ? ctx : "New note") + SaveWarning, $"Esc back to the list · {closeKey} close",
            _buffer.Text, _buffer.Caret, _buffer.Anchor, caretOn, composing);
    }

    // ------------------------------------------------------------------ key repeat

    /// <summary>Repeats a held key: first after 0.4 s, then 30 times a second. One key repeats at a time.</summary>
    private sealed class KeyRepeat
    {
        private KeyCode _held = KeyCode.None;
        private float _next;

        public void Reset() => _held = KeyCode.None;

        public bool Fire(KeyCode k)
        {
            if (!Application.isFocused)
            {
                _held = KeyCode.None;
                return false;
            }
            float t = Time.unscaledTime;
            if (Input.GetKeyDown(k))
            {
                _held = k;
                _next = t + 0.4f;
                return true;
            }
            if (_held != k) return false;
            if (!Input.GetKey(k))
            {
                _held = KeyCode.None;
                return false;
            }
            if (t < _next) return false;
            _next = t + 1f / 30f;
            return true;
        }
    }
}
