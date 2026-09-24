using System.Collections.Generic;
using UnityEngine;

public enum SetPieceType { Kickoff, ThrowIn, Corner, GoalKick }

/// <summary>
/// Ядро матча: строит поле с разметкой и воротами, спавнит 10 игроков, следит за правилами
/// (гол, аут, угловой, удар от ворот, розыгрыш с центра), считает счёт и время,
/// управляет переключением твоего игрока, ведёт изометрическую камеру и рисует HUD.
/// Вешается на пустой объект "Match"; в инспекторе нужно указать Ball и Main Camera.
/// Ось X — вдоль поля: красные (ты) защищают ворота на -X и атакуют в +X, синие — наоборот.
///
/// Фазы: Play (мяч в игре, идёт время) → Stopped (пауза после гола/аута, все стоят)
///       → SetPiece (мяч на точке, исполнитель разыгрывает) → Play ... → Over.
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
    public float matchTime = 60f;       // «чистое» время: в паузах и на стандартах таймер стоит
    public float goalPause = 1.5f;
    public float outPause = 1f;

    [Header("Камера (изометрия)")]
    public Vector3 camAngles = new Vector3(45f, 45f, 0f);
    public float camSize = 11f, camDistance = 40f, camSmooth = 4f;

    [HideInInspector] public List<Player> players = new List<Player>();
    [HideInInspector] public Player controlled;      // кем ты сейчас управляешь
    [HideInInspector] public Player passReceiver;    // кому летит пас (он выходит на мяч)
    public const Team HumanTeam = Team.Red;

    enum Phase { Play, Stopped, SetPiece, Over }
    Phase phase;
    float phaseTimer, timeLeft, passTimer;
    int scoreRed, scoreBlue;
    string banner, hint;

    // текущий / следующий стандарт
    SetPieceType nextType;
    Team spTeam, nextTeam;
    Vector3 spSpot, nextSpot;
    Player taker;

    Collider goalLeft, goalRight;                    // триггеры ворот красных (-X) и синих (+X)
    readonly Player[] chaser = new Player[2];
    Transform marker, aimArrow, passRing;            // жёлтый шар над тобой, стрелка прицела, кольцо под адресатом паса

    public bool Stopped => phase == Phase.Stopped || phase == Phase.Over;
    public bool SetPieceActive => phase == Phase.SetPiece;
    public Team SetPieceTeam => spTeam;
    public Vector3 SetPieceSpot => spSpot;
    public float SetPieceTime => phaseTimer;
    public bool IsTaker(Player p) => phase == Phase.SetPiece && taker == p;
    float L => length * 0.5f;
    float W => width * 0.5f;

    void Awake() => I = this;

    // ------------------------------------------------------------ автозапуск

    /// <summary>Если в открытой сцене нет MatchManager — создаём всё сами: Play работает в любой сцене.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (FindAnyObjectByType<MatchManager>() == null) CreateSceneObjects();
    }

    /// <summary>Создаёт камеру/свет (если их нет), мяч и объект Match со ссылками. Используется и редактор-скриптом.</summary>
    public static MatchManager CreateSceneObjects()
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camera = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
        }
        if (FindAnyObjectByType<Light>() == null)
        {
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        var ballGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ballGo.name = "Ball";
        ballGo.transform.position = new Vector3(0f, 0.25f, 0f);
        ballGo.transform.localScale = Vector3.one * 0.5f;
        var newBall = ballGo.AddComponent<Ball>();   // Rigidbody добавится через RequireComponent

        var mm = new GameObject("Match").AddComponent<MatchManager>();
        mm.ball = newBall;
        mm.cam = camera;
        return mm;
    }

    // Start, а не Awake: к этому моменту Ball.Awake уже отработал
    void Start()
    {
        if (cam == null) cam = Camera.main;
        BuildArena();
        SpawnTeams();
        CreateIndicators();

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
        banner = null;
        BeginSetPiece(SetPieceType.Kickoff, HumanTeam, Vector3.zero);
    }

    // ------------------------------------------------------------ цикл

    void Update()
    {
        if (GameInput.RestartPressed()) Restart();

        switch (phase)
        {
            case Phase.Stopped:
                phaseTimer -= Time.deltaTime;
                if (phaseTimer <= 0f) BeginSetPiece(nextType, nextTeam, nextSpot);
                break;

            case Phase.SetPiece:
                phaseTimer += Time.deltaTime;
                break;

            case Phase.Play:
                timeLeft -= Time.deltaTime;
                if (timeLeft <= 0f) { EndMatch(); break; }
                CheckOut();
                break;
        }

        if (passReceiver != null && (passTimer -= Time.deltaTime) <= 0f) passReceiver = null;
        UpdateChasers();
    }

    void LateUpdate()
    {
        // Камера: фиксированный изометрический угол, позиция плавно тянется за мячом
        Vector3 focus = ball.transform.position;
        focus.y = 0f;
        Vector3 wanted = focus - cam.transform.forward * camDistance;
        cam.transform.position = Vector3.Lerp(cam.transform.position, wanted, 1f - Mathf.Exp(-camSmooth * Time.deltaTime));

        UpdateIndicators();
    }

    void EndMatch()
    {
        timeLeft = 0f;
        phase = Phase.Over;
        hint = null;
        banner = (scoreRed == scoreBlue ? "Ничья" : scoreRed > scoreBlue ? "Победа красных" : "Победа синих")
                 + $" {scoreRed}:{scoreBlue}\nR — сыграть ещё";
    }

    // ------------------------------------------------------------ правила

    /// <summary>Мяч полностью пересёк линию поля (не в створе ворот) → аут / угловой / от ворот.</summary>
    void CheckOut()
    {
        if (ball.Held) return;
        Vector3 b = ball.Body.position;
        float r = ball.Radius;
        Team last = ball.lastTouch != null ? ball.lastTouch.team : HumanTeam;

        // Боковая линия → аут (вводит команда, которая мяч НЕ выбивала)
        if (Mathf.Abs(b.z) > W + r)
        {
            Vector3 spot = new Vector3(Mathf.Clamp(b.x, -L + 1f, L - 1f), 0f, Mathf.Sign(b.z) * W);
            StopForSetPiece(SetPieceType.ThrowIn, Player.Opp(last), spot, outPause, "Аут");
            return;
        }

        // Лицевая линия
        if (Mathf.Abs(b.x) > L + r)
        {
            bool inGoalMouth = Mathf.Abs(b.z) < goalWidth * 0.5f && b.y < goalHeight;
            if (inGoalMouth) return;                         // летит в ворота — решит триггер гола

            float side = Mathf.Sign(b.x);
            Team defending = side < 0f ? Team.Red : Team.Blue;
            if (last == defending)                           // защитники выбили за свою линию → угловой
                StopForSetPiece(SetPieceType.Corner, Player.Opp(defending),
                                new Vector3(side * (L - 0.3f), 0f, Mathf.Sign(b.z) * (W - 0.3f)), outPause, "Угловой");
            else                                             // атакующие → удар от ворот
                StopForSetPiece(SetPieceType.GoalKick, defending,
                                new Vector3(side * (L - 1.5f), 0f, 0f), outPause, "От ворот");
        }
    }

    /// <summary>Вызывается мячом при входе в любой триггер.</summary>
    public void OnBallTrigger(Collider c)
    {
        if (phase != Phase.Play) return;
        Team conceded;
        if (c == goalLeft) { scoreBlue++; conceded = Team.Red; banner = "ГОЛ! Забили синие"; }
        else if (c == goalRight) { scoreRed++; conceded = Team.Blue; banner = "ГОЛ! Забили красные"; }
        else return;
        StopForSetPiece(SetPieceType.Kickoff, conceded, Vector3.zero, goalPause, null);  // с центра начинают пропустившие
    }

    /// <summary>Остановка игры: все замирают, через pause секунд — розыгрыш стандарта.</summary>
    void StopForSetPiece(SetPieceType type, Team team, Vector3 spot, float pause, string title)
    {
        phase = Phase.Stopped;
        phaseTimer = pause;
        nextType = type; nextTeam = team; nextSpot = spot;
        passReceiver = null;
        if (title != null) banner = $"{title}: {(team == Team.Red ? "красные" : "синие")}";
    }

    void BeginSetPiece(SetPieceType type, Team team, Vector3 spot)
    {
        phase = Phase.SetPiece;
        phaseTimer = 0f;
        spTeam = team; spSpot = spot;
        banner = null;
        passReceiver = null;

        if (type == SetPieceType.Kickoff)
            foreach (var p in players) p.ResetTo(p.homePos, p.team == Team.Red ? Vector3.right : Vector3.left);

        ball.Hold(spot);

        // Направление «в поле» от точки стандарта
        Vector3 attack = team == Team.Red ? Vector3.right : Vector3.left;
        Vector3 inField = type == SetPieceType.ThrowIn ? new Vector3(0f, 0f, -Mathf.Sign(spot.z))
                        : type == SetPieceType.Corner ? (-spot).normalized
                        : attack;

        // Исполнитель: на ударе от ворот — вратарь, иначе ближайший полевой. Ставим его за мяч.
        taker = type == SetPieceType.GoalKick ? Keeper(team) : NearestFieldPlayer(team, spot);
        taker.ResetTo(spot - inField * 0.9f, inField);

        // Соперников, стоящих слишком близко, отодвигаем на 4 м (на центре — за круг)
        foreach (var p in players)
        {
            if (p.team == team || p.role == Role.Keeper) continue;
            Vector3 d = p.Position - spot;
            if (d.magnitude < 4f)
                p.ResetTo(ClampToField(spot + (d.sqrMagnitude < 0.01f ? -inField : d.normalized) * 4f, 0.5f), -d);
        }

        // Твоя команда разыгрывает — управление переходит к исполнителю
        if (team == HumanTeam && taker.role == Role.Field) controlled = taker;
        hint = team == HumanTeam && taker == controlled
            ? "Стандарт: WASD — направление, J — пас, K — удар"
            : null;
    }

    // ------------------------------------------------------------ события от игроков и мяча

    public void OnKick(Player kicker, Player receiver)
    {
        if (phase == Phase.SetPiece) { phase = Phase.Play; hint = null; }   // стандарт разыгран — время пошло
        passReceiver = receiver;
        passTimer = 2.5f;
        // Твоя команда отдала пас — управление сразу переходит к адресату
        if (receiver != null && receiver.team == HumanTeam && receiver.role == Role.Field) controlled = receiver;
    }

    public void OnPossession(Player owner)
    {
        passReceiver = null;
        // Мяч у полевого твоей команды — управляешь им
        if (owner.team == HumanTeam && owner.role == Role.Field) controlled = owner;
    }

    /// <summary>Q: переключиться на игрока своей команды, ближайшего к мячу (кроме текущего).</summary>
    public void SwitchControl()
    {
        if (IsTaker(controlled)) return;
        Player best = null;
        float bestD = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team != HumanTeam || p.role != Role.Field || p == controlled) continue;
            float d = Vector3.Distance(p.Position, ball.transform.position);
            if (d < bestD) { bestD = d; best = p; }
        }
        if (best != null) controlled = best;
    }

    // ------------------------------------------------------------ запросы для ИИ

    public float OwnGoalX(Team t) => t == Team.Red ? -L : L;
    public Vector3 GoalOf(Team t) => new Vector3(OwnGoalX(t), 0f, 0f);
    public bool IsChaser(Player p) => chaser[(int)p.team] == p;

    /// <summary>Позиция «держать место»: базовая точка, смещённая за мячом; в атаке — выше по полю.</summary>
    public Vector3 FormationPos(Player p, bool attacking)
    {
        Vector3 b = ball.transform.position;
        float forward = (p.team == Team.Red ? 1f : -1f) * (attacking ? 5f : -2f);
        return ClampToField(p.homePos + new Vector3(b.x * 0.55f + forward, 0f, b.z * 0.35f), 1f);
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
            float d = Vector3.Distance(p.Position, me.Position);
            if (d < dist) { dist = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// Лучший адресат паса: партнёр в секторе prefDir ± maxAngle. Чем ближе к направлению прицела и чем
    /// свободнее линия паса (нет соперников рядом с отрезком), тем лучше. Вратарь — только если больше некому.
    /// </summary>
    public Player FindPassTarget(Player passer, Vector3 prefDir, float maxAngle)
    {
        Vector3 from = passer.Position;
        prefDir.y = 0f;
        Player best = null;
        float bestScore = float.MinValue;
        foreach (var mate in players)
        {
            if (mate == passer || mate.team != passer.team) continue;
            Vector3 to = mate.Position - from;
            float d = to.magnitude;
            if (d < 2.5f || d > 28f) continue;
            float angle = Vector3.Angle(prefDir, to);
            if (angle > maxAngle) continue;

            float score = -angle * 1.2f - d * 0.4f;
            if (mate.role == Role.Keeper) score -= 60f;
            foreach (var opp in players)
            {
                if (opp.team == passer.team) continue;
                float lane = DistanceToSegment(opp.Position, from, mate.Position);
                if (lane < 1.5f) score -= (1.5f - lane) * 25f;   // соперник рядом с линией паса — перехватит
            }
            if (score > bestScore) { bestScore = score; best = mate; }
        }
        return best;
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f));
        return Vector3.Distance(p, a + ab * t);
    }

    Player Keeper(Team t) => players.Find(p => p.team == t && p.role == Role.Keeper);

    Player NearestFieldPlayer(Team t, Vector3 spot)
    {
        Player best = null;
        float bestD = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team != t || p.role != Role.Field) continue;
            float d = Vector3.Distance(p.Position, spot);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// В каждой команде к мячу бежит ближайший полевой. В твоей команде ты тоже «кандидат»: если ближе всех ты,
    /// ИИ-партнёры держат позиции. Бонус 1.5 м текущему «охотнику», чтобы роль не мигала.
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
                float d = Vector3.Distance(p.Position, b) - (p == chaser[t] ? 1.5f : 0f);
                if (d < bestD) { bestD = d; best = p; }
            }
            chaser[t] = best;
        }
    }

    // ------------------------------------------------------------ спавн

    void SpawnTeams()
    {
        // Расстановка красных (своя половина -X). Синие — зеркально по X.
        Vector3[] layout =
        {
            new Vector3(-L + 1f,  0f, 0f),              // 0 вратарь
            new Vector3(-L * 0.6f, 0f, -W * 0.4f),      // 1 защитник
            new Vector3(-L * 0.6f, 0f,  W * 0.4f),      // 2 защитник
            new Vector3(-L * 0.2f, 0f, -W * 0.3f),      // 3 нападающий
            new Vector3(-L * 0.2f, 0f,  W * 0.3f),      // 4 нападающий
        };

        for (int t = 0; t < 2; t++)
            for (int i = 0; i < layout.Length; i++)
            {
                Team team = (Team)t;
                Vector3 home = layout[i];
                if (team == Team.Blue) home.x = -home.x;
                players.Add(CreatePlayer(team, i == 0 ? Role.Keeper : Role.Field, home));
            }
        controlled = players[4];
    }

    Player CreatePlayer(Team team, Role role, Vector3 home)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"{team}_{role}_{players.Count}";
        Color c = team == Team.Red ? new Color(0.9f, 0.2f, 0.2f) : new Color(0.2f, 0.4f, 0.95f);
        if (role == Role.Keeper) c = Color.Lerp(c, Color.black, 0.45f);        // вратарь темнее
        Paint(go, c);
        // «Нос» показывает, куда смотрит игрок
        Prim(PrimitiveType.Cube, go.transform, new Vector3(0f, 0.5f, 0.45f), new Vector3(0.25f, 0.15f, 0.3f), Color.white);

        var p = go.AddComponent<Player>();
        p.Init(this, team, role, home);
        // Мяч не сталкивается с капсулами физически — касания считает Ball (контроль, блоки)
        Physics.IgnoreCollision(ball.Col, go.GetComponent<Collider>());
        return p;
    }

    void CreateIndicators()
    {
        marker = Prim(PrimitiveType.Sphere, null, Vector3.zero, Vector3.one * 0.4f, Color.yellow).transform;
        aimArrow = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(0.12f, 0.02f, 1.2f), Color.yellow).transform;
        passRing = Prim(PrimitiveType.Cylinder, null, Vector3.zero, new Vector3(1.3f, 0.01f, 1.3f), new Color(1f, 0.9f, 0.2f)).transform;
    }

    /// <summary>Жёлтый шар над тобой, стрелка прицела под ногами и кольцо под тем, кому уйдёт пас по J.</summary>
    void UpdateIndicators()
    {
        if (controlled == null) return;
        Vector3 pos = controlled.Position;
        marker.position = pos + Vector3.up * 2.4f;
        aimArrow.position = pos + controlled.Aim * 1.4f + Vector3.up * 0.03f;
        aimArrow.rotation = Quaternion.LookRotation(controlled.Aim);

        bool canPass = controlled.HasBall || IsTaker(controlled);
        Player target = canPass ? FindPassTarget(controlled, controlled.Aim, 70f) : null;
        passRing.gameObject.SetActive(target != null);
        if (target != null) passRing.position = target.Position + Vector3.up * 0.02f;
    }

    // ------------------------------------------------------------ поле

    void BuildArena()
    {
        Transform root = new GameObject("Arena").transform;

        // Газон с запасом за линиями (зона аутов), верхняя грань на y = 0
        Prim(PrimitiveType.Cube, root, new Vector3(0f, -0.5f, 0f), new Vector3(length + 10f, 1f, width + 10f),
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
            BuildGoal(root, s);
        }

        // Невидимое ограждение по краю газона: мяч и игроки не улетают с поля (бортов у кромки нет — есть ауты)
        float fx = L + 5f, fz = W + 5f;
        Fence(root, new Vector3(0f, 2f,  fz), new Vector3(length + 12f, 4f, 1f));
        Fence(root, new Vector3(0f, 2f, -fz), new Vector3(length + 12f, 4f, 1f));
        Fence(root, new Vector3( fx, 2f, 0f), new Vector3(1f, 4f, width + 12f));
        Fence(root, new Vector3(-fx, 2f, 0f), new Vector3(1f, 4f, width + 12f));
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

    static void Fence(Transform root, Vector3 center, Vector3 size) =>
        Prim(PrimitiveType.Cube, root, center, size, Color.clear, true).GetComponent<Renderer>().enabled = false;

    Transform Line(Transform root, Vector3 pos, Vector3 size) =>
        Prim(PrimitiveType.Cube, root, pos, size, Color.white).transform;

    /// <summary>Примитив с цветом; коллайдер удаляется, если он не нужен (разметка, индикаторы).</summary>
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

    // ------------------------------------------------------------ HUD

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
            GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 120), banner, st);
        }
        if (!string.IsNullOrEmpty(hint))
        {
            st.fontSize = 22;
            GUI.Label(new Rect(0, 50, Screen.width, 30), hint, st);
        }

        // Шкала силы удара
        if (controlled != null && controlled.charge > 0f)
        {
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(20, Screen.height - 70, 204, 24), Texture2D.whiteTexture);
            GUI.color = Color.Lerp(Color.yellow, Color.red, controlled.charge);
            GUI.DrawTexture(new Rect(22, Screen.height - 68, 200 * controlled.charge, 20), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        GUI.Label(new Rect(20, Screen.height - 40, Screen.width, 30),
            "WASD — бег   Shift — спринт   J — пас (кольцо = адресат)   K (зажать) — удар   Q — смена игрока   R — рестарт");
    }
}
