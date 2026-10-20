using UnityEngine;
using UnityEngine.SceneManagement;

/// Entry point for the Menu Scene.
///
/// The menu scene holds only a camera rig, a light and the environment
/// vignette; everything interactive is created here at runtime (same
/// code-first pattern as HikingPlayerController in the gameplay scene).
/// The player stands still and looks around — there is no CharacterController
/// and no locomotion in this scene by design.
///
/// Session handoff: the choices live on MovementModeManager, which is already a
/// DontDestroyOnLoad singleton, so confirming here and loading the gameplay
/// scene is the whole mechanism. HikingPlayerController reads
/// MovementModeManager.SessionConfirmed on start and goes straight into the
/// hike — no extra config object, no serialisation.
public class MenuSceneBootstrap : MonoBehaviour
{
    /// Scene names, as they appear in Build Settings.
    public const string MenuScene     = "MenuScene";
    public const string GameplayScene = "SampleScene";

    /// True when the menu scene is actually in Build Settings. Guards the
    /// gameplay scene's "bounce back to the menu" path so a missing entry
    /// degrades to a warning rather than a silently dead Play button.
    public static bool MenuSceneAvailable => Application.CanStreamedLevelBeLoaded(MenuScene);

    bool starting;

    void Start()
    {
        var rig  = FindAnyObjectByType<OVRCameraRig>();
        var head = rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;

        // Match the gameplay scene's tracking origin so the menu sits at the
        // same eye height the hike will use.
        if (OVRManager.instance != null)
            OVRManager.instance.trackingOriginType = OVRManager.TrackingOrigin.EyeLevel;

        var mm = MovementModeManager.GetOrCreate();

        VRPointer.Create(rig, head);
        MenuSceneUI.Create(head, mm, StartWorkout);
    }

    void StartWorkout()
    {
        if (starting) return;      // the load takes a frame; ignore a double click
        starting = true;

        // Persists the choice and flips SessionConfirmed, which is what the
        // gameplay scene keys off.
        MovementModeManager.Instance.ConfirmSession();
        SceneManager.LoadScene(GameplayScene);
    }
}
