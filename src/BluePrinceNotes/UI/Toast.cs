using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BluePrinceNotes.UI;

/// <summary>
/// A short message near the bottom of the screen that fades out, e.g. when the hotkey can't open the notepad.
/// Its own small canvas, above the notepad's; destroyed on scene load like the panel (the fonts are scene assets).
/// </summary>
internal sealed class Toast
{
    private const float ShowTime = 1.8f, FadeTime = 0.4f;
    private static readonly Color Back = new(0.02f, 0.03f, 0.06f, 0.72f);
    private static readonly Color Text = new(1f, 0.97f, 0.88f, 1f);

    private GameObject _root;
    private RectTransform _strip;
    private Image _bg;
    private TextMeshProUGUI _text;
    private CanvasGroup _group;
    private float _until = -1f;

    public void Show(string message, string fontName)
    {
        if (!Ensure(fontName)) return;
        float size = Mathf.Max(12f, Screen.height * 0.026f);
        _text.fontSize = size;
        _text.text = message;
        var pref = _text.GetPreferredValues(message, Screen.width * 0.8f, 0f);
        _strip.sizeDelta = new Vector2(pref.x + 2f * size, pref.y + 0.6f * size);
        _strip.anchoredPosition = new Vector2(0f, Screen.height * 0.18f);
        _group.alpha = 1f;
        _root.SetActive(true);
        _until = Time.unscaledTime + ShowTime;
    }

    public void Tick()
    {
        if (_root == null || !_root.activeSelf) return;
        float left = _until - Time.unscaledTime;
        if (left <= 0f) Hide();
        else _group.alpha = Mathf.Clamp01(left / FadeTime);
    }

    public void Hide()
    {
        if (_root != null && _root.activeSelf) _root.SetActive(false);
    }

    public void Destroy()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _text = null;
    }

    private bool Ensure(string fontName)
    {
        if (_root != null && _text != null && _text.font != null) return true;
        Destroy();
        var font = NotepadView.FindFont(fontName, out _);
        if (font == null) return false;

        _root = new GameObject("BluePrinceNotes Toast");
        Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 601;
        _group = _root.AddComponent<CanvasGroup>();
        _group.interactable = false;
        _group.blocksRaycasts = false;

        var stripGo = new GameObject("Strip");
        stripGo.transform.SetParent(_root.transform, false);
        _strip = stripGo.AddComponent<RectTransform>();
        _strip.anchorMin = _strip.anchorMax = new Vector2(0.5f, 0f);
        _strip.pivot = new Vector2(0.5f, 0.5f);
        _bg = stripGo.AddComponent<Image>();
        _bg.color = Back;
        _bg.raycastTarget = false;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(stripGo.transform, false);
        _text = textGo.AddComponent<TextMeshProUGUI>();
        _text.font = font;
        _text.color = Text;
        _text.richText = false;
        _text.alignment = TextAlignmentOptions.Center;
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.raycastTarget = false;
        var rt = _text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        _root.SetActive(false);
        return true;
    }
}
