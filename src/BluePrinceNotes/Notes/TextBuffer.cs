using System;
using System.Collections.Generic;
using System.Text;

namespace BluePrinceNotes.Notes;

/// <summary>
/// The text being edited, with a caret, a selection and undo. Indexes are UTF-16 positions in the text; the caret
/// never ends up between the two halves of a surrogate pair. Line breaks are always "\n".
/// Visual moves (up/down a wrapped line) need the layout and live in the view; this class only has logical ones.
/// </summary>
internal sealed class TextBuffer
{
    private const int MaxUndo = 200;
    private const double UndoGroupTime = 1.0;   // typing within this many seconds is undone in one step

    private enum EditKind { None, Type, Delete, Other }

    private readonly struct Snapshot
    {
        public readonly string Text;
        public readonly int Caret, Anchor;
        public Snapshot(string text, int caret, int anchor) { Text = text; Caret = caret; Anchor = anchor; }
    }

    private readonly StringBuilder _sb = new();
    private readonly List<Snapshot> _undo = new(), _redo = new();
    private EditKind _lastKind;
    private double _lastEditAt = double.NegativeInfinity;
    private string _text = "";
    private bool _textStale;

    public TextBuffer(int maxLength) => MaxLength = maxLength;

    public int MaxLength { get; }
    public int Caret { get; private set; }
    /// <summary>The other end of the selection; equal to Caret when nothing is selected.</summary>
    public int Anchor { get; private set; }
    /// <summary>Goes up on every text change.</summary>
    public int Version { get; private set; }
    public int Length => _sb.Length;
    public bool HasSelection => Anchor != Caret;
    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionEnd => Math.Max(Anchor, Caret);

    public string Text
    {
        get
        {
            if (_textStale) { _text = _sb.ToString(); _textStale = false; }
            return _text;
        }
    }

    public string SelectedText => HasSelection ? _sb.ToString(SelectionStart, SelectionEnd - SelectionStart) : "";

    /// <summary>Starts over with this text, caret at the end, no undo history.</summary>
    public void Reset(string text)
    {
        _sb.Clear().Append(Clean(text ?? "", MaxLength));
        Caret = Anchor = _sb.Length;
        _undo.Clear();
        _redo.Clear();
        _lastKind = EditKind.None;
        Changed();
    }

    // ------------------------------------------------------------------ editing

    /// <summary>
    /// Types text at the caret, replacing the selection. Control characters other than newlines are dropped.
    /// A paste is always its own undo step.
    /// </summary>
    public void Insert(string s, double now, bool paste = false)
    {
        s = Clean(s, int.MaxValue);
        // Trim to the room left before starting the edit, so a key at the limit changes nothing at all.
        int room = MaxLength - (_sb.Length - (SelectionEnd - SelectionStart));
        if (s.Length > room) s = TrimToWhole(s, room);
        if (s.Length == 0 && !HasSelection) return;
        bool wordEnd = s.Length > 0 && char.IsWhiteSpace(s[s.Length - 1]);
        BeginEdit(HasSelection || paste ? EditKind.Other : EditKind.Type, now);
        DeleteSelectionRaw();
        _sb.Insert(Caret, s);
        Caret = Anchor = Caret + s.Length;
        Changed();
        if (wordEnd) _lastKind = EditKind.None;  // the next word is its own undo step
    }

    public void Backspace(bool word, double now)
    {
        if (HasSelection) { DeleteSelection(now); return; }
        if (Caret == 0) return;
        int from = word ? WordLeft(Caret) : StepLeft(Caret);
        BeginEdit(EditKind.Delete, now);
        _sb.Remove(from, Caret - from);
        Caret = Anchor = from;
        Changed();
    }

    public void Delete(bool word, double now)
    {
        if (HasSelection) { DeleteSelection(now); return; }
        if (Caret >= _sb.Length) return;
        int to = word ? WordRight(Caret) : StepRight(Caret);
        BeginEdit(EditKind.Delete, now);
        _sb.Remove(Caret, to - Caret);
        Changed();
    }

    public void DeleteSelection(double now)
    {
        if (!HasSelection) return;
        BeginEdit(EditKind.Other, now);
        DeleteSelectionRaw();
        Changed();
    }

    private void DeleteSelectionRaw()
    {
        if (!HasSelection) return;
        int start = SelectionStart;
        _sb.Remove(start, SelectionEnd - start);
        Caret = Anchor = start;
    }

    // ------------------------------------------------------------------ caret

    /// <summary>Moves the caret; with extend, the selection grows from its anchor.</summary>
    public void MoveTo(int index, bool extend)
    {
        index = Snap(Math.Clamp(index, 0, _sb.Length));
        Caret = index;
        if (!extend) Anchor = index;
        _lastKind = EditKind.None;
    }

    public void Left(bool word, bool extend)
    {
        if (HasSelection && !extend) { MoveTo(SelectionStart, false); return; }
        MoveTo(word ? WordLeft(Caret) : StepLeft(Caret), extend);
    }

    public void Right(bool word, bool extend)
    {
        if (HasSelection && !extend) { MoveTo(SelectionEnd, false); return; }
        MoveTo(word ? WordRight(Caret) : StepRight(Caret), extend);
    }

    /// <summary>Start of the logical line (after the previous "\n").</summary>
    public int LineStart(int index)
    {
        for (int i = Math.Min(index, _sb.Length) - 1; i >= 0; i--)
            if (_sb[i] == '\n') return i + 1;
        return 0;
    }

    /// <summary>End of the logical line (before the next "\n").</summary>
    public int LineEnd(int index)
    {
        for (int i = Math.Max(index, 0); i < _sb.Length; i++)
            if (_sb[i] == '\n') return i;
        return _sb.Length;
    }

    public void SelectAll()
    {
        Anchor = 0;
        Caret = _sb.Length;
        _lastKind = EditKind.None;
    }

    // ------------------------------------------------------------------ undo

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Take());
        Restore(Pop(_undo));
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Take());
        Restore(Pop(_redo));
    }

    private void BeginEdit(EditKind kind, double now)
    {
        // Typing (or deleting) in one run is one undo step; anything else starts a new one.
        bool sameRun = kind == _lastKind && kind != EditKind.Other && now - _lastEditAt < UndoGroupTime;
        if (!sameRun)
        {
            _undo.Add(Take());
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }
        _redo.Clear();
        _lastKind = kind;
        _lastEditAt = now;
    }

    private Snapshot Take() => new(Text, Caret, Anchor);

    private static Snapshot Pop(List<Snapshot> list)
    {
        var s = list[list.Count - 1];
        list.RemoveAt(list.Count - 1);
        return s;
    }

    private void Restore(Snapshot s)
    {
        _sb.Clear().Append(s.Text);
        Caret = Math.Clamp(s.Caret, 0, _sb.Length);
        Anchor = Math.Clamp(s.Anchor, 0, _sb.Length);
        _lastKind = EditKind.None;
        Changed();
    }

    // ------------------------------------------------------------------ helpers

    private void Changed()
    {
        _textStale = true;
        Version++;
    }

    private int StepLeft(int i)
    {
        if (i <= 0) return 0;
        i--;
        if (i > 0 && char.IsLowSurrogate(_sb[i]) && char.IsHighSurrogate(_sb[i - 1])) i--;
        return i;
    }

    private int StepRight(int i)
    {
        if (i >= _sb.Length) return _sb.Length;
        i++;
        if (i < _sb.Length && char.IsLowSurrogate(_sb[i]) && char.IsHighSurrogate(_sb[i - 1])) i++;
        return i;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c);

    /// <summary>Ctrl+Left: back over spaces, then over a word (or a run of punctuation).</summary>
    private int WordLeft(int i)
    {
        while (i > 0 && char.IsWhiteSpace(_sb[i - 1]) && _sb[i - 1] != '\n') i--;
        if (i > 0 && _sb[i - 1] == '\n') return i - 1;
        if (i == 0) return 0;
        bool word = IsWord(_sb[i - 1]);
        while (i > 0 && !char.IsWhiteSpace(_sb[i - 1]) && IsWord(_sb[i - 1]) == word) i--;
        return i;
    }

    /// <summary>Ctrl+Right: over a word (or a run of punctuation), then over spaces.</summary>
    private int WordRight(int i)
    {
        int n = _sb.Length;
        if (i < n && _sb[i] == '\n') return i + 1;
        if (i < n && !char.IsWhiteSpace(_sb[i]))
        {
            bool word = IsWord(_sb[i]);
            while (i < n && !char.IsWhiteSpace(_sb[i]) && IsWord(_sb[i]) == word) i++;
        }
        while (i < n && char.IsWhiteSpace(_sb[i]) && _sb[i] != '\n') i++;
        return i;
    }

    /// <summary>Never leave the caret inside a surrogate pair.</summary>
    private int Snap(int i)
    {
        if (i > 0 && i < _sb.Length && char.IsLowSurrogate(_sb[i]) && char.IsHighSurrogate(_sb[i - 1])) return i - 1;
        return i;
    }

    /// <summary>"\r\n" and "\r" become "\n"; tabs become spaces; other control characters are dropped.</summary>
    internal static string Clean(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length && sb.Length <= max; i++)  // one past the limit, then trim whole
        {
            char c = s[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
            else if (c == '\n') sb.Append('\n');
            else if (c == '\t') sb.Append("    ");
            else if (c >= 0x20 && c != 0x7F && !(c >= 0x80 && c < 0xA0)) sb.Append(c);
        }
        return TrimToWhole(sb.ToString(), max);
    }

    private static string TrimToWhole(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length <= max) return s;
        if (char.IsHighSurrogate(s[max - 1])) max--;
        return s.Substring(0, max);
    }
}
