using UnityEditor;
using UnityEngine;

/// <summary>
/// Добавляет в старый Input Manager оси курков и правого стика геймпада (раскладка Xbox на Windows).
/// Нужны, только если в проекте выключен пакет Input System: без них RT (спринт), LT и правый стик не читаются.
/// Уже существующие оси не трогает. Срабатывает один раз при открытии редактора.
/// </summary>
[InitializeOnLoad]
static class InputAxesSetup
{
    static InputAxesSetup() => EditorApplication.delayCall += AddAxes;

    struct AxisDef
    {
        public string name; public int axis; public bool invert;
        public AxisDef(string n, int a, bool inv = false) { name = n; axis = a; invert = inv; }
    }

    static readonly AxisDef[] Axes =
    {
        new AxisDef("MF RT", 9),          // 10-я ось — правый курок (0…1)
        new AxisDef("MF LT", 8),          // 9-я ось — левый курок (0…1)
        new AxisDef("MF RX", 3),          // 4-я ось — правый стик X
        new AxisDef("MF RY", 4),          // 5-я ось — правый стик Y
    };

    static void AddAxes()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/InputManager.asset");
        if (assets == null || assets.Length == 0) return;
        var so = new SerializedObject(assets[0]);
        SerializedProperty axes = so.FindProperty("m_Axes");
        if (axes == null) return;

        bool changed = false;
        foreach (AxisDef def in Axes)
        {
            if (HasAxis(axes, def.name)) continue;
            axes.arraySize++;
            SerializedProperty a = axes.GetArrayElementAtIndex(axes.arraySize - 1);
            a.FindPropertyRelative("m_Name").stringValue = def.name;
            a.FindPropertyRelative("descriptiveName").stringValue = "";
            a.FindPropertyRelative("descriptiveNegativeName").stringValue = "";
            a.FindPropertyRelative("negativeButton").stringValue = "";
            a.FindPropertyRelative("positiveButton").stringValue = "";
            a.FindPropertyRelative("altNegativeButton").stringValue = "";
            a.FindPropertyRelative("altPositiveButton").stringValue = "";
            a.FindPropertyRelative("gravity").floatValue = 0f;
            a.FindPropertyRelative("dead").floatValue = 0.1f;
            a.FindPropertyRelative("sensitivity").floatValue = 1f;
            a.FindPropertyRelative("snap").boolValue = false;
            a.FindPropertyRelative("invert").boolValue = def.invert;
            a.FindPropertyRelative("type").intValue = 2;          // ось джойстика
            a.FindPropertyRelative("axis").intValue = def.axis;
            a.FindPropertyRelative("joyNum").intValue = 0;        // любой джойстик
            changed = true;
        }
        if (!changed) return;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log("Мини-футбол: в Input Manager добавлены оси курков и правого стика (MF RT, MF LT, MF RX, MF RY).");
    }

    static bool HasAxis(SerializedProperty axes, string name)
    {
        for (int i = 0; i < axes.arraySize; i++)
            if (axes.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue == name) return true;
        return false;
    }
}
