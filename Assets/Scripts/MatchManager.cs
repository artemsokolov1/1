using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ядро матча: строит поле с разметкой, бортами и воротами, спавнит 10 игроков,
/// считает голы и время, сбрасывает позиции, ведёт изометрическую камеру и рисует HUD.
/// Вешается на пустой объект "Match"; в инспекторе нужно указать Ball и Main Camera.
/// Ось X — вдоль поля: красные защищают ворота на -X и атакуют в +X, синие — наоборот.
/// </summary>
public class MatchManager : MonoBehaviour
{
    public static MatchManager I { get; private set; }

    [Header("Ссылки на объекты сцены")]
    public Ball ball;
    public Camera cam;

    [Header("Размеры поля, м")]
    public float length = 40f, width = 24f;
    public float goalWidth = 6f, goalHeight = 2f, goalDepth = 2f;
    public float boxDepth = 5f, boxHalfWidth = 5f;   // штрафная

    [Header("Матч")]
    public float matchTime = 60f;
    public float goalPause = 1.5f;      // пауза после гола до сброса позиций
    public float kickoffPause = 0.7f;   // пауза перед стартом розыгрыша

    [Header("Камера (изометрия)")]
    public Vector3 camAngles = new Vector3(45f, 45f, 0f);
    public float camSize = 11f, camDistance = 40f, camSmooth = 4f;

    [HideInInspector] public List<Player> players = new List<Player>();

    int scoreRed, scoreBlue;
    float timeLeft, pauseTimer;
    bool matchOver, resetPending;
    string banner;
    Player human;
    Collider goalLeft, goalRight;                    // триггеры ворот красных (-X) и синих (+X)
    readonly Player[] chaser = new Player[2];        // кто в каждой команде сейчас бежит к мячу

    public bool Frozen => matchOver || pauseTimer > 0f;
    float L => length * 0.5f;
    float W => width * 0.5f;

    void Awake() => I = this;

    // Start, а не Awake: к этому моменту Ball.Awake уже отработал
    void Start()
    {
        BuildArena();
        SpawnTeams();

        cam.orthographic = true;
        cam.orthographicSize = camSize;
        cam.transform.rotation = Quaternion.Euler(camAngles);
        cam.transform.position = -cam.transform.forward * camDistance;

        Restart();
    }

    void Restart()
    {
        scoreRed = scoreBlue = 0;
        timeLeft = matchTime;
        matchOver = false;
        banner = null;
        KickOff();
    }

    /// <summary>Сброс позиций: игроки по местам, мяч в центр, короткая пауза.</summary>
    void KickOff()
    {
        foreach (var p in players) p.ResetTo(p.homePos);
        ball.ResetTo(new Vector3(0f, ball.Radius + 0.01f, 0f));
        pauseTimer = kickoffPause;
        resetPending = false;
    }

    void Update()
    {
        if (GameInput.RestartPressed()) Restart();

        if (pauseTimer > 0f)
        {
            pauseTimer -= Time.deltaTime;
            if (pauseTimer <= 0f && resetPending) { banner = null; KickOff(); }
        }
        else if (!matchOver)
        {
            timeLeft -= Time.deltaTime;
            if (timeLeft <= 0f)
            {
                timeLeft = 0f;
                matchOver = true;
                banner = (scoreRed == scoreBlue ? "Ничья" : scoreRed > scoreBlue ? "Победа красных" : "Победа синих")
                         + $" {scoreRed}:{scoreBlue}\nR — сыграть ещё";
            }
        }

        // Страховка: мяч вылетел за пределы арены — возвращаем в центр
        Vector3 b = ball.transform.position;
        if (!resetPending && (Mathf.Abs(b.x) > L + goalDepth + 2f || Mathf.Abs(b.z) > W + 3f || b.y < -3f))
            ball.ResetTo(new Vector3(0f, ball.Radius + 0.01f, 0f));

        UpdateChasers();
    }

    // Камера: фиксированный изометрический угол, позиция плавно тянется за мячом
    void LateUpdate()
    {
        Vector3 focus = ball.transform.position;
        focus.y = 0f;
        Vector3 wanted = focus - cam.transform.forward * camDistance;
        cam.transform.position = Vector3.Lerp(cam.transform.position, wanted, 1f - Mathf.Exp(-camSmooth * Time.deltaTime));
    }

    // ---------------------------------------------------------------- голы

    /// <summary>Вызывается мячом при входе в любой триггер.</summary>
    public void OnBallTrigger(Collider c)
    {
        if (Frozen) return;
        if (c == goalLeft) { scoreBlue++; banner = "ГОЛ! Забили синие"; }
        else if (c == goalRight) { scoreRed++; banner = "ГОЛ! Забили красные"; }
        else return;
        pauseTimer = goalPause;   // таймер матча на паузе, игроки стоят
        resetPending = true;      // по окончании паузы — KickOff()
    }

    // ---------------------------------------------------------------- запросы для ИИ

    public float OwnGoalX(Team t) => t == Team.Red ? -L : L;
    public Vector3 GoalOf(Team t) => new Vector3(OwnGoalX(t), 0f, 0f);
    public bool IsChaser(Player p) => chaser[(int)p.team] == p;

    /// <summary>Позиция «держать место»: базовая точка, смещённая вслед за мячом.</summary>
    public Vector3 FormationPos(Player p)
    {
        Vector3 b = ball.transform.position;
        return ClampToField(p.homePos + new Vector3(b.x * 0.6f, 0f, b.z * 0.3f), 1f);
    }

    public Vector3 ClampToField(Vector3 v, float margin) =>
        new Vector3(Mathf.Clamp(v.x, -L + margin, L - margin), 0f, Mathf.Clamp(v.z, -W + margin, W - margin));

    public Player NearestOpponent(Player me, out float dist)
    {
        Player best = null;
        dist = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team == me.team) continue;
            float d = Vector3.Distance(p.transform.position, me.transform.position);
            if (d < dist) { dist = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// В каждой команде к мячу бежит ближайший полевой (включая человека: если ближе всех он — ИИ-партнёры держат позиции).
    /// Бонус 1.5 м текущему «охотнику», чтобы роль не мигала между двумя игроками.
    /// </summary>
    void UpdateChasers()
    {
        Vector3 b = ball.transform.position;
        b.y = 0f;
        for (int t = 0; t < 2; t++)
        {
            Player best = null;
            float bestD = float.MaxValue;
            foreach (var p in players)
            {
                if ((int)p.team != t || p.role != Role.Field) continue;
                Vector3 pp = p.transform.position;
                pp.y = 0f;
                float d = Vector3.Distance(pp, b) - (p == chaser[t] ? 1.5f : 0f);
                if (d < bestD) { bestD = d; best = p; }
            }
            chaser[t] = best;
        }
    }

    // ---------------------------------------------------------------- спавн

    void SpawnTeams()
    {
        // Расстановка красных (своя половина -X). Синие — зеркально по X.
        Vector3[] layout =
        {
            new Vector3(-L + 1f,  0f, 0f),              // 0 вратарь
            new Vector3(-L * 0.6f, 0f, -W * 0.4f),      // 1 защитник
            new Vector3(-L * 0.6f, 0f,  W * 0.4f),      // 2 защитник
            new Vector3(-L * 0.2f, 0f, -W * 0.3f),      // 3 нападающий
            new Vector3(-L * 0.2f, 0f,  W * 0.3f),      // 4 нападающий (им управляет игрок)
        };

        for (int t = 0; t < 2; t++)
            for (int i = 0; i < layout.Length; i++)
            {
                Team team = (Team)t;
                Vector3 home = layout[i];
                if (team == Team.Blue) home.x = -home.x;
                bool isHuman = team == Team.Red && i == 4;
                players.Add(CreatePlayer(team, i == 0 ? Role.Keeper : Role.Field, isHuman, home));
            }
    }

    Player CreatePlayer(Team team, Role role, bool isHuman, Vector3 home)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"{team}_{role}{(isHuman ? "_YOU" : "")}";
        Color c = team == Team.Red ? new Color(0.9f, 0.2f, 0.2f) : new Color(0.2f, 0.4f, 0.95f);
        if (role == Role.Keeper) c = Color.Lerp(c, Color.black, 0.45f);        // вратарь темнее
        Paint(go, c);

        // «Нос» показывает, куда смотрит игрок; жёлтый шар — твой игрок
        Prim(PrimitiveType.Cube, go.transform, new Vector3(0f, 0.5f, 0.45f), new Vector3(0.25f, 0.15f, 0.3f), Color.white);
        if (isHuman) Prim(PrimitiveType.Sphere, go.transform, new Vector3(0f, 1.4f, 0f), Vector3.one * 0.4f, Color.yellow);

        var p = go.AddComponent<Player>();
        p.Init(this, team, role, isHuman, home);
        if (isHuman) human = p;
        return p;
    }

    // ---------------------------------------------------------------- поле

    void BuildArena()
    {
        Transform root = new GameObject("Arena").transform;

        // Газон (с коллайдером — по нему катается мяч), верхняя грань на y = 0
        Prim(PrimitiveType.Cube, root, new Vector3(0f, -0.5f, 0f), new Vector3(length + 8f, 1f, width + 8f),
             new Color(0.2f, 0.55f, 0.25f), true);

        // Разметка — тонкие белые кубики без коллайдеров
        const float y = 0.01f, t = 0.12f, h = 0.02f;
        Line(root, new Vector3(0f, y,  W), new Vector3(length + t, h, t));   // боковые
        Line(root, new Vector3(0f, y, -W), new Vector3(length + t, h, t));
        Line(root, new Vector3( L, y, 0f), new Vector3(t, h, width));        // лицевые
        Line(root, new Vector3(-L, y, 0f), new Vector3(t, h, width));
        Line(root, new Vector3(0f, y, 0f), new Vector3(t, h, width));        // центральная

        // Центральный круг из 36 отрезков
        const int segs = 36;
        const float radius = 3f;
        for (int i = 0; i < segs; i++)
        {
            float a = i * Mathf.PI * 2f / segs;
            var seg = Line(root, new Vector3(Mathf.Cos(a) * radius, y, Mathf.Sin(a) * radius),
                           new Vector3(t, h, 2f * Mathf.PI * radius / segs + 0.02f));
            seg.rotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);   // отрезок по касательной
        }

        foreach (float s in new[] { -1f, 1f })   // s = -1 — ворота красных, +1 — синих
        {
            // Штрафная
            Line(root, new Vector3(s * (L - boxDepth), y, 0f), new Vector3(t, h, boxHalfWidth * 2f));
            Line(root, new Vector3(s * (L - boxDepth * 0.5f), y,  boxHalfWidth), new Vector3(boxDepth, h, t));
            Line(root, new Vector3(s * (L - boxDepth * 0.5f), y, -boxHalfWidth), new Vector3(boxDepth, h, t));

            // Торцевые борта от штанги до угла (+1 м, чтобы закрыть угол)
            float len = W + 1f - goalWidth * 0.5f;
            Wall(root, new Vector3(s * (L + 0.5f), 0f,  goalWidth * 0.5f + len * 0.5f), new Vector3(1f, 4f, len), true);
            Wall(root, new Vector3(s * (L + 0.5f), 0f, -goalWidth * 0.5f - len * 0.5f), new Vector3(1f, 4f, len), true);
            // Невидимая стенка над перекладиной — мяч не улетит за ворота
            Wall(root, new Vector3(s * (L + 0.5f), goalHeight, 0f), new Vector3(1f, 4f - goalHeight, goalWidth), false);

            BuildGoal(root, s);
        }

        // Боковые борта — мяч отскакивает, аутов нет (аркада)
        Wall(root, new Vector3(0f, 0f,  W + 0.5f), new Vector3(length + 2f, 4f, 1f), true);
        Wall(root, new Vector3(0f, 0f, -W - 0.5f), new Vector3(length + 2f, 4f, 1f), true);
    }

    void BuildGoal(Transform root, float s)
    {
        float gw = goalWidth * 0.5f, gh = goalHeight, gd = goalDepth;
        Color net = new Color(0.8f, 0.8f, 0.8f);

        // Штанги и перекладина — с коллайдерами, мяч от них отскакивает
        Prim(PrimitiveType.Cube, root, new Vector3(s * L, gh * 0.5f,  gw), new Vector3(0.2f, gh, 0.2f), Color.white, true);
        Prim(PrimitiveType.Cube, root, new Vector3(s * L, gh * 0.5f, -gw), new Vector3(0.2f, gh, 0.2f), Color.white, true);
        Prim(PrimitiveType.Cube, root, new Vector3(s * L, gh, 0f), new Vector3(0.2f, 0.2f, goalWidth + 0.2f), Color.white, true);

        // «Сетка»: задняя и боковые стенки + невидимая крыша (чтобы не закрывала мяч от камеры)
        Prim(PrimitiveType.Cube, root, new Vector3(s * (L + gd), gh * 0.5f, 0f), new Vector3(0.1f, gh, goalWidth), net, true);
        Prim(PrimitiveType.Cube, root, new Vector3(s * (L + gd * 0.5f), gh * 0.5f,  gw), new Vector3(gd, gh, 0.1f), net, true);
        Prim(PrimitiveType.Cube, root, new Vector3(s * (L + gd * 0.5f), gh * 0.5f, -gw), new Vector3(gd, gh, 0.1f), net, true);
        var roof = Prim(PrimitiveType.Cube, root, new Vector3(s * (L + gd * 0.5f), gh, 0f), new Vector3(gd, 0.1f, goalWidth), net, true);
        roof.GetComponent<Renderer>().enabled = false;

        // Триггер гола начинается в 0.5 м за линией: мяч (R = 0.25) касается его, только полностью пересёк линию
        float depth = gd - 0.6f;
        var trig = Prim(PrimitiveType.Cube, root, new Vector3(s * (L + 0.5f + depth * 0.5f), gh * 0.5f, 0f),
                        new Vector3(depth, gh - 0.2f, goalWidth - 0.2f), Color.clear, true);
        trig.name = s < 0 ? "GoalTrigger_Red" : "GoalTrigger_Blue";
        trig.GetComponent<Renderer>().enabled = false;
        var col = trig.GetComponent<Collider>();
        col.isTrigger = true;
        if (s < 0) goalLeft = col; else goalRight = col;
    }

    /// <summary>Борт: видимая низкая доска 0.3 м + высокий невидимый коллайдер (size.y) от высоты baseY.</summary>
    void Wall(Transform root, Vector3 basePos, Vector3 size, bool visibleBoard)
    {
        var wall = Prim(PrimitiveType.Cube, root, basePos + Vector3.up * size.y * 0.5f, size, Color.clear, true);
        wall.GetComponent<Renderer>().enabled = false;
        if (visibleBoard)
            Prim(PrimitiveType.Cube, root, basePos + Vector3.up * 0.15f, new Vector3(size.x, 0.3f, size.z), new Color(0.9f, 0.9f, 0.9f));
    }

    Transform Line(Transform root, Vector3 pos, Vector3 size) =>
        Prim(PrimitiveType.Cube, root, pos, size, Color.white).transform;

    /// <summary>Примитив с цветом; коллайдер удаляется, если он не нужен (разметка, декор).</summary>
    static GameObject Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color color, bool keepCollider = false)
    {
        var go = GameObject.CreatePrimitive(type);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
        Paint(go, color);
        if (!keepCollider) Destroy(go.GetComponent<Collider>());
        return go;
    }

    // material.color работает и во встроенном пайплайне, и в URP (_BaseColor помечен как [MainColor])
    static void Paint(GameObject go, Color c) => go.GetComponent<Renderer>().material.color = c;

    // ---------------------------------------------------------------- HUD

    void OnGUI()
    {
        var st = new GUIStyle(GUI.skin.label)
        {
            fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, richText = true
        };
        st.normal.textColor = Color.white;

        GUI.Label(new Rect(0, 10, Screen.width, 40),
            $"<color=#ff5555>КРАСНЫЕ {scoreRed}</color> : <color=#5588ff>{scoreBlue} СИНИЕ</color>     {Mathf.CeilToInt(timeLeft)} c", st);

        if (!string.IsNullOrEmpty(banner))
        {
            st.fontSize = 44;
            GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 120), banner, st);
        }

        // Шкала силы удара
        if (human != null && human.charge > 0f)
        {
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(20, Screen.height - 70, 204, 24), Texture2D.whiteTexture);
            GUI.color = Color.Lerp(Color.yellow, Color.red, human.charge);
            GUI.DrawTexture(new Rect(22, Screen.height - 68, 200 * human.charge, 20), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        GUI.Label(new Rect(20, Screen.height - 40, Screen.width, 30),
            "WASD/стрелки — бег   J — пас   K (зажать) — удар   R — рестарт");
    }
}
