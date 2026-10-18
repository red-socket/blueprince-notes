using MelonLoader;

namespace BluePrinceNotes.Features;

public abstract class Feature
{
    public abstract string Name { get; }
    protected virtual string Description => "";
    protected virtual bool DefaultEnabled => true;
    internal int Errors;

    private MelonPreferences_Entry<bool> _enabled;
    private bool _wasEnabled;
    public bool Enabled => _enabled.Value;

    public void Init()
    {
        _enabled = Core.Prefs.CreateEntry($"{Name}_Enabled", DefaultEnabled, $"{Name}: enabled", Description);
        _wasEnabled = _enabled.Value;
        CreatePrefs();
    }

    /// <summary>
    /// Core stops calling a disabled feature, so it must restore what it changed right away. Polled from
    /// Core.OnUpdate rather than the prefs event, which may fire on another thread.
    /// </summary>
    internal void PollEnabled()
    {
        bool on = _enabled.Value;
        if (_wasEnabled && !on) Core.Run(this, OnTurnedOff);
        _wasEnabled = on;
    }

    protected virtual void CreatePrefs() { }
    protected virtual void OnTurnedOff() { }
    public virtual void OnSceneLoaded(string sceneName) { }
    public virtual void OnUpdate() { }
    public virtual void OnLateUpdate() { }
    /// <summary>The game is quitting (a clean quit; nothing runs on a crash).</summary>
    public virtual void OnQuit() { }
}
