using System.Globalization;
using UnityEngine;

/// <summary>
/// Общие инструменты интерфейса (IMGUI): масштаб под 1080p, шрифты ОС с кириллицей, цвета, прямоугольники, текст.
/// </summary>
public static class UI
{
    public const float RefHeight = 1080f;
    public static float Scale => Screen.height / RefHeight;
    public static float Width => Screen.width / Scale;           // «виртуальная» ширина экрана при высоте 1080

    public static readonly Color Lime = new Color(0.72f, 0.95f, 0.16f);
    public static readonly Color Dark = new Color(0.07f, 0.08f, 0.1f);
    public static readonly Color PanelColor = new Color(0.1f, 0.12f, 0.15f, 0.92f);
    public static readonly Color Orange = new Color(1f, 0.55f, 0.1f);
    public static readonly Color Muted = new Color(1f, 1f, 1f, 0.6f);

    static Font head, body;
    static Texture2D gradient;
    static GUIStyle label;

    // Шрифты берём из системы: Impact/Bahnschrift есть в Windows и поддерживают кириллицу
    public static Font Head
    {
        get
        {
            if (head == null) head = Font.CreateDynamicFontFromOSFont(new[] { "Impact", "Bahnschrift SemiBold Condensed", "Bahnschrift", "Arial Black", "Arial" }, 64);
            return head;
        }
    }
    public static Font Body
    {
        get
        {
            if (body == null) body = Font.CreateDynamicFontFromOSFont(new[] { "Bahnschrift", "Segoe UI", "Tahoma", "Arial" }, 32);
            return body;
        }
    }

    /// <summary>Горизонтальный градиент (тёмный слева → прозрачный справа) под меню.</summary>
    public static Texture2D LeftGradient
    {
        get
        {
            if (gradient == null)
            {
                gradient = new Texture2D(256, 1) { wrapMode = TextureWrapMode.Clamp };
                for (int x = 0; x < 256; x++)
                    gradient.SetPixel(x, 0, new Color(0.03f, 0.03f, 0.06f, Mathf.Pow(1f - x / 255f, 1.2f) * 0.93f));
                gradient.Apply();
            }
            return gradient;
        }
    }

    /// <summary>Вызывать в начале каждого OnGUI: всё рисуем в координатах экрана высотой 1080.</summary>
    public static void Begin() => GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(Scale, Scale, 1f));

    public static void Box(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    static GUIStyle Style(Font font, int size, Color color, TextAnchor anchor, bool wrap)
    {
        if (label == null) label = new GUIStyle(GUI.skin.label) { richText = true, clipping = TextClipping.Overflow };
        label.font = font;
        label.fontSize = size;
        label.normal.textColor = color;
        label.alignment = anchor;
        label.wordWrap = wrap;
        return label;
    }

    public static void Label(Rect r, string text, Font font, int size, Color color, TextAnchor anchor) =>
        GUI.Label(r, text, Style(font, size, color, anchor, false));

    public static void Wrapped(Rect r, string text, int size, Color color) =>
        GUI.Label(r, text, Style(Body, size, color, TextAnchor.UpperLeft, true));

    public static float TextWidth(string text, Font font, int size) =>
        Style(font, size, Color.white, TextAnchor.MiddleLeft, false).CalcSize(new GUIContent(text)).x;

    /// <summary>140616 → «140 616».</summary>
    public static string Num(int n) => n.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
}

/// <summary>
/// Главное меню в стиле современных футбольных игр (на русском) + пауза и экран итогов матча.
/// Все пункты кликабельны мышью; навигация с геймпада/клавиатуры: стик/стрелки — выбор, A/Enter — нажать, B/Esc — назад.
/// Добавляется автоматически на объект Match.
/// </summary>
public class MainMenu : MonoBehaviour
{
    enum Page { Main, Team, Store, Social, Upgrades, Practice, Club, Challenges, Swaps, Settings, Controls, QuitConfirm, ResetConfirm }

    Page page = Page.Main;
    bool pauseControls;                  // на паузе открыт экран управления
    int storeTab;                        // 0 — формы, 1 — мячи
    string toast;
    float toastTimer;
    int news;
    float newsTimer;

    // навигация геймпадом по кнопкам текущего экрана
    int focus, navIndex, navCount;
    bool activate, padNav, wasVisible;
    Vector2 lastMouse;

    static readonly string[] Roles = { "ВРАТАРЬ", "ЗАЩИТНИК", "ЗАЩИТНИК", "НАПАДАЮЩИЙ", "НАПАДАЮЩИЙ" };
    static readonly string[] RandomNames =
        { "Иванов", "Петров", "Сидоров", "Морозов", "Новиков", "Фёдоров", "Михайлов", "Белов", "Тарасов", "Жуков", "Комаров", "Киселёв" };

    MatchManager MM => MatchManager.I;

    // ------------------------------------------------------------ ввод

    void Update()
    {
        var mm = MM;
        if (mm == null) return;
        if (!mm.InMenu && !mm.Paused) { page = Page.Main; pauseControls = false; wasVisible = false; return; }
        // Первый кадр после появления меню пропускаем: нажатие, которое открыло меню, не должно сразу что-то выбрать
        if (!wasVisible) { wasVisible = true; focus = 0; return; }

        newsTimer += Time.unscaledDeltaTime;
        if (newsTimer > 5f) { newsTimer = 0f; news = (news + 1) % 3; }
        if (toastTimer > 0f) toastTimer -= Time.unscaledDeltaTime;

        bool typing = GUIUtility.keyboardControl != 0;   // курсор в поле ввода — не листаем меню клавишами W/S
        int nav = typing ? 0 : GameInput.NavVertical();
        if (nav != 0) { focus += nav; padNav = true; }
        if (GameInput.Down(Btn.Confirm) && !typing) { activate = true; padNav = true; }

        // «Назад»: B / Backspace / Esc (в матче Esc — это пауза, её обрабатывает MatchManager)
        bool back = GameInput.Down(Btn.Back) && !(mm.InMatch && GameInput.Down(Btn.Pause));
        if (!back) return;
        if (typing) { GUIUtility.keyboardControl = 0; return; }
        if (mm.Paused)
        {
            if (pauseControls) pauseControls = false; else mm.Resume();
        }
        else if (mm.LastResult != null) mm.LastResult = null;
        else if (page == Page.Main) SetPage(Page.QuitConfirm);
        else if (page == Page.Controls || page == Page.ResetConfirm) SetPage(Page.Settings);
        else SetPage(Page.Main);
    }

    void SetPage(Page p)
    {
        page = p;
        focus = 0;
        GUIUtility.keyboardControl = 0;
    }

    void Toast(string text)
    {
        toast = text;
        toastTimer = 2.5f;
    }

    // ------------------------------------------------------------ отрисовка

    void OnGUI()
    {
        var mm = MM;
        if (mm == null || (!mm.InMenu && !mm.Paused)) return;
        GUI.depth = -10;                   // поверх HUD матча
        UI.Begin();

        Event e = Event.current;
        if (e.type == EventType.Repaint)
        {
            if ((e.mousePosition - lastMouse).sqrMagnitude > 4f) padNav = false;   // мышь сдвинулась — выбор мышью
            lastMouse = e.mousePosition;
        }
        navIndex = 0;

        if (mm.Paused) DrawPause(mm);
        else if (mm.LastResult != null) DrawResult(mm);
        else
            switch (page)
            {
                case Page.Main: DrawMain(mm); break;
                case Page.Team: DrawTeam(); break;
                case Page.Store: DrawStore(mm); break;
                case Page.Social: DrawSocial(); break;
                case Page.Upgrades: DrawUpgrades(); break;
                case Page.Practice: DrawPractice(mm); break;
                case Page.Club: DrawClub(mm); break;
                case Page.Challenges: DrawChallenges(); break;
                case Page.Swaps: DrawSwaps(); break;
                case Page.Settings: DrawSettings(); break;
                case Page.Controls: DrawControls(Page.Settings); break;
                case Page.QuitConfirm: DrawQuitConfirm(); break;
                case Page.ResetConfirm: DrawResetConfirm(mm); break;
            }

        if (toastTimer > 0f && !string.IsNullOrEmpty(toast))
        {
            float w = UI.Width;
            UI.Box(new Rect(w * 0.5f - 400, 900, 800, 70), new Color(0.05f, 0.06f, 0.08f, 0.95f));
            UI.Box(new Rect(w * 0.5f - 400, 900, 8, 70), UI.Lime);
            UI.Label(new Rect(w * 0.5f - 380, 900, 780, 70), toast, UI.Body, 28, Color.white, TextAnchor.MiddleCenter);
        }

        if (e.type == EventType.Repaint)
        {
            navCount = navIndex;
            if (navCount > 0) focus = ((focus % navCount) + navCount) % navCount;
            activate = false;
        }
    }

    /// <summary>
    /// Кликабельная кнопка. Подсвечивается при наведении мышью или при выборе стиком/стрелками;
    /// срабатывает по клику или по A/Enter.
    /// </summary>
    bool Button(Rect r, string text, Font font, int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft, Color? bg = null)
    {
        int id = navIndex++;
        Event e = Event.current;
        bool hover = r.Contains(e.mousePosition);
        if (hover && !padNav) focus = id;
        bool focused = padNav ? id == focus : hover;

        if (bg.HasValue)
        {
            UI.Box(r, focused ? Color.Lerp(bg.Value, Color.white, 0.15f) : bg.Value);
            if (focused) UI.Box(new Rect(r.x, r.yMax - 5, r.width, 5), UI.Lime);     // выбранная плашка — лаймовая полоса снизу
        }
        else if (focused && anchor == TextAnchor.MiddleLeft)
            UI.Box(new Rect(r.x - 22, r.y + r.height * 0.3f, 8, r.height * 0.4f), UI.Lime);
        Rect tr = bg.HasValue ? new Rect(r.x + 18, r.y, r.width - 36, r.height) : r;
        UI.Label(tr, text, font, size, focused && !bg.HasValue ? UI.Lime : color, anchor);

        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
        if (activate && id == focus && e.type == EventType.Repaint) { activate = false; clicked = true; }
        return clicked;
    }

    bool PanelButton(Rect r, string text, int size = 30) =>
        Button(r, text, UI.Head, size, Color.white, TextAnchor.MiddleCenter, UI.PanelColor);

    bool LimeButton(Rect r, string text, int size = 30)
    {
        UI.Box(r, UI.Lime);
        return Button(r, text, UI.Head, size, UI.Dark, TextAnchor.MiddleCenter, new Color(0.72f, 0.95f, 0.16f, 0f));
    }

    static void Tag(Rect r, string text, Color bg, Color fg)
    {
        UI.Box(r, bg);
        UI.Label(r, text, UI.Head, (int)(r.height * 0.7f), fg, TextAnchor.MiddleCenter);
    }

    string TextField(Rect r, string value, int max)
    {
        var st = new GUIStyle(GUI.skin.textField) { font = UI.Body, fontSize = 28, alignment = TextAnchor.MiddleLeft };
        st.padding = new RectOffset(14, 14, 6, 6);
        return GUI.TextField(r, value, max, st);
    }

    void Frame(string title)
    {
        float w = UI.Width;
        UI.Box(new Rect(0, 0, w, 1080), new Color(0.03f, 0.04f, 0.06f, 0.86f));
        UI.Label(new Rect(60, 40, 1400, 110), title, UI.Head, 96, Color.white, TextAnchor.MiddleLeft);
        Wallet(new Rect(w - 520, 60, 460, 50));
    }

    static void Wallet(Rect r)
    {
        var p = Profile.Current;
        UI.Box(new Rect(r.x, r.y + 14, 22, 22), new Color(1f, 0.8f, 0.2f));
        UI.Label(new Rect(r.x + 32, r.y, 220, r.height), UI.Num(p.coins) + " монет", UI.Body, 26, Color.white, TextAnchor.MiddleLeft);
        UI.Box(new Rect(r.x + 260, r.y + 14, 22, 22), UI.Lime);
        UI.Label(new Rect(r.x + 292, r.y, 200, r.height), UI.Num(p.tokens) + " жетонов", UI.Body, 26, Color.white, TextAnchor.MiddleLeft);
    }

    bool BackButton() => Button(new Rect(60, 990, 300, 60), "НАЗАД", UI.Head, 44, Color.white);

    // ------------------------------------------------------------ главный экран

    void DrawMain(MatchManager mm)
    {
        var p = Profile.Current;
        float w = UI.Width;
        GUI.DrawTexture(new Rect(0, 0, w * 0.6f, 1080), UI.LeftGradient);

        // Логотип
        UI.Label(new Rect(60, 24, 900, 64), "МИНИ-ФУТБОЛ <color=#B8F229>5 НА 5</color>", UI.Head, 46, Color.white, TextAnchor.MiddleLeft);

        // Крупные пункты
        float y = 140;
        if (Button(new Rect(60, y, 620, 120), "ИГРАТЬ", UI.Head, 124, Color.white)) mm.StartMatch(MatchMode.Normal);
        y += 120;
        if (Button(new Rect(60, y, 620, 120), "КОМАНДА", UI.Head, 124, Color.white)) SetPage(Page.Team);
        y += 120;
        if (Button(new Rect(60, y, 620, 120), "МАГАЗИН", UI.Head, 124, Color.white)) SetPage(Page.Store);
        Tag(new Rect(60 + UI.TextWidth("МАГАЗИН", UI.Head, 124) + 24, y + 42, 118, 44), "НОВОЕ", UI.Lime, UI.Dark);

        // Мелкие пункты
        y = 540;
        string[] items = { "СОЦИАЛЬНОЕ", "ПРОКАЧКА ИГРОКОВ", "ТРЕНИРОВКИ", "НАСТРОЙКА КЛУБА", "ИСПЫТАНИЯ", "ОБМЕН", "НАСТРОЙКИ", "ВЫХОД" };
        Page[] targets = { Page.Social, Page.Upgrades, Page.Practice, Page.Club, Page.Challenges, Page.Swaps, Page.Settings, Page.QuitConfirm };
        for (int i = 0; i < items.Length; i++)
        {
            Rect r = new Rect(60, y, 560, 50);
            if (Button(r, items[i], UI.Head, 42, Color.white)) SetPage(targets[i]);
            float tx = 60 + UI.TextWidth(items[i], UI.Head, 42) + 16;
            if (targets[i] == Page.Practice)
                Tag(new Rect(tx, y + 8, 64, 34), $"{p.PracticeDoneCount()}/{Catalog.Practices.Length}", Color.white, UI.Dark);
            if (targets[i] == Page.Challenges && p.ChallengesReady() > 0)
                Tag(new Rect(tx, y + 8, 50, 34), "+" + p.ChallengesReady(), UI.Lime, UI.Dark);
            y += 50;
        }

        // Правая колонка: профиль
        float x0 = w - 500;
        if (Button(new Rect(x0, 40, 440, 52), "", UI.Body, 24, Color.white, TextAnchor.MiddleLeft, new Color(0.12f, 0.14f, 0.18f, 0.92f)))
            SetPage(Page.Social);
        UI.Box(new Rect(x0, 40, 52, 52), Catalog.Kits[p.kit].color);
        UI.Label(new Rect(x0, 40, 52, 52), p.nickname.Length > 0 ? p.nickname.Substring(0, 1).ToUpper() : "?", UI.Head, 30, Color.white, TextAnchor.MiddleCenter);
        UI.Label(new Rect(x0 + 68, 40, 360, 52), p.nickname, UI.Body, 26, Color.white, TextAnchor.MiddleLeft);
        UI.Box(new Rect(x0, 98, 440, 40), new Color(0.12f, 0.14f, 0.18f, 0.6f));
        Wallet(new Rect(x0 + 14, 93, 440, 50));

        // Новости (карусель, кликабельна)
        Rect nr = new Rect(x0, 277, 440, 264);
        Color[] newsBg = { new Color(0.35f, 0.15f, 0.55f, 0.95f), new Color(0.1f, 0.35f, 0.25f, 0.95f), new Color(0.55f, 0.2f, 0.1f, 0.95f) };
        string[] newsTitle = { "НОВАЯ ФОРМА\n<color=#B8F229>УЖЕ В МАГАЗИНЕ</color>", "ТРЕНИРОВКИ\n<color=#B8F229>ЗАРАБОТАЙ МОНЕТЫ</color>", "ПРОКАЧКА\n<color=#B8F229>СТАНЬ БЫСТРЕЕ</color>" };
        string[] newsText = { "Фиолетовый комплект за жетоны", "Три сценария с наградами", "Скорость, удар и контроль мяча" };
        Page[] newsPage = { Page.Store, Page.Practice, Page.Upgrades };
        if (Button(nr, "", UI.Body, 20, Color.white, TextAnchor.MiddleLeft, newsBg[news])) SetPage(newsPage[news]);
        UI.Box(new Rect(nr.x, nr.y, 26, 26), UI.Orange);
        for (int i = 0; i < 3; i++) UI.Box(new Rect(nr.x + 40 + i * 44, nr.y + 20, i == news ? 64 : 36, 6), new Color(1f, 1f, 1f, i == news ? 0.9f : 0.4f));
        UI.Label(new Rect(nr.x + 40, nr.y + 60, 380, 140), newsTitle[news], UI.Head, 50, Color.white, TextAnchor.MiddleLeft);
        UI.Label(new Rect(nr.x + 40, nr.y + 205, 380, 40), newsText[news], UI.Body, 22, Color.white, TextAnchor.MiddleLeft);

        // Испытания: первые 4, выполненные подсвечены — клик забирает награду
        Rect cr = new Rect(x0, 570, 440, 86);
        UI.Box(cr, new Color(0.18f, 0.2f, 0.26f, 0.95f));
        UI.Label(new Rect(cr.x + 22, cr.y, 400, cr.height), "ИСПЫТАНИЯ", UI.Head, 46, Color.white, TextAnchor.MiddleLeft);
        float cy = 660;
        for (int i = 0; i < 4 && i < Catalog.Challenges.Length; i++)
        {
            var ch = Catalog.Challenges[i];
            int prog = p.challengeProgress[i];
            bool ready = prog >= ch.target && !p.challengeClaimed[i];
            bool claimed = p.challengeClaimed[i];
            Rect row = new Rect(x0, cy, 440, 80);
            if (Button(row, "", UI.Body, 20, Color.white, TextAnchor.MiddleLeft, ready ? UI.Lime : new Color(0.12f, 0.14f, 0.18f, 0.95f)))
            {
                if (ready) Claim(i); else SetPage(Page.Challenges);
            }
            Color fg = ready ? UI.Dark : Color.white;
            // «Кольцо» прогресса — квадрат с заливкой снизу вверх
            Rect ring = new Rect(row.x + 16, row.y + 14, 52, 52);
            UI.Box(ring, new Color(0f, 0f, 0f, 0.35f));
            float f = Mathf.Clamp01(prog / (float)ch.target);
            UI.Box(new Rect(ring.x, ring.yMax - ring.height * f, ring.width, ring.height * f), ready ? UI.Dark : UI.Lime);
            UI.Label(new Rect(row.x + 86, row.y + 8, 340, 34), ch.title, UI.Body, 22, fg, TextAnchor.MiddleLeft);
            string reward = claimed ? "Получено" : "+" + UI.Num(ch.reward) + (ch.rewardTokens ? " жетон" : " монет");
            UI.Label(new Rect(row.x + 86, row.y + 40, 250, 32), reward, UI.Body, 20, ready ? UI.Dark : UI.Muted, TextAnchor.MiddleLeft);
            UI.Label(new Rect(row.x + 300, row.y + 40, 124, 32), $"{prog}/{ch.target}", UI.Body, 20, fg, TextAnchor.MiddleRight);
            cy += 84;
        }

        UI.Label(new Rect(w - 520, 1030, 460, 34), "Вер. 0.4.0 — прототип", UI.Body, 20, UI.Muted, TextAnchor.MiddleRight);
    }

    void Claim(int i)
    {
        var p = Profile.Current;
        var ch = Catalog.Challenges[i];
        if (p.challengeClaimed[i] || p.challengeProgress[i] < ch.target) { Toast("Испытание ещё не выполнено"); return; }
        p.challengeClaimed[i] = true;
        if (ch.rewardTokens) p.tokens += ch.reward; else p.coins += ch.reward;
        p.Save();
        Toast($"Награда получена: +{UI.Num(ch.reward)} {(ch.rewardTokens ? "жетон" : "монет")}");
    }

    // ------------------------------------------------------------ команда

    void DrawTeam()
    {
        var p = Profile.Current;
        Frame("КОМАНДА");
        UI.Label(new Rect(60, 150, 1200, 40), "Имена игроков можно изменить. Характеристики растут от прокачки.", UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);

        float y = 210;
        for (int i = 0; i < 5; i++)
        {
            Rect row = new Rect(60, y, 1500, 130);
            UI.Box(row, UI.PanelColor);
            UI.Box(new Rect(row.x, row.y, 10, row.height), i == 4 ? UI.Lime : Catalog.Kits[p.kit].color);
            UI.Label(new Rect(row.x + 30, row.y + 10, 300, 40), Roles[i], UI.Head, 30, UI.Lime, TextAnchor.MiddleLeft);
            UI.Label(new Rect(row.x + 30, row.y + 55, 300, 60), i == 4 ? "стартовый игрок" : "", UI.Body, 20, UI.Muted, TextAnchor.MiddleLeft);

            string name = TextField(new Rect(row.x + 330, row.y + 35, 420, 60), p.playerNames[i], 16);
            if (name != p.playerNames[i]) { p.playerNames[i] = name; p.Save(); }

            bool keeper = i == 0;
            int spd = 60 + p.speedLvl * 7 - (keeper ? 8 : 0);
            int sht = 58 + p.shotLvl * 7 - (keeper ? 15 : 0);
            int ctl = 62 + p.controlLvl * 7 + (keeper ? 10 : 0);
            StatBar(new Rect(row.x + 800, row.y + 20, 320, 26), "СКО", spd);
            StatBar(new Rect(row.x + 800, row.y + 52, 320, 26), "УДР", sht);
            StatBar(new Rect(row.x + 800, row.y + 84, 320, 26), "КОН", ctl);
            UI.Label(new Rect(row.x + 1180, row.y, 280, row.height), ((spd + sht + ctl) / 3).ToString(), UI.Head, 72, Color.white, TextAnchor.MiddleRight);
            y += 140;
        }

        if (PanelButton(new Rect(1240, 990, 320, 60), "СЛУЧАЙНЫЕ ИМЕНА", 28))
        {
            for (int i = 0; i < 5; i++) p.playerNames[i] = RandomNames[Random.Range(0, RandomNames.Length)];
            p.Save();
            Toast("Имена обновлены");
        }
        if (BackButton()) SetPage(Page.Main);
    }

    static void StatBar(Rect r, string label, int value)
    {
        UI.Label(new Rect(r.x, r.y, 60, r.height), label, UI.Body, 20, UI.Muted, TextAnchor.MiddleLeft);
        UI.Box(new Rect(r.x + 60, r.y + 8, 200, r.height - 16), new Color(1f, 1f, 1f, 0.12f));
        UI.Box(new Rect(r.x + 60, r.y + 8, 200 * Mathf.Clamp01(value / 100f), r.height - 16), value >= 80 ? UI.Lime : Color.white);
        UI.Label(new Rect(r.x + 270, r.y, 50, r.height), value.ToString(), UI.Body, 20, Color.white, TextAnchor.MiddleLeft);
    }

    // ------------------------------------------------------------ магазин

    void DrawStore(MatchManager mm)
    {
        var p = Profile.Current;
        Frame("МАГАЗИН");
        if (Button(new Rect(60, 160, 200, 56), "ФОРМЫ", UI.Head, 36, storeTab == 0 ? UI.Lime : Color.white)) storeTab = 0;
        if (Button(new Rect(290, 160, 200, 56), "МЯЧИ", UI.Head, 36, storeTab == 1 ? UI.Lime : Color.white)) storeTab = 1;

        Catalog.Item[] items = storeTab == 0 ? Catalog.Kits : Catalog.Balls;
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            bool owned = storeTab == 0 ? p.ownedKits.Contains(i) : p.ownedBalls.Contains(i);
            bool equipped = storeTab == 0 ? p.kit == i : p.ballSkin == i;
            Rect card = new Rect(60 + (i % 5) * 330, 250 + (i / 5) * 360, 300, 330);

            if (Button(card, "", UI.Body, 20, Color.white, TextAnchor.MiddleLeft, UI.PanelColor)) BuyOrEquip(mm, storeTab, i);
            if (storeTab == 0)
            {
                UI.Box(new Rect(card.x + 95, card.y + 30, 110, 170), it.color);                // «футболка»
                UI.Box(new Rect(card.x + 60, card.y + 30, 180, 60), it.color);
            }
            else
            {
                UI.Box(new Rect(card.x + 90, card.y + 50, 120, 120), it.color);                // «мяч»
                UI.Box(new Rect(card.x + 130, card.y + 50, 40, 120), new Color(0f, 0f, 0f, 0.15f));
            }
            UI.Label(new Rect(card.x + 16, card.y + 215, 268, 40), it.name, UI.Head, 30, Color.white, TextAnchor.MiddleCenter);
            string price = equipped ? "<color=#B8F229>ВЫБРАНО</color>"
                         : owned ? "КУПЛЕНО — ВЫБРАТЬ"
                         : it.price == 0 ? "БЕСПЛАТНО"
                         : UI.Num(it.price) + (it.forTokens ? " жетона" : " монет");
            UI.Label(new Rect(card.x + 16, card.y + 262, 268, 40), price, UI.Body, 24, Color.white, TextAnchor.MiddleCenter);
        }

        if (BackButton()) SetPage(Page.Main);
    }

    void BuyOrEquip(MatchManager mm, int tab, int i)
    {
        var p = Profile.Current;
        var it = tab == 0 ? Catalog.Kits[i] : Catalog.Balls[i];
        var owned = tab == 0 ? p.ownedKits : p.ownedBalls;
        if (!owned.Contains(i))
        {
            if (it.forTokens ? p.tokens < it.price : p.coins < it.price)
            {
                Toast(it.forTokens ? "Не хватает жетонов — загляни в «Обмен»" : "Не хватает монет — сыграй матч или пройди тренировку");
                return;
            }
            if (it.forTokens) p.tokens -= it.price; else p.coins -= it.price;
            owned.Add(i);
            Toast($"Куплено: {it.name}");
        }
        else Toast($"Выбрано: {it.name}");
        if (tab == 0) p.kit = i; else p.ballSkin = i;
        p.Save();
        mm.RefreshLook();
    }

    // ------------------------------------------------------------ социальное

    void DrawSocial()
    {
        var p = Profile.Current;
        Frame("СОЦИАЛЬНОЕ");
        UI.Label(new Rect(60, 170, 400, 40), "НИК", UI.Head, 30, UI.Lime, TextAnchor.MiddleLeft);
        string nick = TextField(new Rect(60, 215, 520, 64), p.nickname, 20);
        if (nick != p.nickname) { p.nickname = nick; p.Save(); }

        string[] labels = { "МАТЧИ", "ПОБЕДЫ", "НИЧЬИ", "ПОРАЖЕНИЯ", "ЗАБИТО", "ПРОПУЩЕНО", "% ПОБЕД" };
        int winRate = p.matches > 0 ? Mathf.RoundToInt(100f * p.wins / p.matches) : 0;
        int[] values = { p.matches, p.wins, p.draws, p.losses, p.goalsFor, p.goalsAgainst, winRate };
        for (int i = 0; i < labels.Length; i++)
        {
            Rect r = new Rect(60 + (i % 4) * 330, 320 + (i / 4) * 190, 310, 170);
            UI.Box(r, UI.PanelColor);
            UI.Label(new Rect(r.x + 20, r.y + 15, 280, 40), labels[i], UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);
            UI.Label(new Rect(r.x + 20, r.y + 60, 280, 90), values[i].ToString(), UI.Head, 80, Color.white, TextAnchor.MiddleLeft);
        }

        UI.Label(new Rect(60, 720, 1300, 40), "Онлайн-матчи с друзьями появятся позже. Пока можно поделиться статистикой или позвать друга.",
                 UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);
        if (PanelButton(new Rect(60, 780, 480, 70), "СКОПИРОВАТЬ СТАТИСТИКУ"))
        {
            GUIUtility.systemCopyBuffer = $"{p.nickname} ({p.clubName}): матчей {p.matches}, побед {p.wins}, ничьих {p.draws}, " +
                                          $"поражений {p.losses}, голы {p.goalsFor}:{p.goalsAgainst}";
            Toast("Статистика скопирована в буфер обмена");
        }
        if (PanelButton(new Rect(570, 780, 480, 70), "ПРИГЛАСИТЬ ДРУГА"))
        {
            GUIUtility.systemCopyBuffer = $"Сыграй в Мини-футбол 5 на 5! Мой ник: {p.nickname}";
            Toast("Приглашение скопировано — отправь его другу");
        }
        if (BackButton()) SetPage(Page.Main);
    }

    // ------------------------------------------------------------ прокачка

    void DrawUpgrades()
    {
        var p = Profile.Current;
        Frame("ПРОКАЧКА ИГРОКОВ");
        string[] names = { "СКОРОСТЬ", "УДАР", "КОНТРОЛЬ МЯЧА" };
        string[] desc = { "+0.25 м/с к бегу и +0.3 м/с к спринту за уровень", "+1 м/с к силе удара за уровень", "Радиус приёма мяча больше — легче принять пас и отобрать" };
        int[] lvls = { p.speedLvl, p.shotLvl, p.controlLvl };

        for (int i = 0; i < 3; i++)
        {
            Rect row = new Rect(60, 200 + i * 220, 1500, 190);
            UI.Box(row, UI.PanelColor);
            UI.Label(new Rect(row.x + 30, row.y + 20, 600, 60), names[i], UI.Head, 54, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(row.x + 30, row.y + 85, 800, 40), desc[i], UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);
            for (int l = 0; l < Catalog.MaxUpgrade; l++)
                UI.Box(new Rect(row.x + 30 + l * 70, row.y + 140, 60, 22), l < lvls[i] ? UI.Lime : new Color(1f, 1f, 1f, 0.15f));

            bool max = lvls[i] >= Catalog.MaxUpgrade;
            int price = Catalog.UpgradePrice(lvls[i]);
            Rect br = new Rect(row.x + 1060, row.y + 55, 410, 80);
            bool click = max ? PanelButton(br, "МАКСИМУМ") : LimeButton(br, $"УЛУЧШИТЬ — {UI.Num(price)}");
            if (click)
            {
                if (max) Toast("Уже максимальный уровень");
                else if (p.coins < price) Toast("Не хватает монет");
                else
                {
                    p.coins -= price;
                    if (i == 0) p.speedLvl++; else if (i == 1) p.shotLvl++; else p.controlLvl++;
                    p.Save();
                    Toast($"{names[i]}: уровень {lvls[i] + 1}");
                }
            }
        }
        if (BackButton()) SetPage(Page.Main);
    }

    // ------------------------------------------------------------ тренировки

    void DrawPractice(MatchManager mm)
    {
        var p = Profile.Current;
        Frame("ТРЕНИРОВКИ");
        for (int i = 0; i < Catalog.Practices.Length; i++)
        {
            var pr = Catalog.Practices[i];
            Rect card = new Rect(60 + i * 560, 200, 520, 640);
            UI.Box(card, UI.PanelColor);
            UI.Box(new Rect(card.x, card.y, card.width, 8), p.practiceDone[i] ? UI.Lime : UI.Orange);
            UI.Label(new Rect(card.x + 30, card.y + 30, 470, 60), pr.title.ToUpper(), UI.Head, 40, Color.white, TextAnchor.MiddleLeft);
            UI.Wrapped(new Rect(card.x + 30, card.y + 110, 460, 200), pr.description, 26, UI.Muted);
            UI.Label(new Rect(card.x + 30, card.y + 330, 460, 40), $"Время: {pr.time:0} с   Цель: {pr.goalsNeeded} гол(а)", UI.Body, 24, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(card.x + 30, card.y + 380, 460, 40),
                     p.practiceDone[i] ? "<color=#B8F229>ПРОЙДЕНО</color>" : $"Награда: {UI.Num(pr.reward)} монет",
                     UI.Body, 26, Color.white, TextAnchor.MiddleLeft);
            if (LimeButton(new Rect(card.x + 30, card.y + 530, 460, 80), "НАЧАТЬ", 40)) mm.StartMatch(pr.mode, i);
        }
        if (BackButton()) SetPage(Page.Main);
    }

    // ------------------------------------------------------------ клуб

    void DrawClub(MatchManager mm)
    {
        var p = Profile.Current;
        Frame("НАСТРОЙКА КЛУБА");
        UI.Label(new Rect(60, 170, 600, 40), "НАЗВАНИЕ КЛУБА", UI.Head, 30, UI.Lime, TextAnchor.MiddleLeft);
        string club = TextField(new Rect(60, 215, 620, 64), p.clubName, 24);
        if (club != p.clubName) { p.clubName = club; p.Save(); }

        UI.Label(new Rect(60, 320, 600, 40), "ФОРМА (КУПЛЕННЫЕ)", UI.Head, 30, UI.Lime, TextAnchor.MiddleLeft);
        float x = 60;
        foreach (int i in p.ownedKits)
        {
            Rect r = new Rect(x, 370, 200, 170);
            if (Button(r, "", UI.Body, 20, Color.white, TextAnchor.MiddleLeft, p.kit == i ? new Color(0.25f, 0.3f, 0.12f, 0.95f) : UI.PanelColor))
            {
                p.kit = i; p.Save(); mm.RefreshLook(); Toast("Форма: " + Catalog.Kits[i].name);
            }
            UI.Box(new Rect(r.x + 60, r.y + 20, 80, 90), Catalog.Kits[i].color);
            UI.Label(new Rect(r.x, r.y + 120, 200, 40), Catalog.Kits[i].name, UI.Body, 20, Color.white, TextAnchor.MiddleCenter);
            x += 220;
        }

        UI.Label(new Rect(60, 580, 600, 40), "МЯЧ (КУПЛЕННЫЕ)", UI.Head, 30, UI.Lime, TextAnchor.MiddleLeft);
        x = 60;
        foreach (int i in p.ownedBalls)
        {
            Rect r = new Rect(x, 630, 200, 170);
            if (Button(r, "", UI.Body, 20, Color.white, TextAnchor.MiddleLeft, p.ballSkin == i ? new Color(0.25f, 0.3f, 0.12f, 0.95f) : UI.PanelColor))
            {
                p.ballSkin = i; p.Save(); mm.RefreshLook(); Toast("Мяч: " + Catalog.Balls[i].name);
            }
            UI.Box(new Rect(r.x + 60, r.y + 25, 80, 80), Catalog.Balls[i].color);
            UI.Label(new Rect(r.x, r.y + 120, 200, 40), Catalog.Balls[i].name, UI.Body, 20, Color.white, TextAnchor.MiddleCenter);
            x += 220;
        }

        if (PanelButton(new Rect(60, 850, 520, 70), "КУПИТЬ БОЛЬШЕ В МАГАЗИНЕ")) SetPage(Page.Store);
        if (BackButton()) SetPage(Page.Main);
    }

    // ------------------------------------------------------------ испытания

    void DrawChallenges()
    {
        var p = Profile.Current;
        Frame("ИСПЫТАНИЯ");
        for (int i = 0; i < Catalog.Challenges.Length; i++)
        {
            var ch = Catalog.Challenges[i];
            int prog = p.challengeProgress[i];
            bool ready = prog >= ch.target && !p.challengeClaimed[i];
            Rect row = new Rect(60, 190 + i * 150, 1500, 130);
            UI.Box(row, ready ? new Color(0.25f, 0.32f, 0.1f, 0.95f) : UI.PanelColor);
            UI.Label(new Rect(row.x + 30, row.y + 15, 900, 50), ch.title.ToUpper(), UI.Head, 40, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(row.x + 30, row.y + 70, 500, 40),
                     "Награда: " + UI.Num(ch.reward) + (ch.rewardTokens ? " жетон" : " монет"), UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);
            UI.Box(new Rect(row.x + 560, row.y + 82, 400, 16), new Color(1f, 1f, 1f, 0.12f));
            UI.Box(new Rect(row.x + 560, row.y + 82, 400 * Mathf.Clamp01(prog / (float)ch.target), 16), UI.Lime);
            UI.Label(new Rect(row.x + 970, row.y + 70, 120, 40), $"{prog}/{ch.target}", UI.Body, 24, Color.white, TextAnchor.MiddleLeft);

            Rect br = new Rect(row.x + 1130, row.y + 25, 340, 80);
            bool click = p.challengeClaimed[i] ? PanelButton(br, "ПОЛУЧЕНО")
                       : ready ? LimeButton(br, "ЗАБРАТЬ")
                       : PanelButton(br, "В ПРОЦЕССЕ");
            if (click)
            {
                if (p.challengeClaimed[i]) Toast("Награда уже получена");
                else Claim(i);
            }
        }
        if (BackButton()) SetPage(Page.Main);
    }

    // ------------------------------------------------------------ обмен

    void DrawSwaps()
    {
        var p = Profile.Current;
        Frame("ОБМЕН");
        UI.Label(new Rect(60, 160, 1400, 40), "Жетоны нужны для редких вещей в магазине. Их можно купить за монеты или продать обратно.",
                 UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);

        string[] titles = { "КУПИТЬ 1 ЖЕТОН", "КУПИТЬ 5 ЖЕТОНОВ", "ПРОДАТЬ 1 ЖЕТОН" };
        string[] subs =
        {
            $"{UI.Num(Catalog.TokenBuyPrice)} монет → 1 жетон",
            $"{UI.Num(Catalog.TokenBuyPrice * 5)} монет → 5 жетонов",
            $"1 жетон → {UI.Num(Catalog.TokenSellPrice)} монет",
        };
        for (int i = 0; i < 3; i++)
        {
            Rect card = new Rect(60 + i * 520, 240, 480, 360);
            UI.Box(card, UI.PanelColor);
            UI.Box(new Rect(card.x + 30, card.y + 40, 60, 60), i == 2 ? new Color(1f, 0.8f, 0.2f) : UI.Lime);
            UI.Label(new Rect(card.x + 30, card.y + 120, 420, 60), titles[i], UI.Head, 44, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(card.x + 30, card.y + 185, 420, 40), subs[i], UI.Body, 26, UI.Muted, TextAnchor.MiddleLeft);
            if (LimeButton(new Rect(card.x + 30, card.y + 260, 420, 70), "ОБМЕНЯТЬ"))
            {
                if (i < 2)
                {
                    int n = i == 0 ? 1 : 5;
                    if (p.coins < Catalog.TokenBuyPrice * n) { Toast("Не хватает монет"); continue; }
                    p.coins -= Catalog.TokenBuyPrice * n;
                    p.tokens += n;
                    Toast($"+{n} жетон(ов)");
                }
                else
                {
                    if (p.tokens < 1) { Toast("Нет жетонов для продажи"); continue; }
                    p.tokens--;
                    p.coins += Catalog.TokenSellPrice;
                    Toast($"+{UI.Num(Catalog.TokenSellPrice)} монет");
                }
                p.Save();
            }
        }
        if (BackButton()) SetPage(Page.Main);
    }

    // ------------------------------------------------------------ настройки

    void DrawSettings()
    {
        var p = Profile.Current;
        Frame("НАСТРОЙКИ");
        float y = 200;

        if (PanelButton(Row(ref y), $"ДЛИТЕЛЬНОСТЬ МАТЧА:  {Catalog.MatchLengths[p.matchLength] / 60} МИН"))
        { p.matchLength = (p.matchLength + 1) % Catalog.MatchLengths.Length; p.Save(); }
        if (PanelButton(Row(ref y), $"СЛОЖНОСТЬ:  {Catalog.Difficulties[p.difficulty].ToUpper()}"))
        { p.difficulty = (p.difficulty + 1) % 3; p.Save(); MM.RefreshLook(); }
        if (PanelButton(Row(ref y), $"КАМЕРА:  {Catalog.CameraModes[p.cameraMode].ToUpper()}"))
        { p.cameraMode = (p.cameraMode + 1) % Catalog.CameraModes.Length; p.Save(); }
        if (PanelButton(Row(ref y), $"АВТОСМЕНА ИГРОКА:  {Catalog.AutoSwitchModes[p.autoSwitch].ToUpper()}"))
        { p.autoSwitch = (p.autoSwitch + 1) % Catalog.AutoSwitchModes.Length; p.Save(); }
        if (PanelButton(Row(ref y), $"ПОДСКАЗКИ УПРАВЛЕНИЯ:  {(p.showHints ? "ВКЛ" : "ВЫКЛ")}"))
        { p.showHints = !p.showHints; p.Save(); }
        if (PanelButton(Row(ref y), $"ПОЛНЫЙ ЭКРАН:  {(Screen.fullScreen ? "ВКЛ" : "ВЫКЛ")}"))
            Screen.fullScreen = !Screen.fullScreen;
        if (PanelButton(Row(ref y), "УПРАВЛЕНИЕ (ГЕЙМПАД И КЛАВИАТУРА)")) SetPage(Page.Controls);
        if (PanelButton(Row(ref y), "<color=#FF8A3D>СБРОСИТЬ ПРОГРЕСС</color>")) SetPage(Page.ResetConfirm);

        if (BackButton()) SetPage(Page.Main);
    }

    static Rect Row(ref float y)
    {
        Rect r = new Rect(60, y, 1000, 76);
        y += 90;
        return r;
    }

    void DrawControls(Page backTo)
    {
        Frame("УПРАВЛЕНИЕ");
        string[,] rows =
        {
            { "АТАКА", "XBOX", "PLAYSTATION", "КЛАВИАТУРА" },
            { "Бег / финты", "Левый стик / правый стик", "L3 / R3", "WASD / T F G H" },
            { "Пас низом (с RB — прострел)", "A", "Крест", "J" },
            { "Удар, головой (с RB — закрученный)", "B", "Круг", "K" },
            { "Навес / длинный / перевод", "X", "Квадрат", "L" },
            { "Пас на ход (с RB — навесом)", "Y", "Треугольник", "I" },
            { "Укрывание корпусом / модификатор", "RB", "R1", "E" },
            { "Забегание партнёра / смена", "LB", "L1", "Q" },
            { "Спринт / медленное ведение", "RT / LT", "R2 / L2", "Shift / Space" },
            { "ОБОРОНА", "", "", "" },
            { "Сдерживание (держать)", "A", "Крест", "J" },
            { "Подкат / отбор", "B / X", "Круг / Квадрат", "K / L" },
            { "Выход вратаря / прессинг партнёра", "Y / RB", "Треугольник / R1", "I / E" },
            { "Смена игрока / выжидание", "LB, правый стик / LT", "L1, R3 / L2", "Q, TFGH / Space" },
            { "Пауза", "Start", "Options", "Esc" },
        };
        float[] cols = { 60, 640, 1000, 1360 };
        for (int r = 0; r < rows.GetLength(0); r++)
        {
            float y = 170 + r * 52;
            if (r % 2 == 1) UI.Box(new Rect(50, y, 1700, 52), new Color(1f, 1f, 1f, 0.04f));
            for (int c = 0; c < 4; c++)
            {
                bool head = r == 0 || rows[r, 1] == "";
                UI.Label(new Rect(cols[c], y, 560, 52), rows[r, c], head ? UI.Head : UI.Body, head ? 30 : 24,
                         head ? UI.Lime : Color.white, TextAnchor.MiddleLeft);
            }
        }
        if (BackButton())
        {
            if (MM.Paused) pauseControls = false; else SetPage(backTo);
        }
    }

    // ------------------------------------------------------------ диалоги

    bool Dialog(string title, string text, string yes, string no, out bool yesClicked)
    {
        float w = UI.Width;
        UI.Box(new Rect(0, 0, w, 1080), new Color(0f, 0f, 0f, 0.75f));
        Rect card = new Rect(w * 0.5f - 450, 330, 900, 400);
        UI.Box(card, new Color(0.1f, 0.12f, 0.15f, 0.98f));
        UI.Box(new Rect(card.x, card.y, card.width, 8), UI.Lime);
        UI.Label(new Rect(card.x, card.y + 40, card.width, 80), title, UI.Head, 64, Color.white, TextAnchor.MiddleCenter);
        UI.Label(new Rect(card.x, card.y + 130, card.width, 50), text, UI.Body, 26, UI.Muted, TextAnchor.MiddleCenter);
        yesClicked = LimeButton(new Rect(card.x + 60, card.y + 260, 370, 80), yes, 36);
        bool noClicked = PanelButton(new Rect(card.x + 470, card.y + 260, 370, 80), no, 36);
        return yesClicked || noClicked;
    }

    void DrawQuitConfirm()
    {
        if (Dialog("ВЫЙТИ ИЗ ИГРЫ?", "Прогресс сохраняется автоматически.", "ДА, ВЫЙТИ", "ОТМЕНА", out bool yes))
        {
            if (yes)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
            SetPage(Page.Main);
        }
    }

    void DrawResetConfirm(MatchManager mm)
    {
        if (Dialog("СБРОСИТЬ ПРОГРЕСС?", "Монеты, покупки, прокачка и статистика будут удалены.", "СБРОСИТЬ", "ОТМЕНА", out bool yes))
        {
            if (yes) { Profile.ResetAll(); mm.RefreshLook(); Toast("Прогресс сброшен"); }
            SetPage(Page.Settings);
        }
    }

    // ------------------------------------------------------------ итог матча и пауза

    void DrawResult(MatchManager mm)
    {
        var r = mm.LastResult;
        float w = UI.Width;
        UI.Box(new Rect(0, 0, w, 1080), new Color(0.02f, 0.03f, 0.05f, 0.8f));
        Rect card = new Rect(w * 0.5f - 520, 200, 1040, 640);
        UI.Box(card, new Color(0.1f, 0.12f, 0.15f, 0.97f));
        UI.Box(new Rect(card.x, card.y, card.width, 10), r.good ? UI.Lime : UI.Orange);
        UI.Label(new Rect(card.x, card.y + 40, card.width, 90), r.title, UI.Head, 80, r.good ? UI.Lime : UI.Orange, TextAnchor.MiddleCenter);
        UI.Label(new Rect(card.x, card.y + 140, card.width, 130), r.score, UI.Head, 120, Color.white, TextAnchor.MiddleCenter);
        UI.Label(new Rect(card.x, card.y + 270, card.width, 40),
                 $"{Profile.Current.clubName}  —  {Catalog.OpponentClub}", UI.Body, 26, UI.Muted, TextAnchor.MiddleCenter);
        for (int i = 0; i < r.lines.Count; i++)
            UI.Label(new Rect(card.x, card.y + 330 + i * 40, card.width, 40), r.lines[i], UI.Body, 28, Color.white, TextAnchor.MiddleCenter);

        if (LimeButton(new Rect(card.x + 90, card.y + 520, 400, 80), "В МЕНЮ", 38)) mm.LastResult = null;
        if (PanelButton(new Rect(card.x + 550, card.y + 520, 400, 80), "СЫГРАТЬ ЕЩЁ", 38))
        {
            mm.LastResult = null;
            mm.StartMatch(MatchMode.Normal);
        }
    }

    void DrawPause(MatchManager mm)
    {
        if (pauseControls) { DrawControls(Page.Main); return; }
        float w = UI.Width;
        GUI.DrawTexture(new Rect(0, 0, w * 0.6f, 1080), UI.LeftGradient);
        UI.Box(new Rect(0, 0, w, 1080), new Color(0f, 0f, 0f, 0.35f));
        UI.Label(new Rect(60, 120, 900, 130), "ПАУЗА", UI.Head, 124, Color.white, TextAnchor.MiddleLeft);

        float y = 300;
        if (Button(new Rect(60, y, 600, 70), "ПРОДОЛЖИТЬ", UI.Head, 60, Color.white)) mm.Resume();
        y += 80;
        if (Button(new Rect(60, y, 600, 70), "НАЧАТЬ ЗАНОВО", UI.Head, 60, Color.white)) mm.RestartMatch();
        y += 80;
        if (Button(new Rect(60, y, 600, 70), "УПРАВЛЕНИЕ", UI.Head, 60, Color.white)) { pauseControls = true; focus = 0; }
        y += 80;
        if (Button(new Rect(60, y, 600, 70), "ВЫЙТИ В МЕНЮ", UI.Head, 60, Color.white)) mm.QuitToMenu();
        UI.Label(new Rect(60, 700, 900, 40), "Выход в меню во время матча — без наград.", UI.Body, 24, UI.Muted, TextAnchor.MiddleLeft);
    }
}
