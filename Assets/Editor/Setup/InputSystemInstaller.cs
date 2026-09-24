using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

/// <summary>
/// Ставит пакет Input System (нужен для геймпада), если его нет в проекте.
/// Версия в манифесте не зашита — Unity сама берёт рекомендованную для твоего редактора,
/// поэтому проект открывается в любой Unity 6.x без ошибок совместимости.
/// Лежит в отдельной сборке без зависимостей: работает, даже если остальной код ещё не скомпилировался.
/// </summary>
[InitializeOnLoad]
static class InputSystemInstaller
{
    const string PackageName = "com.unity.inputsystem";
    const string TriedKey = "MiniFootball.InputSystemInstallTried";
    static AddRequest request;

    static InputSystemInstaller() => EditorApplication.delayCall += TryInstall;

    static void TryInstall()
    {
        if (SessionState.GetBool(TriedKey, false)) return;   // одна попытка за сессию редактора
        SessionState.SetBool(TriedKey, true);
        if (UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName) != null)
        {
            EnsureBackendEnabled();
            return;
        }

        Debug.Log("Мини-футбол: устанавливаю Input System для геймпада (версию подберёт Unity)…");
        request = Client.Add(PackageName);
        EditorApplication.update += WaitForRequest;
    }

    static void WaitForRequest()
    {
        if (!request.IsCompleted) return;
        EditorApplication.update -= WaitForRequest;
        if (request.Status == StatusCode.Success)
        {
            Debug.Log($"Мини-футбол: Input System {request.Result.version} установлен.");
            EditorApplication.delayCall += EnsureBackendEnabled;
        }
        else
            Debug.LogWarning("Мини-футбол: не удалось установить Input System (" + request.Error.message +
                             "). Поставь вручную: Window → Package Manager → Unity Registry → Input System → Install.");
    }

    /// <summary>
    /// Пакет стоит, но в Player Settings включён только старый ввод (Active Input Handling = Input Manager) —
    /// тогда не работают курки RT/LT и правый стик. Предлагаем включить режим «Both» и перезапустить редактор.
    /// </summary>
    static void EnsureBackendEnabled()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (assets == null || assets.Length == 0) return;
        var so = new SerializedObject(assets[0]);
        SerializedProperty prop = so.FindProperty("activeInputHandler");   // 0 — старый, 1 — Input System, 2 — оба
        if (prop == null || prop.intValue != 0) return;
        bool ok = EditorUtility.DisplayDialog("Мини-футбол",
            "Пакет Input System установлен, но выключен. Без него не работают курки RT/LT (спринт, выжидание) и правый стик.\n\n" +
            "Включить (режим «Both») и перезапустить редактор?", "Да, включить", "Позже");
        if (!ok) return;
        prop.intValue = 2;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        EditorApplication.OpenProject(Directory.GetCurrentDirectory());
    }
}
