using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Подключает 3D-модель игрока (Mixamo) из Assets/Models/Player:
///  1) переводит все .fbx в Humanoid (аватар из каждой модели — скелет Mixamo одинаковый, анимации переносятся);
///  2) в файлах анимаций берёт дубль «mixamo.com», зацикливает его, запекает поворот и высоту корня в позу;
///  3) собирает аниматор Assets/Resources/Models/PlayerAnimator.controller: смесь Idle → Running по параметру Speed;
///  4) делает префаб Assets/Resources/Models/PlayerModel.prefab ростом ~1.85 м — его подхватывает MatchManager.
/// Запускается сам при открытии проекта, если префаба нет или в папке изменился набор файлов,
/// а также через меню «Mini Football → Настроить модель игрока».
/// Имена файлов: персонаж — .fbx без слов idle/run/sprint (например Character.fbx); ходьба/бег — idle, run/running, sprint.
/// </summary>
[InitializeOnLoad]
static class PlayerModelSetup
{
    const string ModelFolder = "Assets/Models/Player";
    const string OutFolder = "Assets/Resources/Models";
    const string ControllerPath = OutFolder + "/PlayerAnimator.controller";
    const string PrefabPath = OutFolder + "/PlayerModel.prefab";
    const float TargetHeight = 1.85f;
    const string SignaturePath = OutFolder + "/PlayerModel.files.txt";   // какие файлы были при последней сборке
    public const float RunThreshold = 5.5f;   // скорость (м/с), при которой анимация бега играет в своём темпе

    static PlayerModelSetup() => EditorApplication.delayCall += AutoSetup;

    static void AutoSetup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (!Directory.Exists(ModelFolder) || FindCharacter() == null) return;
        bool upToDate = File.Exists(PrefabPath) && File.Exists(SignaturePath) && File.ReadAllText(SignaturePath) == Signature();
        if (!upToDate) Setup();   // первый запуск или в папке появились/пропали файлы
    }

    static string Signature()
    {
        var names = new List<string>();
        foreach (string f in ModelFiles()) names.Add(Path.GetFileName(f));
        names.Sort();
        return string.Join("\n", names.ToArray());
    }

    [MenuItem("Mini Football/Настроить модель игрока")]
    public static void Setup()
    {
        string character = FindCharacter();
        if (character == null)
        {
            Debug.LogWarning($"Мини-футбол: в {ModelFolder} нет .fbx персонажа.");
            return;
        }
        string idle = FindByKeyword("idle"), run = FindByKeyword("run"), sprint = FindByKeyword("sprint");

        // 1–2. Настройки импорта
        ConfigureImporter(character, null);
        if (idle != null) ConfigureImporter(idle, "Idle");
        if (run != null) ConfigureImporter(run, "Running");
        if (sprint != null) ConfigureImporter(sprint, "Sprint");

        // 3. Аниматор
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder(OutFolder)) AssetDatabase.CreateFolder("Assets/Resources", "Models");
        AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        AnimationClip idleClip = LoadClip(idle, "Idle"), runClip = LoadClip(run, "Running"), sprintClip = LoadClip(sprint, "Sprint");
        if (idleClip != null) tree.AddChild(idleClip, 0f);
        if (runClip != null) tree.AddChild(runClip, RunThreshold);
        if (sprintClip != null) tree.AddChild(sprintClip, 8.5f);

        // 4. Префаб нужного роста
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(character);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        instance.name = "PlayerModel";
        instance.transform.position = Vector3.zero;
        instance.transform.rotation = Quaternion.identity;

        float height = MeasureHeight(instance);
        if (height > 0.01f) instance.transform.localScale = Vector3.one * (TargetHeight / height);

        var animator = instance.GetComponent<Animator>();
        if (animator == null) animator = instance.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;                        // двигает капсула (физика), модель — только анимирует
        animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

        AssetDatabase.DeleteAsset(PrefabPath);
        PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);
        Object.DestroyImmediate(instance);
        File.WriteAllText(SignaturePath, Signature());
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Мини-футбол: модель игрока готова ({Path.GetFileName(character)}; " +
                  $"idle: {(idleClip != null ? "да" : "нет")}, бег: {(runClip != null ? "да" : "нет")}, спринт: {(sprintClip != null ? "да" : "нет")}). Нажми Play.");
    }

    static string[] ModelFiles() =>
        Directory.Exists(ModelFolder) ? Directory.GetFiles(ModelFolder, "*.fbx") : new string[0];

    static bool IsAnimationFile(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n.Contains("idle") || n.Contains("run") || n.Contains("sprint");
    }

    static string FindCharacter()
    {
        foreach (string f in ModelFiles())
            if (!IsAnimationFile(f)) return f.Replace('\\', '/');
        return null;
    }

    static string FindByKeyword(string keyword)
    {
        foreach (string f in ModelFiles())
        {
            string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            if (keyword == "run" && n.Contains("sprint")) continue;
            if (n.Contains(keyword)) return f.Replace('\\', '/');
        }
        return null;
    }

    /// <summary>Humanoid-риг; для анимаций — один зацикленный клип из дубля «mixamo.com».</summary>
    static void ConfigureImporter(string path, string clipName)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null) return;
        bool changed = false;
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            changed = true;
        }
        if (changed) importer.SaveAndReimport();   // после смены рига дубли анимаций пересчитываются

        if (clipName == null) return;
        ModelImporterClipAnimation[] defaults = importer.defaultClipAnimations;
        if (defaults == null || defaults.Length == 0) return;
        // Настоящая анимация Mixamo — дубль «mixamo.com» (второй, «Take 001», — пустая T-поза); иначе берём самый длинный
        ModelImporterClipAnimation take = null;
        foreach (var d in defaults)
            if (d.takeName == "mixamo.com") take = d;
        if (take == null)
        {
            take = defaults[0];
            foreach (var d in defaults)
                if (d.lastFrame - d.firstFrame > take.lastFrame - take.firstFrame) take = d;
        }

        take.name = clipName;
        take.loopTime = true;
        take.lockRootRotation = true;          // поворот корня — в позу (разворачивает игрока физика)
        take.keepOriginalOrientation = true;
        take.lockRootHeightY = true;           // высота корня — в позу
        take.keepOriginalPositionY = true;
        importer.clipAnimations = new[] { take };
        importer.SaveAndReimport();
    }

    static AnimationClip LoadClip(string path, string clipName)
    {
        if (path == null) return null;
        foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            var clip = o as AnimationClip;
            if (clip != null && !clip.name.StartsWith("__preview__") && clip.name == clipName) return clip;
        }
        return null;
    }

    /// <summary>Рост модели по мешам (в позе привязки).</summary>
    static float MeasureHeight(GameObject go)
    {
        var renderers = new List<Renderer>(go.GetComponentsInChildren<Renderer>());
        if (renderers.Count == 0) return 0f;
        Bounds b = renderers[0].bounds;
        foreach (var r in renderers) b.Encapsulate(r.bounds);
        return b.size.y;
    }
}
