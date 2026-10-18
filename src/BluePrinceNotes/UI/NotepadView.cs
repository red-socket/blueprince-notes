using System;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BluePrinceNotes.UI;

/// <summary>
/// The notepad panel: its own ScreenSpaceOverlay canvas (sortingOrder 600, above Blue Prince QoL's overlays at
/// 500-502, below the letterbox camera at 9999 and UnityExplorer at 30000), a dark panel, a header line and the
/// note text. Built on first show and destroyed on every scene load, because the game fonts are scene assets.
/// </summary>
internal sealed class NotepadView
{
    private const string RootName = "BluePrinceNotes Notepad";
    private const string FallbackFont = "TypeNeat SDF";

    private GameObject _root;
    private RectTransform _panel, _viewport;
    private TextMeshProUGUI _header, _body;
    private string _shownHeader, _shownBody, _builtFor;
    private float _nextTry;

    public bool Visible => _root != null && _root.activeSelf;

    public void Show(string header, string body, string fontName, float fontScale)
    {
        if (_body == null || _header == null || _body.font == null || _builtFor != fontName)
        {
            Destroy();
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 1f;
            if (!TryCreate(fontName)) return;
        }
        if (!_root.activeSelf) _root.SetActive(true);

        float size = Mathf.Max(12f, Screen.height * Mathf.Clamp(fontScale, 0.01f, 0.1f));
        float pad = size * 0.8f;
        _panel.sizeDelta = new Vector2(Screen.width * 0.55f, Screen.height * 0.6f);

        _header.fontSize = size * 0.75f;
        var hr = _header.rectTransform;
        hr.anchorMin = new Vector2(0f, 1f);
        hr.anchorMax = new Vector2(1f, 1f);
        hr.pivot = new Vector2(0.5f, 1f);
        hr.offsetMin = new Vector2(pad, -pad - size * 1.2f);
        hr.offsetMax = new Vector2(-pad, -pad * 0.5f);

        _viewport.anchorMin = Vector2.zero;
        _viewport.anchorMax = Vector2.one;
        _viewport.offsetMin = new Vector2(pad, pad);
        _viewport.offsetMax = new Vector2(-pad, -pad - size * 1.4f);
        _body.fontSize = size;
        var br = _body.rectTransform;
        br.anchorMin = Vector2.zero;
        br.anchorMax = Vector2.one;
        br.offsetMin = br.offsetMax = Vector2.zero;

        if (_shownHeader != header) { _header.text = header; _shownHeader = header; }
        if (_shownBody != body) { _body.text = body; _shownBody = body; }
    }

    public void Hide()
    {
        if (_root != null && _root.activeSelf) _root.SetActive(false);
    }

    public void Destroy()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;
        _panel = _viewport = null;
        _header = _body = null;
        _shownHeader = _shownBody = _builtFor = null;
    }

    private bool TryCreate(string fontName)
    {
        var font = FindFont(fontName, out var fallback);
        if (font == null)
        {
            Core.Log.Warning($"[Notepad] no usable font found");
            return false;
        }
        if (fallback)
            Core.Log.Warning($"[Notepad] font '{fontName}' not found, using '{font.name}'");

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
        bg.color = new Color(0.02f, 0.03f, 0.06f, 0.88f);
        bg.raycastTarget = false;

        _header = MakeText(panelGo, "Header", font);
        _header.color = new Color(1f, 0.97f, 0.88f, 0.6f);
        _header.alignment = TextAlignmentOptions.TopLeft;
        _header.textWrappingMode = TextWrappingModes.NoWrap;

        // The text overflows upwards as it grows; the viewport clips it below the header.
        var viewGo = new GameObject("Viewport");
        viewGo.transform.SetParent(panelGo.transform, false);
        _viewport = viewGo.AddComponent<RectTransform>();
        viewGo.AddComponent<RectMask2D>();

        _body = MakeText(viewGo, "Body", font);
        _body.color = new Color(1f, 0.97f, 0.88f, 1f);
        _body.alignment = TextAlignmentOptions.BottomLeft;  // the end of the text, where you type, stays in view
        _body.textWrappingMode = TextWrappingModes.Normal;

        _builtFor = fontName;
        Core.Log.Msg($"[Notepad] using game font '{font.name}'");
        return true;
    }

    private static TextMeshProUGUI MakeText(GameObject parent, string name, TMP_FontAsset font)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.richText = false;           // note text is shown as typed, '<' included
        t.parseCtrlCharacters = false;
        t.overflowMode = TextOverflowModes.Overflow;
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
