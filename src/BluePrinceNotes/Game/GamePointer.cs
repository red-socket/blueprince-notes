using Il2Cpp;
using UnityEngine;

namespace BluePrinceNotes.Game;

/// <summary>
/// The game draws its mouse pointer itself (CustomCursorRenderer, in OnRenderImage), under every overlay canvas, so
/// the notepad page would hide it. While the notepad is open the game's pointer is switched off
/// (CustomCursorRenderer.DrawCursor, a static flag that SetMouseCursor2 sets back to true) and the notepad draws the
/// same "1-CURSOR" art on top of the page. The flag is put back on every close path.
/// </summary>
internal static class GamePointer
{
    private const string PointerTexture = "1-CURSOR";

    private static bool _hidden, _before;
    private static Texture2D _tex;
    private static float _nextSearch;

    public static void Hide()
    {
        try
        {
            if (!_hidden)
            {
                _before = CustomCursorRenderer.DrawCursor;
                _hidden = true;
            }
            CustomCursorRenderer.DrawCursor = false;  // also every frame: a cursor change in the game turns it back on
        }
        catch (System.Exception e)
        {
            Core.Log.Warning($"[Notepad] hiding the game pointer: {e.Message}");
        }
    }

    public static void Restore()
    {
        if (!_hidden) return;
        _hidden = false;
        try { CustomCursorRenderer.DrawCursor = _before; }
        catch (System.Exception e) { Core.Log.Warning($"[Notepad] restoring the game pointer: {e.Message}"); }
    }

    /// <summary>The game's UI pointer texture (looked up once, and again if it was unloaded).</summary>
    public static Texture2D Texture
    {
        get
        {
            if (_tex != null) return _tex;
            if (Time.unscaledTime < _nextSearch) return null;  // scanning every texture is slow: not every frame
            _nextSearch = Time.unscaledTime + 5f;
            foreach (var t in Resources.FindObjectsOfTypeAll<Texture2D>())
                if (t != null && t.name == PointerTexture) return _tex = t;
            return null;
        }
    }
}
