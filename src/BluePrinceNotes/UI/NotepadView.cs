using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    public readonly bool Deleting;   // first Delete press: struck through, waiting for the second
    public ListRow(string title, string meta, bool deleting = false) { Title = title; Meta = meta; Deleting = deleting; }
}

/// <summary>
/// The notepad panel, drawn as a ruled notebook page over the dimmed game: its own ScreenSpaceOverlay canvas
/// (sortingOrder 600, above Blue Prince QoL's overlays at 500-502, below the letterbox camera at 9999 and
/// UnityExplorer at 30000). A red header on a red double rule, the list of notes or the editor on the blue rules, and
/// a pencil hint line. Built on first show and destroyed on every scene load (the game fonts are scene assets); the
/// paper texture is kept.
///
/// Everything sits on one grid: line k's baseline is T + A + k*L below the page top, where A is the font's ascent and
/// L its line advance at the notepad size. The rules are baked into the paper texture at those baselines, the editor
/// shows exactly n lines and scrolls by whole lines, and each list row takes two lines (title, then its context).
/// The editor draws its own caret and selection from TMP's layout (textInfo), cached once per text change.
/// </summary>
internal sealed class NotepadView
{
    private const string RootName = "BluePrinceNotes Notepad";
    private const string FallbackFont = "TypeNeat SDF";
    private const int MaxRows = 20;
    private const int PaperSeed = 1912;

    private static readonly Color Ink = new(0.075f, 0.065f, 0.070f, 1f);
    private static readonly Color Red = new(0.396f, 0f, 0.060f, 1f);
    private static readonly Color CaretColor = new(0.700f, 0.080f, 0.120f, 1f);  // brighter than Red: a thin bar of Red reads as an "l"
    private static readonly Color Pencil = new(0.142f, 0.119f, 0.119f, 0.78f);
    private static readonly Color PaperFlat = new(0.957f, 0.933f, 0.859f, 1f);   // until the texture is ready
    private static readonly Color DimColor = new(0f, 0f, 0f, 0.5f);

    private GameObject _root;
    private RectTransform _panel, _listArea, _viewport, _selLayer;
    private RawImage _paper;
    private TextMeshProUGUI _header, _hint, _search, _empty, _body, _marker, _probe;
    private Image _caret, _rowMark, _strike;
    private readonly List<Image> _selection = new();
    private readonly List<Row> _rows = new();
    private string _builtFor;
    private float _nextTry;

    private sealed class Row
    {
        public TextMeshProUGUI Title, Meta;
    }

    public bool Visible => _root != null && _root.activeSelf;

    // ------------------------------------------------------------------ geometry

    // Page grid, in px, y down from the page top (see the class comment).
    private float _s, _A, _L, _em;
    private int _W, _H, _B, _n;
    private float _T, _M, _X0, _X1;
    private string _geomKey;

    private float Baseline(int line) => _T + _A + line * _L;

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
        UpdatePaper();
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
        _panel = _listArea = _viewport = _selLayer = null;
        _paper = null;
        _header = _hint = _search = _empty = _body = _marker = _probe = null;
        _caret = _rowMark = _strike = null;
        _selection.Clear();
        _rows.Clear();
        _builtFor = null;
        _geomKey = null;
        _layoutKey = null;
        _shown.Clear();
    }

    private static int Even(float v) => Mathf.RoundToInt(v / 2f) * 2;

    private void Layout(float fontScale)
    {
        int sw = Screen.width, sh = Screen.height;
        var key = $"{sw}x{sh}:{fontScale}:{_body.font.name}";
        if (key == _geomKey) return;
        _geomKey = key;

        float s = Mathf.Max(12f, sh * Mathf.Clamp(fontScale, 0.01f, 0.1f));
        var face = _body.font.faceInfo;
        float pt = Mathf.Max(1f, face.pointSize);
        _s = s;
        _A = face.ascentLine / pt * s;
        _L = face.lineHeight / pt * s;
        _em = Mathf.Max(1.2f, (face.ascentLine - face.descentLine) / pt) + 0.05f;
        MeasureLines();

        _W = Even(Mathf.Min(sw - 40f, Mathf.Max(0.55f * sw, 24f * s)));
        _T = 2.30f * s;
        _n = Mathf.Max(4, Mathf.FloorToInt((0.72f * sh - _T - 1.50f * s) / _L + 0.001f));
        _H = Even(_T + _n * _L + 1.50f * s);
        _B = Mathf.RoundToInt(0.75f * s);
        _M = 2.4f * s;
        _X0 = _M + 0.5f * s;
        _X1 = _W - 0.9f * s;

        // Centred on whole pixels, so the paper's 1-2 px rules stay sharp.
        _panel.sizeDelta = new Vector2(_W, _H);
        _panel.anchoredPosition = new Vector2((sw - _W) % 2 != 0 ? 0.5f : 0f, (sh - _H) % 2 != 0 ? 0.5f : 0f);
        var pr = _paper.rectTransform;
        pr.anchorMin = Vector2.zero;
        pr.anchorMax = Vector2.one;  // its border (shadow) is set in UpdatePaper

        // Header on the red double rule (a touch above it, so descenders don't sit in the rule); hint at the bottom.
        _header.fontSize = 0.95f * s;
        PlaceText(_header, _X0, _X1, Baseline(-1) - 0.08f * s, 0.95f);
        _hint.fontSize = 0.6f * s;
        PlaceText(_hint, _X0, _X1, _H - 0.55f * s, 0.6f);

        // List: X0..X1 over the n lines. Editor viewport: 0.3 s wider on each side, so the highlighter ends and a caret
        // at column 0 aren't clipped; the body is inset by the same amount.
        PlaceTL(_listArea, _X0, _T, _X1 - _X0, _n * _L);
        PlaceTL(_viewport, _X0 - 0.3f * s, _T, _X1 - _X0 + 0.6f * s, _n * _L);
        var br = _body.rectTransform;
        br.anchorMin = br.anchorMax = new Vector2(0f, 1f);
        br.pivot = new Vector2(0.5f, 1f);
        br.sizeDelta = new Vector2(_X1 - _X0, _n * _L);
        _bodyX = 0.3f * s + (_X1 - _X0) / 2f;
        br.anchoredPosition = new Vector2(_bodyX, _scroll);
        _selLayer.anchorMin = _selLayer.anchorMax = br.anchorMin;
        _selLayer.pivot = br.pivot;
        _selLayer.sizeDelta = br.sizeDelta;
        _selLayer.anchoredPosition = br.anchoredPosition;
        _body.fontSize = s;

        _search.fontSize = s;
        _empty.fontSize = 0.8f * s;
        _marker.fontSize = s;
        foreach (var r in _rows)
        {
            r.Title.fontSize = s;
            r.Meta.fontSize = 0.6f * s;
        }
        _caret.rectTransform.sizeDelta = new Vector2(Mathf.Max(2f, 0.08f * s), 1.14f * s);
        _strike.rectTransform.sizeDelta = new Vector2(0f, Mathf.Max(2f, 0.07f * s));
        _rowMark.pixelsPerUnitMultiplier = 64f / (1.22f * s);
        foreach (var img in _selection) img.pixelsPerUnitMultiplier = 64f / (1.22f * s);
        _layoutKey = null;
        _listMarkKey = null;
    }

    /// <summary>A and L as TMP lays them out (a two-line sample), in case they differ from the font's numbers.</summary>
    private void MeasureLines()
    {
        try
        {
            _probe.fontSize = _s;
            _probe.rectTransform.sizeDelta = new Vector2(10f * _s, 4f * _L);
            _probe.text = "Ag\nAg";
            _probe.ForceMeshUpdate();
            var info = _probe.textInfo;
            if (info.lineCount < 2) return;
            var lines = info.lineInfo;
            float a = -lines[0].baseline, l = lines[0].baseline - lines[1].baseline;
            if (a > 0f && Mathf.Abs(a - _A) > 0.5f) _A = a;
            if (l > 0f && Mathf.Abs(l - _L) > 0.5f) _L = l;
        }
        catch (Exception e)
        {
            Core.Log.Warning($"[Notepad] measuring the font: {e.Message}");
        }
    }

    /// <summary>Top-left anchored rect in page px (x right, y down from the page top).</summary>
    private static void PlaceTL(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>A one-line TopLeft text box from x0 to x1 whose baseline lands on <paramref name="baseline"/>.</summary>
    private void PlaceText(TextMeshProUGUI t, float x0, float x1, float baseline, float size)
    {
        float top = baseline - size * _A;
        PlaceTL(t.rectTransform, x0, top, x1 - x0, size * _em * _s);
    }

    // ------------------------------------------------------------------ paper texture

    private static Texture2D _paperTex, _markTex;
    private static Sprite _markSprite;
    private static string _paperKey;
    private static Task<(byte[] px, int w, int h, string key)> _paperTask;
    private static string _paperTaskKey;
    private static bool _paperFailed;

    /// <summary>Builds the paper for the current geometry on a worker thread, and uploads it when it's ready.</summary>
    private void UpdatePaper()
    {
        var g = new NotebookPaper.Geometry
        {
            PageW = _W, PageH = _H, Border = _B, S = _s, MarginX = _M,
            HeaderRuleY = Baseline(-1) + 0.035f * _s, FirstRuleY = Baseline(0) + 0.035f * _s,
            RuleStep = _L, RuleCount = _n, Seed = PaperSeed,
        };
        var key = $"{g.PageW}x{g.PageH}:{g.Border}:{g.S}:{g.HeaderRuleY}:{g.FirstRuleY}:{g.RuleStep}:{g.RuleCount}";

        if (_paperTask != null && _paperTask.IsCompleted)
        {
            var task = _paperTask;
            _paperTask = null;
            if (task.IsFaulted)
            {
                if (!_paperFailed) Core.Log.Warning($"[Notepad] drawing the paper failed: {task.Exception?.GetBaseException().Message}");
                _paperFailed = true;
            }
            else if (task.Result.key == key) Upload(task.Result.px, task.Result.w, task.Result.h, key);
        }
        if (_paperKey != key && _paperTask == null && !_paperFailed)
        {
            _paperTaskKey = key;
            _paperTask = Task.Run(() =>
            {
                var px = NotebookPaper.Build(g, out int w, out int h);
                return (px, w, h, key);
            });
        }

        bool ready = _paperKey == key && _paperTex != null;
        if (ready && _paper.texture == null) _paper.texture = _paperTex;
        if (!ready && _paper.texture != null) _paper.texture = null;
        _paper.color = ready ? Color.white : PaperFlat;
        // The texture has a shadow border around the page; the flat colour shown until it's ready covers the page only.
        float b = ready ? _B : 0f;
        var pr = _paper.rectTransform;
        if (pr.offsetMax.x != b || pr.offsetMax.y != b)
        {
            pr.offsetMin = new Vector2(-b, -b);
            pr.offsetMax = new Vector2(b, b);
        }
    }

    private static void Upload(byte[] px, int w, int h, string key)
    {
        try
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.LoadRawTextureData(px);
            tex.Apply(false, true);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;  // the game unloads unused assets; keep ours
            if (_paperTex != null) UnityEngine.Object.Destroy(_paperTex);
            _paperTex = tex;
            _paperKey = key;
        }
        catch (Exception e)
        {
            _paperFailed = true;
            Core.Log.Warning($"[Notepad] uploading the paper failed: {e.Message}");
        }
    }

    /// <summary>The highlighter swipe (selection, selected row): a sliced sprite whose chisel ends keep their shape.</summary>
    private static Sprite Highlighter()
    {
        if (_markSprite != null) return _markSprite;
        const int w = 256, h = 64;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.LoadRawTextureData(NotebookPaper.BuildHighlighter(w, h, PaperSeed));
        tex.Apply(false, true);
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.hideFlags = HideFlags.HideAndDontSave;
        _markTex = tex;
        _markSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
            new Vector4(32, 0, 32, 0));
        _markSprite.hideFlags = HideFlags.HideAndDontSave;
        return _markSprite;
    }

    private Image MakeMark(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var img = go.AddComponent<Image>();
        img.sprite = Highlighter();
        img.type = Image.Type.Sliced;
        img.color = Color.white;
        img.raycastTarget = false;
        if (_s > 0f) img.pixelsPerUnitMultiplier = 64f / (1.22f * _s);
        return img;
    }

    // ------------------------------------------------------------------ list

    private int _listTop;
    private string _listMarkKey;

    public void ShowList(string header, string hint, string search, IReadOnlyList<ListRow> rows, int selected, string empty)
    {
        _viewport.gameObject.SetActive(false);
        _listArea.gameObject.SetActive(true);
        Set(_header, header);
        Set(_hint, hint);

        // Each row takes two lines of the grid: the title on a rule, its context just under it, then a blank rule.
        // While searching, the search text takes line 0.
        bool searching = !string.IsNullOrEmpty(search);
        if (_search.gameObject.activeSelf != searching) _search.gameObject.SetActive(searching);
        int first = searching ? 1 : 0;
        if (searching)
        {
            Set(_search, search);
            PlaceText(_search, 0f, _X1 - _X0, _A, 1f);
        }
        int fit = Mathf.Clamp((_n - first) / 2, 1, MaxRows);
        int count = rows?.Count ?? 0;
        if (selected >= 0 && selected < _listTop) _listTop = selected;
        if (selected >= _listTop + fit) _listTop = selected - fit + 1;
        _listTop = Mathf.Clamp(_listTop, 0, Mathf.Max(0, count - fit));

        bool markShown = false, strikeShown = false;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            int n = _listTop + i;
            bool used = i < fit && n < count;
            if (row.Title.gameObject.activeSelf != used)
            {
                row.Title.gameObject.SetActive(used);
                row.Meta.gameObject.SetActive(used);
            }
            if (!used) continue;
            int line = first + 2 * i;
            float baseline = _A + line * _L;  // list-local: the list's top is the page's T
            PlaceText(row.Title, 0f, _X1 - _X0, baseline, 1f);
            PlaceText(row.Meta, 0f, _X1 - _X0, baseline + 0.98f * _s, 0.6f);
            Set(row.Title, rows[n].Title);
            Set(row.Meta, rows[n].Meta);
            if (n != selected) continue;

            // The selected row: a highlighter swipe behind its title, and a red » in the margin.
            float width = TitleWidth(row.Title, rows[n].Title, n, selected);
            var mr = _rowMark.rectTransform;
            PlaceTL(mr, -0.22f * _s, baseline - 0.92f * _s, width + 0.50f * _s, 1.22f * _s);
            markShown = true;
            PlaceTextRight(_marker, _M - 0.28f * _s, _T + baseline);
            if (rows[n].Deleting)
            {
                PlaceTL(_strike.rectTransform, 0f, baseline - 0.30f * _s, width, _strike.rectTransform.sizeDelta.y);
                strikeShown = true;
            }
        }
        if (_rowMark.gameObject.activeSelf != markShown) _rowMark.gameObject.SetActive(markShown);
        if (_marker.gameObject.activeSelf != markShown) _marker.gameObject.SetActive(markShown);
        if (_strike.gameObject.activeSelf != strikeShown) _strike.gameObject.SetActive(strikeShown);

        if (_empty.gameObject.activeSelf != (count == 0)) _empty.gameObject.SetActive(count == 0);
        if (count == 0)
        {
            Set(_empty, empty);
            float top = _A + (_n / 2) * _L - 0.8f * _A;
            PlaceTL(_empty.rectTransform, 0f, top, _X1 - _X0, 2f * _L);
        }
    }

    private float _titleWidth;

    /// <summary>The selected title's width (measured when the selection or its text changes).</summary>
    private float TitleWidth(TextMeshProUGUI t, string text, int index, int selected)
    {
        var key = $"{selected}:{text}:{_s}";
        if (key != _listMarkKey)
        {
            _listMarkKey = key;
            _titleWidth = Mathf.Min(t.GetPreferredValues(text ?? "").x, _X1 - _X0);
        }
        return _titleWidth;
    }

    /// <summary>The » marker: right-aligned, ending at x (page px), baseline at y (page px).</summary>
    private void PlaceTextRight(TextMeshProUGUI t, float x, float baseline)
    {
        float top = baseline - _A;
        PlaceTL(t.rectTransform, x - 2f * _s, top, 2f * _s, _em * _s);
    }

    // ------------------------------------------------------------------ editor

    // Layout cache, rebuilt when the shown text, the width or the size changes.
    private string _layoutKey;
    private string _laidText = "";
    private int _n2, _lines;
    private float[] _origin = new float[256], _advance = new float[256];
    private int[] _lineOf = new int[256], _index = new int[256];
    private char[] _char = new char[256];
    private int[] _kAt = new int[257];
    private int[] _lFirst = new int[32], _lLast = new int[32];
    private float[] _lAsc = new float[32], _lDesc = new float[32];
    private float _left, _right, _lineH, _asc0, _desc0;
    private float _scroll, _bodyX;

    /// <summary>
    /// Shows text with a caret and selection. caret/anchor index into <paramref name="text"/>; the input method's
    /// pending text (if any) is drawn at the caret.
    /// </summary>
    public void ShowEditor(string header, string hint, string text, int caret, int anchor, bool caretOn, string composing)
    {
        _listArea.gameObject.SetActive(false);
        _viewport.gameObject.SetActive(true);
        if (_marker.gameObject.activeSelf) _marker.gameObject.SetActive(false);
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

        // Caret and selection, in the body's local space (top-anchored, so y goes down from 0). They span the glyphs
        // around the baseline (0.92 s above, 0.22 s below), not the whole line, so they never cross a rule.
        var (cx, cl) = Pos(caret);
        cx = Mathf.Min(cx, _right - _caret.rectTransform.sizeDelta.x);  // trailing spaces run past the edge
        float baseY = LineBase(cl);
        _caret.enabled = caretOn;
        _caret.rectTransform.anchoredPosition = new Vector2(cx - _caret.rectTransform.sizeDelta.x / 2f, baseY - 0.22f * _s);
        DrawSelection(Math.Min(caret, anchor), Math.Max(caret, anchor));

        // Keep the caret line in view.
        if (_followCaret)
        {
            var (asc, desc) = LineY(cl);
            float viewH = _n * _L;
            if (asc > -_scroll) _scroll = -asc;
            if (desc < -_scroll - viewH) _scroll = -desc - viewH;
        }
        ClampScroll();
    }

    private bool _followCaret = true;

    /// <summary>Caret moved or text changed: bring the caret back into view.</summary>
    public void FollowCaret()
    {
        _followCaret = true;
        _wheelRest = 0f;
    }

    /// <summary>Scrolls the editor by a number of lines (mouse wheel); the caret may leave the view.</summary>
    public void ScrollLines(float lines)
    {
        if (_body == null || _lineH <= 0f) return;
        // Whole lines only (the text stays on the rules); fractions from touchpads and fine wheels add up.
        _wheelRest += lines;
        float whole = (float)Math.Truncate(_wheelRest);
        _wheelRest -= whole;
        _followCaret = false;
        if (whole == 0f) return;
        _scroll += whole * _lineH;
        ClampScroll();
    }

    private float _wheelRest;

    public void ResetScroll()
    {
        _scroll = 0f;
        _wheelRest = 0f;
        _followCaret = true;
    }

    /// <summary>Scrolls by whole lines only, so the text stays on the paper's rules.</summary>
    private void ClampScroll()
    {
        float viewH = _n * _L;
        _scroll = Mathf.Clamp(_scroll, 0f, Mathf.Max(0f, ContentHeight() - viewH));
        if (_lineH > 0f) _scroll = Mathf.Round(_scroll / _lineH) * _lineH;
        _body.rectTransform.anchoredPosition = new Vector2(_bodyX, _scroll);
        _selLayer.anchoredPosition = new Vector2(_bodyX, _scroll);
    }

    private void Relayout(string text)
    {
        var key = $"{text.Length}:{text.GetHashCode()}:{_geomKey}";
        if (key == _layoutKey) return;
        _layoutKey = key;
        _laidText = text;

        var br = _body.rectTransform;
        _body.text = text;
        _body.ForceMeshUpdate();

        var info = _body.textInfo;
        _n2 = Math.Max(0, info.characterCount);
        _lines = Math.Max(0, info.lineCount);
        Grow(ref _origin, _n2); Grow(ref _advance, _n2); Grow(ref _lineOf, _n2); Grow(ref _index, _n2); Grow(ref _char, _n2);
        Grow(ref _kAt, text.Length + 1);
        Grow(ref _lFirst, _lines); Grow(ref _lLast, _lines); Grow(ref _lAsc, _lines); Grow(ref _lDesc, _lines);

        var chars = info.characterInfo;
        if (chars == null) _n2 = 0;
        else if (_n2 > chars.Length) _n2 = chars.Length;
        if (!ReadDirect(chars))
        {
            for (int k = 0; k < _n2; k++)
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
        for (int k = 0; k < _n2; k++)
            if (_index[k] >= 0 && _index[k] < text.Length) _kAt[_index[k]] = k;

        _left = br.rect.xMin;
        _right = br.rect.xMax;
        // An empty text has no lines: use the grid (first line's top at 0, baseline at -A).
        _asc0 = 0f;
        _desc0 = -(_em - 0.05f) * _s;
        _lineH = _L;
    }

    // TMP_CharacterInfo is a big struct; reading it through the interop boxes every element (a native object, a GC
    // handle and several native calls each), which costs tens of ms per keystroke in a long note. The array holds
    // the structs inline, so the five fields are read straight from memory instead. The offsets come from IL2CPP's
    // own class data, and the first read is checked against the boxed values; any mismatch falls back for good.
    private static int _ciStride = -1, _oOrigin, _oAdvance, _oLine, _oIndex, _oChar;
    private static bool _ciChecked;

    private bool ReadDirect(Il2CppReferenceArray<TMP_CharacterInfo> chars)
    {
        if (_n2 == 0 || chars == null || !DirectLayout()) return false;
        IntPtr data = chars.Pointer + 4 * IntPtr.Size;  // after the array header, as Il2CppArrayBase does
        for (int k = 0; k < _n2; k++)
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
        if (Same(chars[0], 0) && Same(chars[_n2 - 1], _n2 - 1)) return true;
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
        if (_n2 == 0) return (_left, 0);
        int k = i < _laidText.Length ? KAt(i) : -1;
        if (k >= 0) return (_origin[k], _lineOf[k]);
        int last = _n2 - 1;
        if (_char[last] == '\n') return (_left, _lineOf[last] + 1);  // the empty line after a final newline
        return (_advance[last], _lineOf[last]);
    }

    /// <summary>Top and bottom of a line, in the body's local space.</summary>
    private (float asc, float desc) LineY(int line)
    {
        if (_lines == 0) return (_asc0 - line * _lineH, _desc0 - line * _lineH);
        if (line < _lines) return (_lAsc[line], _lDesc[line]);
        float down = (line - _lines + 1) * _lineH;
        return (_lAsc[_lines - 1] - down, _lDesc[_lines - 1] - down);
    }

    /// <summary>The baseline of a line, in the body's local space (on the grid: -(A + line L)).</summary>
    private float LineBase(int line) => -(_A + line * _L);

    private int LineCount => _n2 > 0 && _char[_n2 - 1] == '\n' ? _lines + 1 : Math.Max(1, _lines);

    private float ContentHeight() => LineCount * _lineH;

    private void DrawSelection(int start, int end)
    {
        int used = 0;
        if (end > start && _n2 > 0)
        {
            var (sx, l0) = Pos(start);
            var (ex, l1) = Pos(end);
            for (int l = l0; l <= l1 && l < _lines; l++)
            {
                float x0 = l == l0 ? sx : _origin[_lFirst[l]];
                float x1 = l == l1 ? ex : _advance[_lLast[l]] + _s * 0.3f;
                x1 = Mathf.Min(x1, _right + 0.25f * _s);
                if (x1 <= x0) continue;
                // One highlighter swipe per line, under the glyphs.
                var img = SelectionImage(used++);
                img.rectTransform.anchoredPosition = new Vector2(x0 - 0.08f * _s, LineBase(l) - 0.30f * _s);
                img.rectTransform.sizeDelta = new Vector2(x1 - x0 + 0.16f * _s, 1.22f * _s);
            }
        }
        for (int i = 0; i < _selection.Count; i++)
            if (_selection[i].enabled != (i < used)) _selection[i].enabled = i < used;
    }

    private Image SelectionImage(int i)
    {
        while (_selection.Count <= i)
        {
            var img = MakeMark(_selLayer.gameObject, "Selection");
            Anchor(img.rectTransform);
            _selection.Add(img);
        }
        return _selection[i];
    }

    /// <summary>
    /// Anchored at the pivot (top centre) of the body or of the selection layer, which has the same rect, so
    /// anchoredPosition is in the text's local space.
    /// </summary>
    private static void Anchor(RectTransform rt)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = Vector2.zero;
    }

    // ------------------------------------------------------------------ editor navigation (from the layout)

    /// <summary>One visual line up (dir -1) or down (+1), keeping the column in goalX (NaN = take the caret's).</summary>
    public int Vertical(int caret, int dir, ref float goalX)
    {
        if (_n2 == 0) return caret;
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
        int lines = Mathf.Max(1, _n - 1);
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
        if (_n2 == 0 || line >= _lines) return caret;
        return _index[_lFirst[line]];
    }

    /// <summary>End of the caret's visual line (before a wrapped line's trailing space).</summary>
    public int End(int caret)
    {
        var (_, line) = Pos(caret);
        if (_n2 == 0 || line >= _lines) return caret;
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

        // The game, dimmed: the notepad reads like a pause.
        var dimGo = new GameObject("Dim");
        dimGo.transform.SetParent(_root.transform, false);
        var dim = dimGo.AddComponent<Image>();
        dim.color = DimColor;
        dim.raycastTarget = false;
        var dr = dim.rectTransform;
        dr.anchorMin = Vector2.zero;
        dr.anchorMax = Vector2.one;
        dr.offsetMin = dr.offsetMax = Vector2.zero;

        var panelGo = new GameObject("Panel");
        panelGo.transform.SetParent(_root.transform, false);
        _panel = panelGo.AddComponent<RectTransform>();
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(0.5f, 0.5f);

        var paperGo = new GameObject("Paper");
        paperGo.transform.SetParent(panelGo.transform, false);
        _paper = paperGo.AddComponent<RawImage>();
        _paper.color = PaperFlat;
        _paper.raycastTarget = false;

        _probe = MakeText(panelGo, "Probe", font, Color.clear, TextWrappingModes.NoWrap, TextOverflowModes.Overflow);
        _probe.rectTransform.anchorMin = _probe.rectTransform.anchorMax = new Vector2(0f, 1f);
        _probe.rectTransform.pivot = new Vector2(0f, 1f);
        _header = MakeText(panelGo, "Header", font, Red, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
        _hint = MakeText(panelGo, "Hint", font, Pencil, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
        _marker = MakeText(panelGo, "Marker", font, Red, TextWrappingModes.NoWrap, TextOverflowModes.Overflow);
        _marker.alignment = TextAlignmentOptions.TopRight;
        _marker.text = font.HasCharacter('»') ? "»" : ">";
        _marker.gameObject.SetActive(false);

        // List: the selected row's highlighter and strike sit under the row texts.
        var listGo = new GameObject("List");
        listGo.transform.SetParent(panelGo.transform, false);
        _listArea = listGo.AddComponent<RectTransform>();
        _rowMark = MakeMark(listGo, "Selected");
        _rowMark.gameObject.SetActive(false);
        _search = MakeText(listGo, "Search", font, Ink, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis);
        _empty = MakeText(listGo, "Empty", font, Pencil, TextWrappingModes.Normal, TextOverflowModes.Overflow);
        _empty.alignment = TextAlignmentOptions.Top;
        for (int i = 0; i < MaxRows; i++)
        {
            var row = new Row
            {
                Title = MakeText(listGo, "Title", font, Ink, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis),
                Meta = MakeText(listGo, "Meta", font, Pencil, TextWrappingModes.NoWrap, TextOverflowModes.Ellipsis),
            };
            row.Title.gameObject.SetActive(false);
            row.Meta.gameObject.SetActive(false);
            _rows.Add(row);
        }
        var strikeGo = new GameObject("Strike");
        strikeGo.transform.SetParent(listGo.transform, false);
        _strike = strikeGo.AddComponent<Image>();
        _strike.color = Red;
        _strike.raycastTarget = false;
        strikeGo.SetActive(false);

        // Editor: exactly n lines, clipped; the text scrolls by whole lines inside.
        var viewGo = new GameObject("Viewport");
        viewGo.transform.SetParent(panelGo.transform, false);
        _viewport = viewGo.AddComponent<RectTransform>();
        viewGo.AddComponent<RectMask2D>();
        // The selection swipes go on a layer drawn before the body (uGUI draws a parent's own mesh before its
        // children, so as children of the body they would cover the ink). It moves with the body.
        var selGo = new GameObject("SelectionLayer");
        selGo.transform.SetParent(viewGo.transform, false);
        _selLayer = selGo.AddComponent<RectTransform>();
        _body = MakeText(viewGo, "Body", font, Ink, TextWrappingModes.Normal, TextOverflowModes.Overflow);
        _body.lineSpacing = 0f;
        _body.paragraphSpacing = 0f;
        var caretGo = new GameObject("Caret");
        caretGo.transform.SetParent(_body.transform, false);
        _caret = caretGo.AddComponent<Image>();
        _caret.color = CaretColor;
        _caret.raycastTarget = false;
        Anchor(_caret.rectTransform);
        viewGo.SetActive(false);

        _builtFor = fontName;
        _geomKey = null;
        Core.Log.Msg($"[Notepad] using game font '{font.name}'");
        return true;
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
