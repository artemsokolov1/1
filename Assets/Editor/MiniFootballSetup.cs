using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

/// <summary>
/// Первый запуск проекта: создаёт сцену Assets/Scenes/Main.unity (камера, свет, Ball, Match),
/// добавляет её в Build Settings и открывает. Дальше — просто нажать Play.
/// Пересоздать сцену можно через меню «Mini Football → Пересоздать сцену».
/// </summary>
[InitializeOnLoad]
static class MiniFootballSetup
{
    const string ScenePath = "Assets/Scenes/Main.unity";
    const string OpenedKey = "MiniFootball.SceneOpened";

    static MiniFootballSetup() => EditorApplication.delayCall += OnEditorReady;

    static void OnEditorReady()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (SessionState.GetBool(OpenedKey, false)) return;   // только один раз за сессию редактора
        SessionState.SetBool(OpenedKey, true);

        Scene active = SceneManager.GetActiveScene();
        bool untouchedUntitled = string.IsNullOrEmpty(active.path) && !active.isDirty;

        if (!File.Exists(ScenePath)) BuildScene();
        else if (untouchedUntitled) EditorSceneManager.OpenScene(ScenePath);
    }

    [MenuItem("Mini Football/Пересоздать сцену")]
    static void RebuildFromMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) BuildScene();
    }

    static void BuildScene()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        MatchManager.CreateSceneObjects();   // Ball + Match со ссылками на мяч и Main Camera
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.Refresh();
    }
}
