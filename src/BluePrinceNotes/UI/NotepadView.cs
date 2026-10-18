using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BluePrinceNotes.UI;

/// <summary>One line in the list of notes.</summary>
internal readonly struct ListRow
{
    public readonly string Title, Meta;
    public ListRow(string title, string meta) { Title = title; Meta = meta; }
}

/// <summary>
/// The notepad panel: its own ScreenSpaceOverlay canvas (sortingOrder 600, above Blue Prince QoL's overlays at
/// 500-502, below the letterbox camera at 9999 and UnityExplorer at 30000). A header, a content area that shows
/// either the list of notes or the editor, and a hint line. Built on first show and destroyed on every scene
/// load, because the game fonts are scene assets.
///
/// The editor draws its own caret and selection from TMP's layout (textInfo), cached once per text change.
/// </summary>
internal sealed class NotepadView
{
    private const string RootName = "BluePrinceNotes Notepad";
    private const string FallbackFont = "TypeNeat SDF";
    private const int MaxRows = 40;

    private static readonly Color Ink = new(1f, 0.97f, 0.88f, 1f);
    private static readonly Color Dim = new(1f, 0.97f, 0.88f, 0.55f);
    private static readonly Color RowMark = new(1f, 0.97f, 0.88f, 0.12f);
    private static readonly Color SelectionColor = new(0.40f, 0.60f, 1f, 0.40f);

    private GameObject _root;
    private RectTransform _panel, _content, _viewport, _listArea;
    private TextMeshProUGUI _header, _hint, _search, _empty, _body;
    private Image _caret;
    private readonly List<Image> _selection = new();
    private readonly List<Row> _rows = new();
    private string _builtFor;
    private float _nextTry;
    private float _size, _panelW;
    private float _em;    // one line of the font, in font sizes: TMP draws nothing in a box shorter than that

    private sealed class Row
    {
        public RectTransform Rect;
        public Image Mark;
        public TextMeshProUGUI Title, Meta;
    }

    public bool Visible => _root != null && _root.activeSelf;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Builds the panel if needed and shows it. False while no usable font is loaded.</summary>
    public bool Begin(string fontName, float fontScale)
    {
        if (_body == null || _header == null || _body.font == null || _builtFor != fontName)
        {
            Destroy();
            if (Time.unscaledTime < _nextTry) return false;
            _nextTry = Time.unscaledTime + 1f;
            if (!TryCreate(fontName)) return false;
        }
        if (!_root.activeSelf) _root.SetActive(true);
        Layout(fontScale);
        return true;
    }

    public void Hide()
    {
        if (_root != null && _root.activeSelf) _root.SetActive(false);
    }

    public void Destroy()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;
        _panel = _content = _viewport = _listArea = null;
        _header = _hint = _search = _empty = _body = null;
        _caret = null;
        _selection.Clear();
        _rows.Clear();
        _builtFor = null;
        _layoutKey = null;
        _shown.Clear();
    }

    private void Layout(float fontScale)
    {
        float size = Mathf.Max(12f, Screen.height * Mathf.Clamp(fontScale, 0.01f, 0.1f));
        float w = Mathf.Min(Screen.width - 40f, Mathf.Max(Screen.width * 0.55f, size * 24f));
        if (Mathf.Approximately(size, _size) && Mathf.Approximately(w, _panelW)) return;
        _size = size;
        _panelW = w;
        float h = Screen.height * 0.72f;
        float pad = size * 0.9f;
        _panel.sizeDelta = new Vector2(w, h);
        var face = _body.font.faceInfo;
        _em = Mathf.Max(1.2f, (face.ascentLine - face.descentLine) / Mathf.Max(1f, face.pointSize)) + 0.05f;

        _header.fontSize = size * 0.9f;
        float headerH = Mathf.Max(size * 1.3f, 0.9f * size * _em);
        Place(_header.rectTransform, 1f, pad, -pad * 0.6f - headerH, -pad, -pad * 0.6f);
        _hint.fontSize = size * 0.6f;
        float hintH = 0.6f * size * _em;
        Place(_hint.rectTransform, 0f, pad, pad * 0.5f, -pad, pad * 0.5f + hintH);

        // Content: between the header and the hint line.
        _content.anchorMin = Vector2.zero;
        _content.anchorMax = Vector2.one;
        _content.offsetMin = new Vector2(pad, pad * 0.5f + hintH + 0.3f * size);
        _content.offsetMax = new Vector2(-pad, -(pad * 0.6f + headerH + 0.3f * size));

        _search.fontSize = size * 0.8f;
        _empty.fontSize = size * 0.8f;
        _body.fontSize = size;
        foreach (var r in _rows)
        {
            r.Title.fontSize = size;
            r.Meta.fontSize = size * 0.6f;
        }
        _caret.rectTransform.sizeDelta = new Vector2(Mathf.Max(2f, size * 0.07f), size);
        _layoutKey = null;
    }

    /// <summary>Stretches horizontally on an anchor line (0 = bottom, 1 = top), with offsets from it.</summary>
    private static void Place(RectTransform rt, float y, float left, float bottom, float right, float top)
    {
        rt.anchorMin = new Vector2(0f, y);
        rt.anchorMax = new Vector2(1f, y);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(right, top);
    }

    // ------------------------------------------------------------------ list

    private int _listTop;

    public void ShowList(string header, string hint, string search, IReadOnlyList<ListRow> rows, int selected, string empty)
    {
        _viewport.gameObject.SetActive(false);
        _listArea.gameObject.SetActive(true);
        Set(_header, header);
        Set(_hint, hint);

        float titleH = _size * _em, metaH = 0.6f * _size * _em;
        float rowH = titleH + metaH + 0.35f * _size;
        float top = 0f;
        bool searching = !string.IsNullOrEmpty(search);
        if (_search.gameObject.activeSelf != searching) _search.gameObject.SetActive(searching);
        if (searching)
        {
            Set(_search, search);
            float searchH = 0.8f * _size * _em;
            Place(_search.rectTransform, 1f, 0f, -searchH, 0f, 0f);
            top = searchH + 0.3f * _size;
        }

        float areaH = _content.rect.height - top;
        int fit = Mathf.Clamp(Mathf.FloorToInt(areaH / rowH), 1, MaxRows);
        int count = rows?.Count ?? 0;
        if (selected >= 0 && selected < _listTop) _listTop = selected;
        if (selected >= _listTop + fit) _listTop = selected - fit + 1;
        _listTop = Mathf.Clamp(_listTop, 0, Mathf.Max(0, count - fit));

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            int n = _listTop + i;
            bool used = i < fit && n < count;
            if (row.Rect.gameObject.activeSelf != used) row.Rect.gameObject.SetActive(used);
            if (!used) continue;
            row.Rect.anchorMin = new Vector2(0f, 1f);
            row.Rect.anchorMax = new Vector2(1f, 1f);
            row.Rect.pivot = new Vector2(0.5f, 1f);
            row.Rect.anchoredPosition = new Vector2(0f, -top - i * rowH);
            row.Rect.sizeDelta = new Vector2(0f, rowH - 0.15f * _size);
            float y = 0.05f * _size;
            Place(row.Title.rectTransform, 1f, _size * 0.3f, -y - titleH, -_size * 0.3f, -y);
            Place(row.Meta.rectTransform, 1f, _size * 0.3f, -y - titleH - metaH, -_size * 0.3f, -y - titleH);
            Set(row.Title, rows[n].Title);
            Set(row.Meta, rows[n].Meta);
            row.Mark.enabled = n == selected;
        }
        if (_empty.gameObject.activeSelf != (count == 0)) _empty.gameObject.SetActive(count == 0);
        if (count == 0) Set(_empty, empty);
    }

    // ------------------------------------------------------------------ editor

    // Layout cache, rebuilt when the shown text, the width or the size changes.
    private string _layoutKey;
    private string _laidText = "";
    private int _n, _lines;
    private float[] _origin = new float[256], _advance = new float[256];
    private int[] _lineOf = new int[256], _index = new int[256];
    private char[] _char = new char[256];
    private int[] _kAt = new int[257];
    private int[] _lFirst = new int[32], _lLast = new int[32];
    private float[] _lAsc = new float[32], _lDesc = new float[32];
    private float _left, _right, _lineH, _asc0, _desc0;
    private float _scroll;

    /// <summary>
    /// Shows text with a caret and selection. caret/anchor index into <paramref name="text"/>; the input method's
    /// pending text (if any) is drawn at the caret.
    /// </summary>
    public void ShowEditor(string header, string hint, string text, int caret, int anchor, bool caretOn, string composing)
    {
        _listArea.gameObject.SetActive(false);
        _viewport.gameObject.SetActive(true);
        Set(_header, header);
        Set(_hint, hint);

        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        anchor = Math.Clamp(anchor, 0, text.Length);
        if (!string.IsNullOrEmpty(composing))
        {
            text = text.Substring(0, caret) + composing + text.Substring(caret);
            caret += composing.Length;
            anchor = caret;
        }
        Relayout(text);

        // Caret and selection, in the body's local space (top-anchored, so y goes down from 0).
        var (cx, cl) = Pos(caret);
        cx = Mathf.Min(cx, _right - _caret.rectTransform.sizeDelta.x);  // trailing spaces run past the edge
        var (asc, desc) = LineY(cl);
        _caret.enabled = caretOn;
        _caret.rectTransform.anchoredPosition = new Vector2(cx, desc);
        _caret.rectTransform.sizeDelta = new Vector2(_caret.rectTransform.sizeDelta.x, asc - desc);
        DrawSelection(Math.Min(caret, anchor), Math.Max(caret, anchor));

        // Keep the caret line in view.
        if (_followCaret)
        {
            float viewH = _viewport.rect.height;
            if (asc > -_scroll) _scroll = -asc;
            if (desc < -_scroll - viewH) _scroll = -desc - viewH;
        }
        ClampScroll();
    }

    private bool _followCaret = true;

    /// <summary>Caret moved or text changed: bring the caret back into view.</summary>
    public void FollowCaret() => _followCaret = true;

    /// <summary>Scrolls the editor by a number of lines (mouse wheel); the caret may leave the view.</summary>
    public void ScrollLines(float lines)
    {
        if (_body == null || _lineH <= 0f) return;
        _scroll += lines * _lineH;
        _followCaret = false;
        ClampScroll();
    }

    public void ResetScroll()
    {
        _scroll = 0f;
        _followCaret = true;
    }

    private void ClampScroll()
    {
        float viewH = _viewport.rect.height;
        _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, ContentHeight() - viewH));
        _body.rectTransform.anchoredPosition = new Vector2(0f, _scroll);
    }

    private void Relayout(string text)
    {
        var key = $"{text.Length}:{text.GetHashCode()}:{_viewport.rect.width}:{_size}";
        if (key == _layoutKey) return;
        _layoutKey = key;
        _laidText = text;

        var br = _body.rectTransform;
        br.anchorMin = new Vector2(0f, 1f);
        br.anchorMax = new Vector2(1f, 1f);
        br.pivot = new Vector2(0.5f, 1f);
        br.sizeDelta = new Vector2(0f, _viewport.rect.height);
        _body.text = text;
        _body.ForceMeshUpdate();

        var info = _body.textInfo;
        _n = Math.Max(0, info.characterCount);
        _lines = Math.Max(0, info.lineCount);
        Grow(ref _origin, _n); Grow(ref _advance, _n); Grow(ref _lineOf, _n); Grow(ref _index, _n); Grow(ref _char, _n);
        Grow(ref _kAt, text.Length + 1);
        Grow(ref _lFirst, _lines); Grow(ref _lLast, _lines); Grow(ref _lAsc, _lines); Grow(ref _lDesc, _lines);

        var chars = info.characterInfo;
        if (chars == null) _n = 0;
        else if (_n > chars.Length) _n = chars.Length;
        if (!ReadDirect(chars))
        {
            for (int k = 0; k < _n; k++)
            {
                var c = chars[k];
                _origin[k] = c.origin;
                _advance[k] = c.xAdvance;
                _lineOf[k] = c.lineNumber;
                _index[k] = c.index;
                _char[k] = c.character;
            }
        }
        var lines = info.lineInfo;
        for (int l = 0; l < _lines; l++)
        {
            var li = lines[l];
            _lFirst[l] = li.firstCharacterIndex;
            _lLast[l] = li.lastCharacterIndex;
            _lAsc[l] = li.ascender;
            _lDesc[l] = li.descender;
        }
        // String index -> character info index (-1 where TMP made no entry, e.g. a surrogate's second half).
        for (int i = 0; i <= text.Length; i++) _kAt[i] = -1;
        for (int k = 0; k < _n; k++)
            if (_index[k] >= 0 && _index[k] < text.Length) _kAt[_index[k]] = k;

        _left = br.rect.xMin;
        _right = br.rect.xMax;
        var face = _body.font.faceInfo;
        float scale = _body.fontSize / Mathf.Max(1f, face.pointSize);
        _asc0 = 0f;
        _desc0 = -(face.ascentLine - face.descentLine) * scale;
        _lineH = _lines >= 2 ? _lAsc[0] - _lAsc[1] : face.lineHeight * scale;
        if (_lineH <= 0f) _lineH = face.lineHeight * scale;
    }

    // TMP_CharacterInfo is a big struct; reading it through the interop boxes every element (a native object, a GC
    // handle and several native calls each), which costs tens of ms per keystroke in a long note. The array holds
    // the structs inline, so the five fields are read straight from memory instead. The offsets come from IL2CPP's
    // own class data, and the first read is checked against the boxed values; any mismatch falls back for good.
    private static int _ciStride = -1, _oOrigin, _oAdvance, _oLine, _oIndex, _oChar;
    private static bool _ciChecked;

    private bool ReadDirect(Il2CppReferenceArray<TMP_CharacterInfo> chars)
    {
        if (_n == 0 || chars == null || !DirectLayout()) return false;
        IntPtr data = chars.Pointer + 4 * IntPtr.Size;  // after the array header, as Il2CppArrayBase does
        for (int k = 0; k < _n; k++)
        {
            IntPtr e = data + k * _ciStride;
            _origin[k] = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(e, _oOrigin));
            _advance[k] = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(e, _oAdvance));
            _lineOf[k] = Marshal.ReadInt32(e, _oLine);
            _index[k] = Marshal.ReadInt32(e, _oIndex);
            _char[k] = (char)Marshal.ReadInt16(e, _oChar);
        }
        GC.KeepAlive(chars);
        if (_ciChecked) return true;
        _ciChecked = true;
        if (Same(chars[0], 0) && Same(chars[_n - 1], _n - 1)) return true;
        _ciStride = 0;
        Core.Log.Warning("[Notepad] fast text layout reads didn't match, using the slow path");
        return false;
    }

    private bool Same(TMP_CharacterInfo c, int k) =>
        c.origin == _origin[k] && c.xAdvance == _advance[k] && c.lineNumber == _lineOf[k] && c.index == _index[k] && c.character == _char[k];

    private static bool DirectLayout()
    {
        if (_ciStride >= 0) return _ciStride > 0;
        _ciStride = 0;
        try
        {
            IntPtr cls = Il2CppClassPointerStore<TMP_CharacterInfo>.NativeClassPtr;
            if (cls == IntPtr.Zero) return false;
            uint align = 0;
            int stride = IL2CPP.il2cpp_class_value_size(cls, ref align);
            int header = 2 * IntPtr.Size;  // value-type field offsets count the boxed object's header
            int Off(string name)
            {
                IntPtr f = IL2CPP.GetIl2CppField(cls, name);
                return f == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(f) - header;
            }
            _oOrigin = Off("origin"); _oAdvance = Off("xAdvance"); _oLine = Off("lineNumber");
            _oIndex = Off("index"); _oChar = Off("character");
            bool Fits(int o, int size) => o >= 0 && o <= stride - size;
            if (stride > 0 && Fits(_oOrigin, 4) && Fits(_oAdvance, 4) && Fits(_oLine, 4) && Fits(_oIndex, 4) && Fits(_oChar, 2))
                _ciStride = stride;
        }
        catch (Exception e)
        {
            Core.Log.Warning($"[Notepad] fast text layout unavailable: {e.Message}");
        }
        return _ciStride > 0;
    }

    private static void Grow<T>(ref T[] a, int n)
    {
        if (a.Length < n) a = new T[Math.Max(n, a.Length * 2)];
    }

    private int KAt(int i)
    {
        for (; i < _laidText.Length; i++)
            if (_kAt[i] >= 0) return _kAt[i];
        return -1;
    }

    /// <summary>Caret x and line number before string index i.</summary>
    private (float x, int line) Pos(int i)
    {
        if (_n == 0) return (_left, 0);
        int k = i < _laidText.Length ? KAt(i) : -1;
        if (k >= 0) return (_origin[k], _lineOf[k]);
        int last = _n - 1;
        if (_char[last] == '\n') return (_left, _lineOf[last] + 1);  // the empty line after a final newline
        return (_advance[last], _lineOf[last]);
    }

    private (float asc, float desc) LineY(int line)
    {
        if (_lines == 0) return (_asc0, _desc0);
        if (line < _lines) return (_lAsc[line], _lDesc[line]);
        float down = (line - _lines + 1) * _lineH;
        return (_lAsc[_lines - 1] - down, _lDesc[_lines - 1] - down);
    }

    private int LineCount => _n > 0 && _char[_n - 1] == '\n' ? _lines + 1 : Math.Max(1, _lines);

    private float ContentHeight() => -LineY(LineCount - 1).desc + _lineH * 0.25f;

    private void DrawSelection(int start, int end)
    {
        int used = 0;
        if (end > start && _n > 0)
        {
            var (sx, l0) = Pos(start);
            var (ex, l1) = Pos(end);
            for (int l = l0; l <= l1 && l < _lines; l++)
            {
                float x0 = l == l0 ? sx : _origin[_lFirst[l]];
                float x1 = l == l1 ? ex : _advance[_lLast[l]] + _size * 0.3f;
                if (x1 <= x0) continue;
                var (asc, desc) = LineY(l);
                var img = SelectionImage(used++);
                img.rectTransform.anchoredPosition = new Vector2(x0, desc);
                img.rectTransform.sizeDelta = new Vector2(x1 - x0, asc - desc);
            }
        }
        for (int i = 0; i < _selection.Count; i++)
            if (_selection[i].enabled != (i < used)) _selection[i].enabled = i < used;
    }

    private Image SelectionImage(int i)
    {
        while (_selection.Count <= i)
        {
            var go = new GameObject("Selection");
            go.transform.SetParent(_body.transform, false);
            go.transform.SetAsFirstSibling();
            var img = go.AddComponent<Image>();
            img.color = SelectionColor;
            img.raycastTarget = false;
            Anchor(img.rectTransform);
            _selection.Add(img);
        }
        return _selection[i];
    }

    /// <summary>Anchored at the body's pivot (top centre), so anchoredPosition is in the text's local space.</summary>
    private static void Anchor(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = Vector2.zero;
    }

    // ------------------------------------------------------------------ editor navigation (from the layout)

    /// <summary>One visual line up (dir -1) or down (+1), keeping the column in goalX (NaN = take the caret's).</summary>
    public int Vertical(int caret, int dir, ref float goalX)
    {
        if (_n == 0) return caret;
        var (x, line) = Pos(caret);
        if (float.IsNaN(goalX)) goalX = x;
        int target = line + dir;
        if (target < 0) return 0;
        if (target >= LineCount) return _laidText.Length;
        return AtX(target, goalX);
    }

    /// <summary>Moves by a page of lines.</summary>
    public int Page(int caret, int dir, ref float goalX)
    {
        int lines = Mathf.Max(1, Mathf.FloorToInt(_viewport.rect.height / Mathf.Max(1f, _lineH)) - 1);
        for (int i = 0; i < lines; i++)
        {
            int next = Vertical(caret, dir, ref goalX);
            if (next == caret) break;
            caret = next;
        }
        return caret;
    }

    /// <summary>Start of the caret's visual line.</summary>
    public int Home(int caret)
    {
        var (_, line) = Pos(caret);
        if (_n == 0 || line >= _lines) return caret;
        return _index[_lFirst[line]];
    }

    /// <summary>End of the caret's visual line (before a wrapped line's trailing space).</summary>
    public int End(int caret)
    {
        var (_, line) = Pos(caret);
        if (_n == 0 || line >= _lines) return caret;
        // A line broken without a space (hyphen, long word): its end index is drawn at the next line's start,
        // so stop before its last character instead.
        int e = LineEndIndex(line);
        return Pos(e).line == line ? e : _index[_lLast[line]];
    }

    private int LineEndIndex(int line)
    {
        int last = _lLast[line];
        char c = _char[last];
        if (c == '\n') return _index[last];
        if (line == _lines - 1) return _laidText.Length;
        return char.IsWhiteSpace(c) ? _index[last] : _index[last] + 1;
    }

    private int AtX(int line, float x)
    {
        if (line >= _lines) return _laidText.Length;
        int first = _lFirst[line], last = _lLast[line];
        int best = _index[first];
        float bestD = Mathf.Abs(_origin[first] - x);
        for (int k = first + 1; k <= last; k++)
        {
            if (_char[k - 1] == '\n') break;
            float d = Mathf.Abs(_origin[k] - x);
            if (d < bestD) { bestD = d; best = _index[k]; }
        }
        // The end of the line is a candidate too.
        int end = LineEndIndex(line);
        var (ex, el) = Pos(end);
        if (el == line && Mathf.Abs(ex - x) < bestD) best = end;
        return best;
    }

    // ------------------------------------------------------------------ building

    private readonly Dictionary<IntPtr, string> _shown = new();

    /// <summary>Sets text only when it changed (a TMP text change rebuilds its mesh).</summary>
    private void Set(TextMeshProUGUI t, string s)
    {
        s ??= "";
        var key = t.Pointer;
        if (_shown.TryGetValue(key, out var old) && old == s) return;
        _shown[key] = s;
        t.text = s;
    }

    private bool TryCreate(string fontName)
    {
        var font = FindFont(fontName, out var fallback);
        if (font == null)
        {
            Core.Log.Warning("[Notepad] no usable font found");
            return false;
        }
        if (fallback) Core.Log.Warning($"[Notepad] font '{fontName}' not found, using '{font.name}'");

        _root = new GameObject(RootName);
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 600;

        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(_root.transform, false);
        _panel = panelGo.AddComponent<RectTransform>();
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0.5f);
        _panel.anchoredPosition = Vector2.zero;
        var bg = panelGo.AddComponent<Image>();
        bg.color = new Color(0.02f, 0.03f, 0.06f, 0.9f);
        bg.raycastTarget = false;

        _header = MakeText(panelGo, "Header", font, Ink, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
        _hint = MakeText(panelGo, "Hint", font, Dim, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);

        var contentGo = new GameObject("Content");
        contentGo.transform.SetParent(panelGo.transform, false);
        _content = contentGo.AddComponent<RectTransform>();

        // List: search line, rows, "no notes" message.
        var listGo = new GameObject("List");
        listGo.transform.SetParent(contentGo.transform, false);
        _listArea = listGo.AddComponent<RectTransform>();
        Stretch(_listArea);
        _search = MakeText(listGo, "Search", font, Ink, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
        _empty = MakeText(listGo, "Empty", font, Dim, TextWrappingModes.Normal, TextOverflowModes.Overflow);
        _empty.alignment = TextAlignmentOptions.Center;
        Stretch(_empty.rectTransform);
        for (int i = 0; i < MaxRows; i++)
        {
            var rowGo = new GameObject("Row");
            rowGo.transform.SetParent(listGo.transform, false);
            var row = new Row { Rect = rowGo.AddComponent<RectTransform>() };
            row.Mark = rowGo.AddComponent<Image>();
            row.Mark.color = RowMark;
            row.Mark.raycastTarget = false;
            row.Title = MakeText(rowGo, "Title", font, Ink, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
            row.Meta = MakeText(rowGo, "Meta", font, Dim, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
            rowGo.SetActive(false);
            _rows.Add(row);
        }

        // Editor: the text overflows downwards and scrolls inside a clipped viewport.
        var viewGo = new GameObject("Viewport");
        viewGo.transform.SetParent(contentGo.transform, false);
        _viewport = viewGo.AddComponent<RectTransform>();
        Stretch(_viewport);
        viewGo.AddComponent<RectMask2D>();
        _body = MakeText(viewGo, "Body", font, Ink, TextWrappingModes.Normal, TextOverflowModes.Overflow);
        var caretGo = new GameObject("Caret");
        caretGo.transform.SetParent(_body.transform, false);
        _caret = caretGo.AddComponent<Image>();
        _caret.color = Ink;
        _caret.raycastTarget = false;
        Anchor(_caret.rectTransform);
        viewGo.SetActive(false);

        _builtFor = fontName;
        _size = _panelW = 0f;
        Core.Log.Msg($"[Notepad] using game font '{font.name}'");
        return true;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    private static TextMeshProUGUI MakeText(GameObject parent, string name, TMP_FontAsset font, Color color,
        TextWrappingModes wrap, TextOverflowModes overflow)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.color = color;
        t.richText = false;           // text is shown as typed, '<' included
        t.parseCtrlCharacters = false;
        t.textWrappingMode = wrap;
        t.overflowMode = overflow;
        t.alignment = TextAlignmentOptions.TopLeft;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>The wanted font, else TypeNeat SDF, else TMP's default (LiberationSans, always loaded).</summary>
    private static TMP_FontAsset FindFont(string want, out bool fallback)
    {
        TMP_FontAsset found = null, neat = null;
        foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (f == null) continue;
            if (found == null && f.name.Equals(want, StringComparison.OrdinalIgnoreCase)) found = f;
            if (neat == null && f.name == FallbackFont) neat = f;
        }
        fallback = found == null;
        if (found != null) return found;
        if (neat != null) return neat;
        try { return TMP_Settings.defaultFontAsset; }
        catch { return null; }
    }
}
