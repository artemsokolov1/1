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
///  5) анимации действий (необязательно) — по словам в названии файла; каждая становится состоянием аниматора
///     с триггером того же имени (игра вызывает его в момент действия):
///       Shot   — shot, shoot, kick, strike, penalty      Pass  — pass
///       Header — header                                   Slide — slide, tackle
///       Catch  — catch, save                              Dive  — dive, diving
///       Fall   — fall, trip, knocked                      Throw — throw
/// Запускается сам при открытии проекта, если префаба нет или в папке изменился набор файлов,
/// а также через меню «Mini Football → Настроить модель игрока».
/// Имена файлов: персонаж — .fbx без этих слов (например Character.fbx); ходьба/бег — idle, run/running, sprint.
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

    struct ActionDef
    {
        public string trigger; public string[] keywords;
        public ActionDef(string t, params string[] k) { trigger = t; keywords = k; }
    }

    // Порядок важен: «Goalkeeper Diving Save» — это Dive, а не Catch; «Slide Tackle» — Slide
    static readonly ActionDef[] Actions =
    {
        new ActionDef("Dive", "dive", "diving"),
        new ActionDef("Catch", "catch", "save"),
        new ActionDef("Header", "header"),
        new ActionDef("Slide", "slide", "tackle"),
        new ActionDef("Pass", "pass"),
        new ActionDef("Shot", "shot", "shoot", "kick", "strike", "penalty"),
        new ActionDef("Fall", "fall", "trip", "knocked"),
        new ActionDef("Throw", "throw"),
    };

    static PlayerModelSetup() => EditorApplication.delayCall += AutoSetup;

    static void AutoSetup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (!Directory.Exists(ModelFolder) || FindCharacter() == null) return;
        bool upToDate = File.Exists(PrefabPath) && File.Exists(SignaturePath) && File.ReadAllText(SignaturePath) == Signature();
        if (!upToDate) Setup();   // первый запуск или в папке появились/пропали файлы (например, новые анимации)
    }

    static string Signature()
    {
        var names = new List<string>();
        foreach (string f in ModelFiles()) names.Add(Path.GetFileName(f));
        names.Sort();
        return string.Join("\n", names.ToArray());
    }

    /// <summary>Какому действию принадлежит файл (по словам в названии), или null.</summary>
    static string ActionOf(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (ActionDef a in Actions)
            foreach (string k in a.keywords)
                if (n.Contains(k)) return a.trigger;
        return null;
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

        // 5. Анимации действий: состояние + триггер; из любого состояния — по триггеру, обратно в бег — по окончании
        AnimatorStateMachine sm = controller.layers[0].stateMachine;
        AnimatorState locomotion = sm.defaultState;
        var found = new List<string>();
        var used = new HashSet<string>();
        foreach (string file in ModelFiles())
        {
            string path = file.Replace('\\', '/');
            string trigger = ActionOf(path);
            if (trigger == null || used.Contains(trigger)) continue;
            ConfigureImporter(path, trigger, false);
            AnimationClip clip = LoadClip(path, trigger);
            if (clip == null) continue;
            used.Add(trigger);
            found.Add(trigger);

            controller.AddParameter(trigger, AnimatorControllerParameterType.Trigger);
            AnimatorState state = sm.AddState(trigger);
            state.motion = clip;
            state.speed = trigger == "Slide" || trigger == "Fall" || trigger == "Dive" ? 1f : 1.4f;   // удары — чуть быстрее
            AnimatorStateTransition go = sm.AddAnyStateTransition(state);
            go.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            go.hasExitTime = false;
            go.duration = 0.05f;
            go.canTransitionToSelf = false;
            if (locomotion != null)
            {
                AnimatorStateTransition back = state.AddTransition(locomotion);
                back.hasExitTime = true;
                back.exitTime = 0.85f;
                back.duration = 0.15f;
            }
        }

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
                  $"idle: {(idleClip != null ? "да" : "нет")}, бег: {(runClip != null ? "да" : "нет")}, спринт: {(sprintClip != null ? "да" : "нет")}; " +
                  $"действия: {(found.Count > 0 ? string.Join(", ", found.ToArray()) : "нет")}). Нажми Play.");
    }

    static string[] ModelFiles() =>
        Directory.Exists(ModelFolder) ? Directory.GetFiles(ModelFolder, "*.fbx") : new string[0];

    static bool IsAnimationFile(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n.Contains("idle") || n.Contains("run") || n.Contains("sprint") || ActionOf(path) != null;
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
            if (ActionOf(f) != null) continue;   // «Soccer Running Kick» — это удар, а не бег
            if (n.Contains(keyword)) return f.Replace('\\', '/');
        }
        return null;
    }

    /// <summary>Humanoid-риг; для анимаций — один клип из дубля «mixamo.com» (ходьба/бег — зациклены, действия — нет).</summary>
    static void ConfigureImporter(string path, string clipName, bool loop = true)
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
        take.loopTime = loop;
        take.lockRootRotation = true;          // поворот корня — в позу (разворачивает игрока физика)
        take.keepOriginalOrientation = true;
        take.lockRootHeightY = true;           // высота корня — в позу
        take.keepOriginalPositionY = true;
        take.lockRootPositionXZ = !loop;       // бег: не запекаем (без root motion он и так на месте); действия: запекаем — модель не «уезжает» с капсулы
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
