using System.Collections.Generic;
using BluePrinceNotes.Features;
using BluePrinceNotes.Util;
using MelonLoader;

[assembly: MelonInfo(typeof(BluePrinceNotes.Core), "Blue Prince Notes", "1.0.0", "Lucas Pospor")]
[assembly: MelonGame("Dogubomb", "BLUE PRINCE")]

namespace BluePrinceNotes;

public partial class Core : MelonMod
{
    internal static MelonLogger.Instance Log;
    internal static MelonPreferences_Category Prefs;

    private readonly List<Feature> _features = new();

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        Prefs = MelonPreferences.CreateCategory("BluePrinceNotes");

        _features.Add(new NotepadFeature());

        AddDevFeatures(_features);

        foreach (var f in _features)
            f.Init();

        Log.Msg($"Loaded {_features.Count} feature(s)");
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        FsmHooks.OnSceneLoaded();
        foreach (var f in _features)
            Run(f, () => f.OnSceneLoaded(sceneName));
    }

    public override void OnUpdate()
    {
        FsmHooks.Tick(HarmonyInstance);
        foreach (var f in _features)
        {
            f.PollEnabled();
            if (f.Enabled) Run(f, f.OnUpdate);
        }
    }

    public override void OnApplicationQuit()
    {
        foreach (var f in _features)
            Run(f, f.OnQuit);
    }

    public override void OnLateUpdate()
    {
        foreach (var f in _features)
            if (f.Enabled) Run(f, f.OnLateUpdate);
    }

    // Testing helpers, only in builds that include them.
    static partial void AddDevFeatures(List<Feature> features);

    // One broken feature must not take the others (or the game) down with it.
    internal static void Run(Feature f, System.Action a)
    {
        try { a(); }
        catch (System.Exception e)
        {
            f.Errors++;
            if (f.Errors <= 3) Log.Error($"[{f.Name}] {e}");
            if (f.Errors == 3) Log.Error($"[{f.Name}] further errors suppressed");
        }
    }
}
