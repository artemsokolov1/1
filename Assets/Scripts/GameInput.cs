using UnityEngine;
#if MF_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

/// <summary>
/// Игровые действия. Раскладка геймпада — как в FIFA 20 (руководство EA): в атаке и в обороне одни и те же кнопки
/// делают разное. Клавиатура: J/K/L/I — A/B/X/Y, E — RB, Q — LB, Shift — RT, Space — LT, T/F/G/H — правый стик.
/// </summary>
public enum Btn
{
    Pass,       // A / Крест        — атака: пас низом (RB+A — прострел)       | оборона: сдерживание (держать)
    Shoot,      // B / Круг         — атака: удар / головой (RB+B — закрученный) | оборона: отбор / толчок корпусом
    Lob,        // X / Квадрат      — атака: навес / длинный пас / перевод      | оборона: подкат
    Through,    // Y / Треугольник  — атака: пас на ход (RB+Y — навесом)        | оборона: выход вратаря (держать)
    Sprint,     // RT / R2          — ускорение
    Switch,     // LB / L1          — атака с мячом: забегание партнёра; без мяча — смена игрока
    Modifier,   // RB / R1          — атака: укрывание мяча / модификатор точного удара и паса | оборона: прессинг партнёра
    Jockey,     // LT / L2          — атака: медленное ведение, укрывание | оборона: выжидание лицом к атаке
    Pause,      // Start / Options  — Esc
    Confirm,    // A / Крест        — Enter (меню)
    Back,       // B / Круг         — Esc / Backspace (меню)
}

/// <summary>
/// Обёртка ввода. С пакетом Input System работают клавиатура и любой геймпад (Xbox, PlayStation, Switch Pro).
/// Без него — клавиатура и базовые кнопки джойстика через старый Input Manager.
/// </summary>
public static class GameInput
{
    const float StickDeadZone = 0.2f;
    static bool rightStickLatched;
    static float navRepeatTimer;
    static int lastNav;

    /// <summary>Последнее устройство — геймпад? (для подсказок в HUD)</summary>
    public static bool UsingGamepad { get; private set; }

#if MF_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
    static Keyboard K => Keyboard.current;
    static Gamepad G => Gamepad.current;

    static KeyControl Key(Btn b)
    {
        var k = K;
        if (k == null) return null;
        switch (b)
        {
            case Btn.Pass: return k.jKey;
            case Btn.Shoot: return k.kKey;
            case Btn.Lob: return k.lKey;
            case Btn.Through: return k.iKey;
            case Btn.Sprint: return k.leftShiftKey;
            case Btn.Switch: return k.qKey;
            case Btn.Modifier: return k.eKey;
            case Btn.Jockey: return k.spaceKey;
            case Btn.Pause: return k.escapeKey;
            case Btn.Confirm: return k.enterKey;
            case Btn.Back: return k.backspaceKey;
        }
        return null;
    }

    static KeyControl AltKey(Btn b)
    {
        var k = K;
        if (k == null) return null;
        if (b == Btn.Back) return k.escapeKey;
        if (b == Btn.Confirm) return k.numpadEnterKey;
        return null;
    }

    static ButtonControl Pad(Btn b)
    {
        var g = G;
        if (g == null) return null;
        switch (b)
        {
            case Btn.Pass: return g.buttonSouth;
            case Btn.Shoot: return g.buttonEast;
            case Btn.Lob: return g.buttonWest;
            case Btn.Through: return g.buttonNorth;
            case Btn.Sprint: return g.rightTrigger;
            case Btn.Switch: return g.leftShoulder;
            case Btn.Modifier: return g.rightShoulder;
            case Btn.Jockey: return g.leftTrigger;
            case Btn.Pause: return g.startButton;
            case Btn.Confirm: return g.buttonSouth;
            case Btn.Back: return g.buttonEast;
        }
        return null;
    }

    public static bool Down(Btn b)
    {
        bool pad = Pad(b) != null && Pad(b).wasPressedThisFrame;
        if (pad) UsingGamepad = true;
        bool key = (Key(b) != null && Key(b).wasPressedThisFrame) || (AltKey(b) != null && AltKey(b).wasPressedThisFrame);
        if (key) UsingGamepad = false;
        return pad || key;
    }

    public static bool Held(Btn b) =>
        (Pad(b) != null && Pad(b).isPressed) || (Key(b) != null && Key(b).isPressed) || (AltKey(b) != null && AltKey(b).isPressed);

    public static bool Up(Btn b) =>
        (Pad(b) != null && Pad(b).wasReleasedThisFrame) || (Key(b) != null && Key(b).wasReleasedThisFrame);

    public static Vector2 Move()
    {
        Vector2 v = Vector2.zero;
        var k = K;
        if (k != null)
        {
            v.x = (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1f : 0f) - (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1f : 0f);
            v.y = (k.wKey.isPressed || k.upArrowKey.isPressed ? 1f : 0f) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1f : 0f);
            if (v.sqrMagnitude > 0f) UsingGamepad = false;
        }
        var g = G;
        if (g != null)
        {
            Vector2 s = g.leftStick.ReadValue();
            if (s.magnitude > StickDeadZone) { v += s; UsingGamepad = true; }
        }
        return Vector2.ClampMagnitude(v, 1f);
    }

    /// <summary>Правый стик геймпада или T/F/G/H на клавиатуре (финты, смена игрока по направлению).</summary>
    public static Vector2 RightStick()
    {
        Vector2 v = G != null ? G.rightStick.ReadValue() : Vector2.zero;
        var k = K;
        if (k != null)
        {
            Vector2 kv = new Vector2((k.hKey.isPressed ? 1f : 0f) - (k.fKey.isPressed ? 1f : 0f),
                                     (k.tKey.isPressed ? 1f : 0f) - (k.gKey.isPressed ? 1f : 0f));
            if (kv.sqrMagnitude > 0f) v = kv;
        }
        return v;
    }

    static int RawNavVertical()
    {
        int n = 0;
        var k = K;
        if (k != null)
        {
            if (k.upArrowKey.isPressed || k.wKey.isPressed) n = -1;
            if (k.downArrowKey.isPressed || k.sKey.isPressed) n = 1;
        }
        var g = G;
        if (g != null)
        {
            float y = g.leftStick.ReadValue().y + (g.dpad.up.isPressed ? 1f : 0f) - (g.dpad.down.isPressed ? 1f : 0f);
            if (y > 0.5f) n = -1;
            if (y < -0.5f) n = 1;
        }
        return n;
    }
#else
    static KeyCode KeyOf(Btn b)
    {
        switch (b)
        {
            case Btn.Pass: return KeyCode.J;
            case Btn.Shoot: return KeyCode.K;
            case Btn.Lob: return KeyCode.L;
            case Btn.Through: return KeyCode.I;
            case Btn.Sprint: return KeyCode.LeftShift;
            case Btn.Switch: return KeyCode.Q;
            case Btn.Modifier: return KeyCode.E;
            case Btn.Jockey: return KeyCode.Space;
            case Btn.Pause: return KeyCode.Escape;
            case Btn.Confirm: return KeyCode.Return;
            case Btn.Back: return KeyCode.Backspace;
        }
        return KeyCode.None;
    }

    // Кнопки джойстика в старом Input Manager (раскладка Xbox на Windows). Курки здесь недоступны — только клавиатура.
    static KeyCode PadOf(Btn b)
    {
        switch (b)
        {
            case Btn.Pass: case Btn.Confirm: return KeyCode.JoystickButton0;
            case Btn.Shoot: case Btn.Back: return KeyCode.JoystickButton1;
            case Btn.Lob: return KeyCode.JoystickButton2;
            case Btn.Through: return KeyCode.JoystickButton3;
            case Btn.Switch: return KeyCode.JoystickButton4;
            case Btn.Modifier: return KeyCode.JoystickButton5;
            case Btn.Pause: return KeyCode.JoystickButton7;
        }
        return KeyCode.None;
    }

    public static bool Down(Btn b)
    {
        bool pad = PadOf(b) != KeyCode.None && Input.GetKeyDown(PadOf(b));
        if (pad) UsingGamepad = true;
        bool key = Input.GetKeyDown(KeyOf(b)) || (b == Btn.Back && Input.GetKeyDown(KeyCode.Escape));
        if (key) UsingGamepad = false;
        return pad || key;
    }
    public static bool Held(Btn b) => Input.GetKey(KeyOf(b)) || (PadOf(b) != KeyCode.None && Input.GetKey(PadOf(b)));
    public static bool Up(Btn b) => Input.GetKeyUp(KeyOf(b)) || (PadOf(b) != KeyCode.None && Input.GetKeyUp(PadOf(b)));
    public static Vector2 Move() => Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
    public static Vector2 RightStick() => new Vector2((Input.GetKey(KeyCode.H) ? 1f : 0f) - (Input.GetKey(KeyCode.F) ? 1f : 0f),
                                                     (Input.GetKey(KeyCode.T) ? 1f : 0f) - (Input.GetKey(KeyCode.G) ? 1f : 0f));

    static int RawNavVertical()
    {
        float y = Input.GetAxisRaw("Vertical");
        return y > 0.5f ? -1 : y < -0.5f ? 1 : 0;
    }
#endif

    /// <summary>Резкий «флик» правым стиком (как в FIFA) — возвращает направление один раз, пока стик не отпущен.</summary>
    public static bool RightStickFlick(out Vector2 dir)
    {
        dir = RightStick();
        if (dir.magnitude < 0.4f) { rightStickLatched = false; return false; }
        if (rightStickLatched || dir.magnitude < 0.75f) return false;
        rightStickLatched = true;
        UsingGamepad = true;
        return true;
    }

    /// <summary>Навигация по меню: -1 вверх, +1 вниз, 0 — ничего. С автоповтором при удержании.</summary>
    public static int NavVertical()
    {
        int n = RawNavVertical();
        if (n == 0) { lastNav = 0; return 0; }
        if (n != lastNav) { lastNav = n; navRepeatTimer = 0.4f; return n; }
        navRepeatTimer -= Time.unscaledDeltaTime;
        if (navRepeatTimer > 0f) return 0;
        navRepeatTimer = 0.12f;
        return n;
    }
}
