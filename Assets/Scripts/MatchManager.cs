using System.Collections.Generic;
using UnityEngine;

public enum SetPieceType { Kickoff, ThrowIn, Corner, GoalKick, FreeKick, Penalty }

/// <summary>Итог матча для экрана результата в меню.</summary>
public class MatchResult
{
    public string title, score;
    public bool good;
    public List<string> lines = new List<string>();
    public MatchStats stats;                          // null на тренировке
}

/// <summary>
/// Ядро игры: поле, игроки, правила (гол, аут, угловой, от ворот, фол → штрафной/пенальти), счёт и время,
/// смена игрока (ручная и автоматическая), опека и стенка, «умная» камера, пауза, награды и возврат в меню.
/// HUD матча (табло, радар, выносливость) рисуется здесь, меню и пауза — в MainMenu.
/// Ось X — вдоль поля: твоя команда защищает ворота на -X и атакует в +X.
///
/// Фазы матча: Play (мяч в игре) → Stopped (пауза после гола/аута) → SetPiece (стандарт) → Play ... → Over.
/// </summary>
public class MatchManager : MonoBehaviour
{
    public static MatchManager I { get; private set; }

    [Header("Ссылки на объекты сцены")]
    public Ball ball;
    public Camera cam;

    // --- Размеры поля, м
    [System.NonSerialized] public float length = 40f, width = 24f;
    [System.NonSerialized] public float goalWidth = 6f, goalHeight = 2f, goalDepth = 2f;
    [System.NonSerialized] public float boxDepth = 5f, boxHalfWidth = 5f;   // штрафная

    // --- Паузы
    [System.NonSerialized] public float goalPause = 1.5f;
    [System.NonSerialized] public float outPause = 1f;
    [System.NonSerialized] public float resultScreenTime = 4f;

    // --- Камера сбоку (трансляция)
    [System.NonSerialized] public float sidePitch = 50f, sideFov = 40f, sideDistance = 30f;
    // --- Умная камера
    [System.NonSerialized] public float smartDistance = 24f;     // базовая дистанция (ближе широкой трансляции)
    [System.NonSerialized] public float smartZoomOut = 0.6f;     // на сколько отъезжать за каждый метр разрыва «игрок — мяч» сверх 5 м
    [System.NonSerialized] public float lookAhead = 0.3f;        // упреждение по скорости мяча, с
    [System.NonSerialized] public float camDamp = 0.35f;         // сглаживание (SmoothDamp), с
    // --- Камера изометрия
    [System.NonSerialized] public Vector3 isoAngles = new Vector3(45f, 45f, 0f);
    [System.NonSerialized] public float isoSize = 11f, isoDistance = 40f;
    [System.NonSerialized] public float camSmooth = 4f;

    [HideInInspector] public List<Player> players = new List<Player>();
    [HideInInspector] public Player controlled;      // кем ты сейчас управляешь
    [HideInInspector] public Player passReceiver;    // кому летит пас (он выходит на мяч)
    public const Team HumanTeam = Team.Red;

    public MatchResult LastResult { get; set; }       // показывается в меню после матча
    public MatchStats Stats { get; private set; } = new MatchStats();   // статистика текущего матча
    public bool IsPractice => practiceIndex >= 0;
    public bool InMenu => app == AppState.Menu;
    public bool Paused => paused;
    public bool InMatch => app == AppState.Match;

    enum AppState { Menu, Match }
    enum Phase { Play, Stopped, SetPiece, Over }
    AppState app = AppState.Menu;
    Phase phase;
    bool paused;
    float phaseTimer, timeLeft, passTimer, resultTimer;
    float keeperRushUntil, teammatePressUntil;
    int scoreRed, scoreBlue;
    string banner, bannerSub;
    int practiceIndex = -1;
    float matchDuration;

    // Для статистики: чей пас в пути, чей удар летит, кто отдал последний точный пас (голевая передача)
    Player pendingPass, shotBy, assistFrom, assistTo;
    float shotTime;
    bool shotOnTarget, shotSaved;
    Team possTeam = HumanTeam;

    SetPieceType spType, nextType;
    Team spTeam, nextTeam;
    Vector3 spSpot, nextSpot;
    Player taker, pressHelper;

    Collider goalLeft, goalRight;                    // триггеры ворот твоей команды (-X) и соперника (+X)
    Player[] chaser = new Player[2];
    Transform marker, nextSwitch;                    // индикаторы: над тобой и над тем, на кого переключит LB

    Player runner, celebrant;                        // кто забегает по LB; кто забил (камера на него)
    float runUntil, autoSwitchAt, markTimer, flashTimer, shakeTime, shakeAmp, manualSwitchUntil, looseSwitchCooldown;
    string flashText;
    Vector3 camVel;
    Dictionary<Player, Player> marks = new Dictionary<Player, Player>();   // защитник → опекаемый
    Dictionary<Player, Vector3> wallSpots = new Dictionary<Player, Vector3>();

    public bool Stopped => app == AppState.Menu || paused || phase == Phase.Stopped || phase == Phase.Over;
    public bool SetPieceActive => phase == Phase.SetPiece;
    public SetPieceType SetPieceKind => spType;
    public Team SetPieceTeam => spTeam;
    public Vector3 SetPieceSpot => spSpot;
    public float SetPieceTime => phaseTimer;
    public bool IsTaker(Player p) => phase == Phase.SetPiece && taker == p;
    public bool KeeperRush => Time.time < keeperRushUntil;
    public bool IsPressHelper(Player p) => Time.time < teammatePressUntil && pressHelper == p;
    public float AiTackleChance => new[] { 0.2f, 0.35f, 0.5f }[Profile.Current.difficulty];
    public bool IsRunner(Player p) => p == runner && Time.time < runUntil;
    public string FlashText => flashTimer > 0f ? flashText : null;
    float L => length * 0.5f;
    float W => width * 0.5f;

    void Awake()
    {
        I = this;
        Time.fixedDeltaTime = 0.01f;   // физика 100 раз в секунду: мяч у ноги без рывков, точнее удары и отскоки
    }

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
        ballGo.transform.position = new Vector3(0f, 0.14f, 0f);
        ballGo.transform.localScale = Vector3.one * 0.28f;      // точный размер задаёт Ball.diameter
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
        if (GetComponent<MainMenu>() == null) gameObject.AddComponent<MainMenu>();
        if (GetComponent<GameAudio>() == null) gameObject.AddComponent<GameAudio>();
        BuildArena();
        CreateIndicators();
        EnterMenu();
    }

    // ------------------------------------------------------------ меню ↔ матч

    /// <summary>Главное меню: на фоне стоят команды, камера облетает твоего игрока.</summary>
    public void EnterMenu()
    {
        Time.timeScale = 1f;
        paused = false;
        app = AppState.Menu;
        banner = null;
        SpawnTeams(MatchMode.Normal);
        ball.Hold(Vector3.zero);
        ApplyCameraProjection();
    }

    /// <summary>Старт матча или тренировки (practice — индекс в Catalog.Practices, -1 — обычный матч).</summary>
    public void StartMatch(MatchMode m, int practice = -1)
    {
        Time.timeScale = 1f;
        paused = false;
        practiceIndex = practice;
        app = AppState.Match;
        SpawnTeams(m);
        ApplyCameraProjection();
        RestartMatch();
    }

    public void RestartMatch()
    {
        Time.timeScale = 1f;
        paused = false;
        scoreRed = scoreBlue = 0;
        timeLeft = practiceIndex >= 0 ? Catalog.Practices[practiceIndex].time
                                      : Catalog.MatchLengths[Profile.Current.matchLength];
        matchDuration = timeLeft;
        Stats = new MatchStats();
        pendingPass = shotBy = assistFrom = assistTo = null;
        possTeam = HumanTeam;
        banner = null;
        LastResult = null;
        BeginSetPiece(SetPieceType.Kickoff, HumanTeam, Vector3.zero);
    }

    public void Pause()
    {
        if (!InMatch || phase == Phase.Over) return;
        paused = true;
        Time.timeScale = 0f;
    }

    public void Resume()
    {
        paused = false;
        Time.timeScale = 1f;
    }

    public void QuitToMenu() => EnterMenu();

    /// <summary>Перекрасить команды и мяч после покупок/смены формы в меню.</summary>
    public void RefreshLook()
    {
        if (InMenu) SpawnTeams(MatchMode.Normal);
        Paint(ball.gameObject, Catalog.Balls[Profile.Current.ballSkin].color);
    }

    // ------------------------------------------------------------ цикл

    /// <summary>После перекомпиляции во время Play несериализуемые поля могут обнулиться — восстанавливаем.</summary>
    void EnsureState()
    {
        if (chaser == null) chaser = new Player[2];
        if (marks == null) marks = new Dictionary<Player, Player>();
        if (wallSpots == null) wallSpots = new Dictionary<Player, Vector3>();
        if (players == null) players = new List<Player>();
    }

    void Update()
    {
        EnsureState();
        // Start / Esc — пауза. «Назад» (B) на паузе обрабатывает меню.
        if (InMatch && phase != Phase.Over && GameInput.Down(Btn.Pause))
        {
            if (paused) Resume(); else Pause();
        }
        if (InMenu || paused) return;

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
                if (ball.Owner != null) possTeam = ball.Owner.team;       // ничей мяч — владение у последней владевшей команды
                Stats.possession[(int)possTeam] += Time.deltaTime;
                CheckOut();
                break;

            case Phase.Over:
                resultTimer -= Time.unscaledDeltaTime;
                if (resultTimer <= 0f || GameInput.Down(Btn.Confirm)) QuitToMenu();
                break;
        }

        if (passReceiver != null && (passTimer -= Time.deltaTime) <= 0f) passReceiver = null;
        if (autoSwitchAt > 0f && Time.time >= autoSwitchAt) { autoSwitchAt = 0f; DoAutoSwitch(); }
        HandleSwitchInput();
        LooseBallAutoSelect();
        // Вратарём управляешь, пока мяч у него или идёт к нему; отдал — управление переходит к полевому
        if (controlled != null && controlled.role == Role.Keeper && !controlled.HasBall && !IsTaker(controlled)
            && !BallComingToKeeper(controlled))
        {
            Player best = BestInterceptor(out _);
            if (best != null) controlled = best;
        }
        flashTimer -= Time.deltaTime;
        UpdateChasers();
        UpdateMarking();
    }

    void LateUpdate()
    {
        EnsureState();
        UpdateCamera();
        UpdateIndicators();
    }

    // ------------------------------------------------------------ камера

    void ApplyCameraProjection()
    {
        bool iso = InMatch && Profile.Current.cameraMode == 2;
        cam.orthographic = iso;
        if (iso) { cam.orthographicSize = isoSize; cam.transform.rotation = Quaternion.Euler(isoAngles); }
        else cam.fieldOfView = InMenu ? 35f : sideFov;
    }

    public void Shake(float amp)
    {
        shakeAmp = Mathf.Max(shakeAmp, amp);
        shakeTime = 0.25f;
    }

    /// <summary>
    /// Камеры:
    ///  0 — «умная трансляция»: держит в кадре мяч и твоего игрока (фокус между ними, ближе к мячу),
    ///      смотрит вперёд по ходу мяча, отъезжает, когда игрок и мяч расходятся, на стандартах показывает
    ///      точку и ворота, после гола — забившего;
    ///  1 — широкая трансляция: только мяч, дальше;
    ///  2 — изометрия.
    /// </summary>
    void UpdateCamera()
    {
        float dt = Time.unscaledDeltaTime;
        float k = 1f - Mathf.Exp(-camSmooth * dt);

        if (InMenu)
        {
            // Облёт твоего игрока: камера качается перед ним
            Vector3 target = controlled != null ? controlled.Position + Vector3.up * 1.1f : Vector3.up;
            float a = (15f + Mathf.Sin(Time.unscaledTime * 0.25f) * 25f) * Mathf.Deg2Rad;
            Vector3 pos = target + new Vector3(Mathf.Cos(a) * 5.5f, 0.9f, Mathf.Sin(a) * 5.5f);
            cam.transform.position = Vector3.Lerp(cam.transform.position, pos, k);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, Quaternion.LookRotation(target - pos), k);
            return;
        }

        Vector3 b = ball.transform.position;
        b.y = 0f;
        int mode = Profile.Current.cameraMode;
        Vector3 focus;
        float dist;
        Quaternion rot;

        if (mode == 2)
        {
            focus = b;
            dist = isoDistance;
            rot = Quaternion.Euler(isoAngles);
        }
        else
        {
            rot = Quaternion.Euler(sidePitch, 0f, 0f);
            if (mode == 1) { focus = b; dist = sideDistance + 3f; }
            else
            {
                Vector3 me = controlled != null ? controlled.Position : b;
                float sep = Vector3.Distance(me, b);
                focus = Vector3.Lerp(me, b, sep > 18f ? 0.85f : 0.65f);
                focus += Vector3.ClampMagnitude(new Vector3(ball.Body.linearVelocity.x, 0f, ball.Body.linearVelocity.z) * lookAhead, 5f);
                dist = smartDistance + Mathf.Clamp(sep - 5f, 0f, 14f) * smartZoomOut
                     + Mathf.Clamp01((ball.Body.linearVelocity.magnitude - 12f) / 15f) * 3f;

                if (phase == Phase.SetPiece)
                {
                    focus = spType == SetPieceType.Kickoff ? spSpot : Vector3.Lerp(spSpot, GoalOf(Player.Opp(spTeam)), 0.35f);
                    dist = smartDistance + 2f;
                }
                else if (phase == Phase.Stopped && celebrant != null)
                {
                    focus = celebrant.Position;                  // гол: камера наезжает на забившего
                    dist = 16f;
                }
            }
            focus.x = Mathf.Clamp(focus.x, -(L - 12f), L - 12f);
            focus.z = Mathf.Clamp(focus.z, -W * 0.45f, W * 0.45f) * 0.7f;
        }

        Vector3 wanted = focus - (rot * Vector3.forward) * dist;
        Vector3 pos2 = Vector3.SmoothDamp(cam.transform.position - ShakeOffset(0f), wanted, ref camVel, camDamp, Mathf.Infinity, dt);
        shakeTime -= dt;
        cam.transform.position = pos2 + ShakeOffset(dt);
        cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, rot, k);
        if (!cam.orthographic) cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, sideFov, 1f - Mathf.Exp(-3f * dt));
    }

    Vector3 lastShake;
    /// <summary>Встряска (удар в штангу, сильный удар, сейв). dt = 0 — вернуть прошлое смещение.</summary>
    Vector3 ShakeOffset(float dt)
    {
        if (dt <= 0f) return lastShake;
        lastShake = shakeTime > 0f ? Random.insideUnitSphere * shakeAmp * (shakeTime / 0.25f) : Vector3.zero;
        if (shakeTime <= 0f) shakeAmp = 0f;
        return lastShake;
    }

    /// <summary>Стик/WASD → направление на поле относительно камеры (вверх по экрану = от камеры).</summary>
    public Vector3 CameraRelative(Vector2 input)
    {
        Vector3 f = cam.transform.forward; f.y = 0f; f.Normalize();
        Vector3 r = cam.transform.right; r.y = 0f; r.Normalize();
        return Vector3.ClampMagnitude(f * input.y + r * input.x, 1f);
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
            {
                if (shotBy != null && shotBy.team != defending && Time.time - shotTime < 3f) GameAudio.CrowdOh();   // удар мимо
                StopForSetPiece(SetPieceType.GoalKick, defending,
                                new Vector3(side * (L - 1.5f), 0f, 0f), outPause, "От ворот");
            }
        }
    }

    /// <summary>Вызывается мячом при входе в любой триггер.</summary>
    public void OnBallTrigger(Collider c)
    {
        if (!InMatch || phase != Phase.Play) return;
        Team conceded;
        if (c == goalLeft) { scoreBlue++; conceded = Team.Red; }
        else if (c == goalRight) { scoreRed++; conceded = Team.Blue; }
        else return;
        GoalEvent g = RecordGoal(Player.Opp(conceded));
        banner = g.own ? "ГОЛ! Автогол" : "ГОЛ! " + g.scorer;
        bannerSub = (g.own ? g.scorer + " · " : g.assist != null ? "Пас: " + g.assist + " · " : "") + TeamName(g.team);
        celebrant = ball.lastTouch;
        GameAudio.Net();
        GameAudio.CrowdRoar(g.team == HumanTeam);
        Shake(0.2f);

        // Тренировка: набрал нужное число голов — сразу итог
        if (practiceIndex >= 0 && scoreRed >= Catalog.Practices[practiceIndex].goalsNeeded) { EndMatch(); return; }

        // С центра начинают пропустившие (на тренировке — всегда ты)
        StopForSetPiece(SetPieceType.Kickoff, practiceIndex >= 0 ? HumanTeam : conceded, Vector3.zero, goalPause, null);
    }

    /// <summary>Остановка игры: все замирают, через pause секунд — розыгрыш стандарта.</summary>
    void StopForSetPiece(SetPieceType type, Team team, Vector3 spot, float pause, string title)
    {
        phase = Phase.Stopped;
        pendingPass = shotBy = assistFrom = assistTo = null;   // мяч вне игры — пас не дошёл, цепочка паса и удара обрывается
        if (type == SetPieceType.Corner) Stats.corners[(int)team]++;
        phaseTimer = pause;
        nextType = type; nextTeam = team; nextSpot = spot;
        passReceiver = null;
        if (title != null) { banner = $"{title}: {TeamName(team)}"; bannerSub = null; }
    }

    public static string SetPieceName(SetPieceType t)
    {
        switch (t)
        {
            case SetPieceType.Kickoff: return "Розыгрыш с центра";
            case SetPieceType.ThrowIn: return "Аут";
            case SetPieceType.Corner: return "Угловой";
            case SetPieceType.GoalKick: return "От ворот";
            case SetPieceType.FreeKick: return "Штрафной";
            default: return "Пенальти";
        }
    }

    void BeginSetPiece(SetPieceType type, Team team, Vector3 spot)
    {
        phase = Phase.SetPiece;
        phaseTimer = 0f;
        banner = bannerSub = null;
        if (type == SetPieceType.Kickoff) GameAudio.Whistle(0);
        passReceiver = null;
        celebrant = null;
        runner = null;
        wallSpots.Clear();

        if (type == SetPieceType.Kickoff)
            foreach (var p in players) p.ResetTo(p.homePos, p.team == Team.Red ? Vector3.right : Vector3.left);

        // Исполнитель: на ударе от ворот — вратарь, иначе ближайший полевой. Если у команды никого нет (тренировка) — разыгрываешь ты.
        taker = FindTaker(team, type, spot);
        if (taker == null || (type != SetPieceType.GoalKick && taker.role == Role.Keeper))
        {
            // У соперника на тренировке нет полевых — аут/угловой разыгрываешь ты
            team = HumanTeam;
            Player field = NearestFieldPlayer(team, spot);
            taker = field != null ? field : Keeper(team);
        }
        spType = type; spTeam = team; spSpot = spot;

        ball.Hold(spot);

        // Направление «в поле» от точки стандарта
        Vector3 attack = team == Team.Red ? Vector3.right : Vector3.left;
        Vector3 toGoal = (GoalOf(Player.Opp(team)) - spot).normalized;
        Vector3 inField = type == SetPieceType.ThrowIn ? new Vector3(0f, 0f, -Mathf.Sign(spot.z))
                        : type == SetPieceType.Corner ? (-spot).normalized
                        : type == SetPieceType.FreeKick || type == SetPieceType.Penalty ? toGoal
                        : attack;
        taker.ResetTo(spot - inField * 0.9f, inField);

        Team defending = Player.Opp(team);
        if (type == SetPieceType.Penalty)
        {
            // Пенальти: вратарь — в центр ворот, остальные — за пределы штрафной
            Player k = Keeper(defending);
            if (k != null) k.ResetTo(GoalOf(defending) + new Vector3(-Mathf.Sign(OwnGoalX(defending)) * 0.3f, 0f, 0f), toGoal * -1f);
            foreach (var p in players)
                if (p != taker && p.role == Role.Field) p.ResetTo(KeepOutOfPenaltyBox(p.Position), p.Facing);
        }
        else if (type == SetPieceType.FreeKick && Vector3.Distance(spot, GoalOf(defending)) < 18f)
        {
            // Штрафной у ворот — стенка из двух ближайших защитников в 4 м от мяча на линии удара
            Vector3 perp = Vector3.Cross(Vector3.up, toGoal);
            int n = 0;
            foreach (var p in SortedByDistance(defending, spot))
            {
                if (p.role != Role.Field || n >= 2) continue;
                Vector3 w = spot + toGoal * 4f + perp * (n == 0 ? -0.55f : 0.55f);
                wallSpots[p] = w;
                p.ResetTo(w, -toGoal);
                n++;
            }
        }

        // Соперников, стоящих слишком близко, отодвигаем на 4 м (на центре — за круг)
        foreach (var p in players)
        {
            if (p.team == team || p.role == Role.Keeper) continue;
            Vector3 d = p.Position - spot;
            if (d.magnitude < 4f)
                p.ResetTo(ClampToField(spot + (d.sqrMagnitude < 0.01f ? -inField : d.normalized) * 4f, 0.5f), -d);
        }

        // Твоя команда разыгрывает — управление переходит к исполнителю (на ударе от ворот — к вратарю)
        if (team == HumanTeam) controlled = taker;
    }

    Player FindTaker(Team team, SetPieceType type, Vector3 spot)
    {
        Player keeper = Keeper(team), field = NearestFieldPlayer(team, spot);
        return type == SetPieceType.GoalKick ? (keeper != null ? keeper : field) : (field != null ? field : keeper);
    }

    void EndMatch()
    {
        timeLeft = Mathf.Max(timeLeft, 0f);
        phase = Phase.Over;
        resultTimer = resultScreenTime;
        passReceiver = null;
        LastResult = GiveRewards();
        banner = LastResult.title + "  " + LastResult.score;
        bannerSub = null;
        GameAudio.Whistle(2);
    }

    /// <summary>Награды и статистика. Всё сохраняется в профиль.</summary>
    MatchResult GiveRewards()
    {
        var p = Profile.Current;
        var r = new MatchResult { score = $"{scoreRed} : {scoreBlue}", stats = practiceIndex >= 0 ? null : Stats };

        if (practiceIndex >= 0)
        {
            var pr = Catalog.Practices[practiceIndex];
            bool success = scoreRed >= pr.goalsNeeded;
            r.good = success;
            r.title = success ? "ТРЕНИРОВКА ПРОЙДЕНА" : "ТРЕНИРОВКА НЕ ПРОЙДЕНА";
            r.lines.Add($"{pr.title}: забито {scoreRed} из {pr.goalsNeeded}");
            if (success && !p.practiceDone[practiceIndex])
            {
                p.practiceDone[practiceIndex] = true;
                p.coins += pr.reward;
                p.AddChallenge(ChallengeKind.PracticeDone, 1);
                r.lines.Add($"+{pr.reward} монет за первое прохождение");
            }
            else if (success) { p.coins += 50; r.lines.Add("+50 монет"); }
        }
        else
        {
            bool win = scoreRed > scoreBlue, draw = scoreRed == scoreBlue;
            r.good = win || draw;
            r.title = win ? "ПОБЕДА" : draw ? "НИЧЬЯ" : "ПОРАЖЕНИЕ";
            int reward = (win ? 500 : draw ? 250 : 100) + scoreRed * 50;
            p.coins += reward;
            p.matches++;
            if (win) p.wins++; else if (draw) p.draws++; else p.losses++;
            p.goalsFor += scoreRed;
            p.goalsAgainst += scoreBlue;
            p.AddChallenge(ChallengeKind.PlayMatches, 1);
            p.AddChallenge(ChallengeKind.ScoreGoals, scoreRed);
            if (win) p.AddChallenge(ChallengeKind.WinMatches, 1);
            if (scoreBlue == 0) p.AddChallenge(ChallengeKind.CleanSheet, 1);
            r.lines.Add($"+{reward} монет (результат + {scoreRed} × 50 за голы)");
        }
        int ready = p.ChallengesReady();
        if (ready > 0) r.lines.Add($"Испытаний можно забрать: {ready}");
        p.Save();
        return r;
    }

    // ------------------------------------------------------------ события от игроков и мяча

    public void OnKick(Player kicker, Player receiver, bool lofted)
    {
        if (phase == Phase.SetPiece) { phase = Phase.Play; wallSpots.Clear(); }   // стандарт разыгран — время пошло
        passReceiver = receiver;
        passTimer = 2.5f;
        TrackKick(kicker, receiver);
        // Твоя команда отдала пас — управление сразу переходит к адресату
        if (receiver != null && receiver.team == HumanTeam) controlled = receiver;   // и полевому, и вратарю
        // Соперник отдал пас/ударил — автосмена на того, кто лучше успевает к мячу
        else if (kicker.team != HumanTeam) ScheduleAutoSwitch(lofted, false);
        // Твой вратарь выбил мяч никому конкретно — сразу даём полевого, ближайшего к полёту мяча
        else if (kicker.role == Role.Keeper && kicker == controlled) autoSwitchAt = Time.time + 0.15f;
    }

    // ------------------------------------------------------------ статистика

    /// <summary>Пас партнёру — попытка паса; удар в сторону ворот — удар (в створ — если летит в рамку).</summary>
    void TrackKick(Player kicker, Player receiver)
    {
        int t = (int)kicker.team;
        pendingPass = null;
        if (receiver != null && receiver != kicker && receiver.team == kicker.team)
        {
            Stats.passes[t]++;
            pendingPass = kicker;
            return;
        }
        if (!IsShotAt(kicker.team, out bool onTarget)) return;
        Stats.shots[t]++;
        if (onTarget) Stats.onTarget[t]++;
        shotBy = kicker;
        shotTime = Time.time;
        shotOnTarget = onTarget;
        shotSaved = false;
    }

    /// <summary>Мяч только что пробит: летит ли он к чужим воротам (±3 м от штанг) и попадёт ли в рамку.</summary>
    bool IsShotAt(Team team, out bool onTarget)
    {
        onTarget = false;
        Vector3 p = ball.Body.position, v = ball.Body.linearVelocity;
        float dx = OwnGoalX(Player.Opp(team)) - p.x;
        if (Mathf.Abs(dx) > 28f || v.x * dx <= 0f || new Vector2(v.x, v.z).magnitude < 10f) return false;
        float t = dx / v.x;
        float z = p.z + v.z * t, y = p.y + v.y * t - 4.9f * t * t;
        if (Mathf.Abs(z) > goalWidth * 0.5f + 3f) return false;
        onTarget = Mathf.Abs(z) < goalWidth * 0.5f && y < goalHeight;
        return true;
    }

    /// <summary>Закрученный или срикошетивший удар мог «уйти» в створ позже — засчитываем, когда его спас вратарь или он влетел.</summary>
    void ConfirmOnTarget()
    {
        if (shotBy == null || shotOnTarget) return;
        shotOnTarget = true;
        Stats.onTarget[(int)shotBy.team]++;
    }

    void TrackPossession(Player owner)
    {
        possTeam = owner.team;
        if (pendingPass != null)
        {
            if (owner.team == pendingPass.team && owner != pendingPass)
            {
                Stats.passesDone[(int)owner.team]++;
                assistFrom = pendingPass;
                assistTo = owner;
            }
            pendingPass = null;
        }
        if (assistTo != null && assistTo.team != owner.team) assistFrom = assistTo = null;   // соперник отобрал — голевой паса не будет
        if (shotBy != null)
        {
            // Вратарь поймал удар соперника — сейв
            if (owner.role == Role.Keeper && owner.team != shotBy.team && Time.time - shotTime < 3f) OnSave(owner);
            shotBy = null;
        }
    }

    /// <summary>Сейв вратаря (поймал или отбил удар). Отбитый мяч, влетевший потом в ворота, — гол автора удара.</summary>
    public void OnSave(Player keeper)
    {
        if (shotSaved || shotBy == null || shotBy.team == keeper.team) return;
        shotSaved = true;
        ConfirmOnTarget();
        Stats.saves[(int)keeper.team]++;
        GameAudio.CrowdOh();
    }

    GoalEvent RecordGoal(Team scoring)
    {
        Player s = ball.lastTouch;
        bool recentShot = shotBy != null && shotBy.team == scoring && Time.time - shotTime < 4f;
        bool own = false;
        if (s == null) s = recentShot ? shotBy : null;
        else if (s.team != scoring)
        {
            if (recentShot) s = shotBy;          // рикошет от защитника или вратаря — гол автора удара
            else own = true;
        }
        if (!own)
        {
            if (recentShot && s == shotBy) ConfirmOnTarget();
            else { Stats.shots[(int)scoring]++; Stats.onTarget[(int)scoring]++; }   // «закатил» без удара — тоже удар в створ
        }
        var g = new GoalEvent
        {
            team = scoring,
            scorer = s != null ? s.displayName : "—",
            own = own,
            assist = !own && s != null && assistTo == s && assistFrom != null && assistFrom != s ? assistFrom.displayName : null,
            time = Mathf.Clamp(matchDuration - timeLeft, 0f, matchDuration),
        };
        Stats.goals.Add(g);
        shotBy = assistFrom = assistTo = pendingPass = null;
        return g;
    }

    public void OnPossession(Player owner)
    {
        passReceiver = null;
        TrackPossession(owner);
        if (owner.team == HumanTeam) controlled = owner;   // мяч у своего (и у вратаря) — управляешь им
        else if (owner.team != HumanTeam && Profile.Current.autoSwitch == 0) ScheduleAutoSwitch(false, false);
    }

    /// <summary>
    /// LB / L1 и правый стик. Работают всегда, пока идёт игра (и когда мяч ничей, и в обороне):
    /// с мячом LB — забегание партнёра, правый стик — финт; без мяча — смена игрока.
    /// </summary>
    void HandleSwitchInput()
    {
        if (controlled == null || (phase != Phase.Play && phase != Phase.SetPiece)) return;
        bool withBall = controlled.HasBall || IsTaker(controlled);
        if (GameInput.Down(Btn.Switch))
        {
            if (withBall) CallRun(controlled);
            else SwitchControl();
        }
        else if (GameInput.RightStickFlick(out Vector2 rs))
        {
            Vector3 d = CameraRelative(rs);
            if (controlled.HasBall) controlled.SkillMove(d);
            else if (!IsTaker(controlled)) SwitchControlDir(d);
        }
    }

    /// <summary>
    /// Мяч ничей (летит или катится) — управление само переходит к игроку, который ближе всех к его пути.
    /// Не срабатывает в режиме автосмены «Вручную», сразу после ручной смены и когда мяч летит адресату твоего паса.
    /// </summary>
    void LooseBallAutoSelect()
    {
        if (phase != Phase.Play || controlled == null || ball.Owner != null || ball.Held) return;
        if (Profile.Current.autoSwitch == 2 || Time.time < manualSwitchUntil || Time.time < looseSwitchCooldown) return;
        if (passReceiver != null && passReceiver.team == HumanTeam) return;
        Player best = BestInterceptor(out float bestD);
        float cur = controlled.role == Role.Keeper && !BallComingToKeeper(controlled) ? float.MaxValue : DistanceToBallPath(controlled);
        if (best != null && best != controlled && cur - bestD > 1f)
        {
            controlled = best;
            looseSwitchCooldown = Time.time + 0.5f;       // не «дёргаем» управление чаще, чем раз в полсекунды
        }
    }

    /// <summary>Мяч стал ничьим (рикошет, отбив, плохое касание, штанга).</summary>
    public void OnLooseBall()
    {
        passReceiver = null;
        ScheduleAutoSwitch(!ball.Grounded, true);
    }

    /// <summary>Короткая надпись по центру экрана (финт, сейв, штанга…).</summary>
    public void Flash(string text)
    {
        flashText = text;
        flashTimer = 1f;
    }

    /// <summary>
    /// Автосмена (как в настройках FIFA): «Авто» — при каждом пасе/потере; «Мячи в воздухе и ничьи» — только на
    /// навесах и отскоках; «Вручную» — никогда (кроме твоих пасов). Срабатывает с задержкой 0.12 с, когда
    /// траектория мяча уже понятна.
    /// </summary>
    void ScheduleAutoSwitch(bool air, bool loose)
    {
        int modeSetting = Profile.Current.autoSwitch;
        if (!InMatch || modeSetting == 2 || (modeSetting == 1 && !air && !loose)) return;
        autoSwitchAt = Time.time + 0.12f;
    }

    void DoAutoSwitch()
    {
        if (controlled == null || IsTaker(controlled) || controlled.HasBall) return;
        if (Time.time < manualSwitchUntil) return;          // ты только что переключился сам — не перебиваем
        Player best = BestInterceptor(out float bestD);
        float cur = DistanceToBallPath(controlled);
        if (best != null && best != controlled && cur - bestD > 1.5f) controlled = best;
    }

    /// <summary>Кто из твоих полевых ближе всего к пути мяча (от мяча до точки, где он остановится/приземлится).</summary>
    Player BestInterceptor(out float bestD)
    {
        Player best = null;
        bestD = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team != HumanTeam) continue;
            if (p.role == Role.Keeper && !BallComingToKeeper(p)) continue;   // вратарь — только если мяч идёт к нему
            float d = DistanceToBallPath(p);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>Мяч идёт к вратарю: пас ему или ничей мяч остановится в его штрафной.</summary>
    bool BallComingToKeeper(Player k)
    {
        if (passReceiver == k) return true;
        if (ball.Owner != null || ball.Held) return false;
        Vector3 rest = ball.PredictRest();
        float gx = OwnGoalX(k.team);
        return Mathf.Abs(rest.x - gx) < boxDepth + 0.5f && Mathf.Abs(rest.z) < boxHalfWidth + 0.5f;
    }

    float DistanceToBallPath(Player p)
    {
        Vector3 b = ball.transform.position; b.y = 0f;
        Player owner = ball.Owner;
        if (owner != null) return Vector3.Distance(p.Position, owner.Position);
        return DistanceToSegment(p.Position, b, ball.PredictRest());
    }

    /// <summary>LB с мячом: самый продвинутый партнёр делает забегание на 2.5 с (под пас на ход).</summary>
    public void CallRun(Player passer)
    {
        Vector3 attack = passer.team == Team.Red ? Vector3.right : Vector3.left;
        Player best = null;
        float bestX = float.MinValue;
        foreach (var p in players)
        {
            if (p.team != passer.team || p.role != Role.Field || p == passer) continue;
            float x = Vector3.Dot(p.Position, attack);
            if (x > bestX) { bestX = x; best = p; }
        }
        if (best == null) return;
        runner = best;
        runUntil = Time.time + 2.5f;
        Flash("ЗАБЕГАНИЕ: " + best.displayName.ToUpper());
    }

    /// <summary>Фол (подкат в соперника раньше мяча): штрафной, а в своей штрафной — пенальти.</summary>
    public void OnFoul(Player fouler, Player victim)
    {
        if (phase != Phase.Play) return;
        Stats.fouls[(int)fouler.team]++;
        Team defending = fouler.team;
        Vector3 spot = ClampToField(victim.Position, 0.5f);
        float gx = OwnGoalX(defending);
        bool inBox = Mathf.Abs(spot.x - gx) < boxDepth && Mathf.Abs(spot.z) < boxHalfWidth;
        if (inBox)
            StopForSetPiece(SetPieceType.Penalty, victim.team, new Vector3(gx - Mathf.Sign(gx) * 4.5f, 0f, 0f), 1.5f, "ПЕНАЛЬТИ");
        else
            StopForSetPiece(SetPieceType.FreeKick, victim.team, spot, 1.2f, "Фол! Штрафной");
        GameAudio.Whistle(inBox ? 1 : 0);
        Shake(0.1f);
    }

    public bool WallSpot(Player p, out Vector3 pos) => wallSpots.TryGetValue(p, out pos);

    /// <summary>На пенальти все полевые, кроме бьющего, стоят за пределами штрафной.</summary>
    public Vector3 KeepOutOfPenaltyBox(Vector3 v)
    {
        if (phase != Phase.SetPiece || spType != SetPieceType.Penalty) return v;
        float gx = OwnGoalX(Player.Opp(spTeam));
        if (Mathf.Abs(v.x - gx) < boxDepth + 1f && Mathf.Abs(v.z) < boxHalfWidth + 1f)
            v.x = gx - Mathf.Sign(gx) * (boxDepth + 1.5f);
        return v;
    }

    /// <summary>Персональная опека: для защитника — точка между опекаемым и своими воротами.</summary>
    public bool MarkTarget(Player p, out Vector3 pos)
    {
        pos = Vector3.zero;
        if (!marks.TryGetValue(p, out Player a) || a == null) return false;
        Vector3 goal = GoalOf(p.team);
        pos = a.Position + (goal - a.Position).normalized * 1.6f;
        return true;
    }

    /// <summary>Раздаём опеку: самого опасного (ближе к нашим воротам) соперника — ближайшему свободному защитнику.</summary>
    void UpdateMarking()
    {
        markTimer -= Time.deltaTime;
        if (markTimer > 0f) return;
        markTimer = 0.3f;
        marks.Clear();
        Player owner = ball.Owner;
        var free = new List<Player>();
        for (int t = 0; t < 2; t++)
        {
            Team team = (Team)t;
            free.Clear();
            foreach (var p in players)
                if (p.team == team && p.role == Role.Field && p != chaser[t] && p != controlled) free.Add(p);
            foreach (var a in SortedByDistance(Player.Opp(team), GoalOf(team)))
            {
                if (a.role != Role.Field || a == owner || free.Count == 0) continue;
                Player best = null;
                float bestD = float.MaxValue;
                foreach (var d in free)
                {
                    float dist = Vector3.Distance(d.Position, a.Position);
                    if (dist < bestD) { bestD = dist; best = d; }
                }
                marks[best] = a;
                free.Remove(best);
            }
        }
    }

    List<Player> SortedByDistance(Team team, Vector3 point)
    {
        var list = players.FindAll(p => p.team == team);
        list.Sort((x, y) => Vector3.Distance(x.Position, point).CompareTo(Vector3.Distance(y.Position, point)));
        return list;
    }

    public void RequestKeeperRush() => keeperRushUntil = Time.time + 0.1f;
    public void RequestTeammatePress() => teammatePressUntil = Time.time + 0.1f;

    /// <summary>LB / Q: переключиться на игрока своей команды, ближайшего к мячу (кроме текущего).</summary>
    public void SwitchControl()
    {
        if (IsTaker(controlled)) return;
        Player best = NextSwitchTarget();
        if (best == null) return;
        controlled = best;
        manualSwitchUntil = Time.time + 1.5f;
        autoSwitchAt = 0f;
    }

    /// <summary>Флик правым стиком: переключиться на партнёра в этом направлении.</summary>
    public void SwitchControlDir(Vector3 dir)
    {
        if (IsTaker(controlled) || dir.sqrMagnitude < 0.01f) return;
        Player best = null;
        float bestScore = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team != HumanTeam || p.role != Role.Field || p == controlled) continue;
            Vector3 to = p.Position - controlled.Position;
            float angle = Vector3.Angle(dir, to);
            if (angle > 60f) continue;
            float score = angle + to.magnitude * 0.5f;
            if (score < bestScore) { bestScore = score; best = p; }
        }
        if (best == null) return;
        controlled = best;
        manualSwitchUntil = Time.time + 1.5f;
        autoSwitchAt = 0f;
    }

    // ------------------------------------------------------------ запросы для ИИ

    public float OwnGoalX(Team t) => t == Team.Red ? -L : L;
    public Vector3 GoalOf(Team t) => new Vector3(OwnGoalX(t), 0f, 0f);
    public bool IsChaser(Player p) => chaser[(int)p.team] == p;
    public string TeamName(Team t) => t == HumanTeam ? Profile.Current.clubName : Catalog.OpponentClub;

    /// <summary>Позиция «держать место»: базовая точка, смещённая за мячом; в атаке — выше по полю.</summary>
    public Vector3 FormationPos(Player p, bool attacking)
    {
        Vector3 b = ball.transform.position;
        float forward = (p.team == Team.Red ? 1f : -1f) * (attacking ? 5f : -2f);
        return ClampToField(p.homePos + new Vector3(b.x * 0.55f + forward, 0f, b.z * 0.35f), 1f);
    }

    /// <summary>
    /// Точка поддержки, когда мяч у своей команды: не «своя позиция» где-то далеко, а место рядом с игроком
    /// с мячом — защитники страхуют сзади-сбоку, нападающие открываются впереди по флангам (треугольники для паса).
    /// </summary>
    public Vector3 SupportPos(Player p)
    {
        Player owner = ball.Owner;
        Vector3 anchor = owner != null ? owner.Position : new Vector3(ball.transform.position.x, 0f, ball.transform.position.z);
        Vector3 fwd = p.team == Team.Red ? Vector3.right : Vector3.left;
        bool defender = Mathf.Abs(p.homePos.x) > L * 0.4f;
        float side = p.homePos.z >= 0f ? 1f : -1f;
        Vector3 pos = anchor + fwd * (defender ? -5f : 7f) + Vector3.forward * side * (defender ? 5f : 6f);
        pos = Vector3.Lerp(FormationPos(p, true), pos, 0.65f);     // немного держим строй
        return ClampToField(pos, 1f);
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
    /// свободнее линия паса (нет соперников рядом с отрезком), тем лучше. Для навеса линия не важна.
    /// Вратарь — только если больше некому.
    /// </summary>
    public Player FindPassTarget(Player passer, Vector3 prefDir, float maxAngle, bool ignoreLane, float maxDist = 28f, float preferDist = -1f)
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
            if (d < 2.5f || d > maxDist) continue;
            float angle = Vector3.Angle(prefDir, to);
            if (angle > maxAngle) continue;

            float score = -angle * 1.2f - (preferDist > 0f ? Mathf.Abs(d - preferDist) * 1.2f : d * 0.4f);
            if (mate.role == Role.Keeper) score -= 60f;
            if (!ignoreLane)
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

    public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
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
    /// В каждой команде к мячу бежит ближайший полевой (в твоей — если ближе всех ты, ИИ держит позиции).
    /// pressHelper — ближайший к мячу партнёр, кроме тебя: он прессингует по RB / R1.
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

        pressHelper = null;
        float helperD = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team != HumanTeam || p.role != Role.Field || p == controlled) continue;
            float d = Vector3.Distance(p.Position, b);
            if (d < helperD) { helperD = d; pressHelper = p; }
        }
    }

    // ------------------------------------------------------------ спавн

    void SpawnTeams(MatchMode m)
    {
        foreach (var p in players)
        {
            p.gameObject.SetActive(false);   // сразу выключаем, чтобы старые капсулы не толкали новые в этом кадре
            Destroy(p.gameObject);
        }
        players.Clear();

        // Расстановка твоей команды (своя половина -X). Соперник — зеркально по X.
        Vector3[] layout =
        {
            new Vector3(-L + 1f,  0f, 0f),              // 0 вратарь
            new Vector3(-L * 0.6f, 0f, -W * 0.4f),      // 1 защитник
            new Vector3(-L * 0.6f, 0f,  W * 0.4f),      // 2 защитник
            new Vector3(-L * 0.2f, 0f, -W * 0.3f),      // 3 нападающий
            new Vector3(-L * 0.2f, 0f,  W * 0.3f),      // 4 нападающий
        };
        // Кто из соперников выходит на поле в каждом режиме
        int[] opponents = m == MatchMode.PracticeFree ? new int[0]
                        : m == MatchMode.PracticeKeeper ? new[] { 0 }
                        : m == MatchMode.PracticeDefense ? new[] { 0, 1, 2 }
                        : new[] { 0, 1, 2, 3, 4 };

        var prof = Profile.Current;
        Color kit = Catalog.Kits[prof.kit].color;
        Color oppKit = Mathf.Abs(kit.b - Catalog.OpponentBlue.b) + Mathf.Abs(kit.r - Catalog.OpponentBlue.r) < 0.4f
            ? Catalog.OpponentAlt : Catalog.OpponentBlue;

        for (int i = 0; i < layout.Length; i++)
        {
            var p = CreatePlayer(Team.Red, i == 0 ? Role.Keeper : Role.Field, layout[i], prof.playerNames[i], kit);
            p.ApplyUpgrades(prof);
            players.Add(p);
        }
        foreach (int i in opponents)
        {
            Vector3 home = layout[i];
            home.x = -home.x;
            var p = CreatePlayer(Team.Blue, i == 0 ? Role.Keeper : Role.Field, home, Catalog.OpponentNames[i], oppKit);
            p.ApplyDifficulty(prof.difficulty);
            players.Add(p);
        }
        controlled = players[4];
        passReceiver = null;
        Paint(ball.gameObject, Catalog.Balls[prof.ballSkin].color);
    }

    Player CreatePlayer(Team team, Role role, Vector3 home, string name, Color kit)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"{team}_{role}_{name}";
        float lum = (kit.r + kit.g + kit.b) / 3f;
        Color c = role == Role.Keeper ? (lum < 0.3f ? Color.Lerp(kit, Color.white, 0.45f) : Color.Lerp(kit, Color.black, 0.45f)) : kit;
        Paint(go, c);

        // 3D-модель человека (если настроена — см. Assets/Editor/PlayerModelSetup.cs), иначе капсула с «носом»
        Transform model = null;
        GameObject prefab = PlayerModelPrefab;
        if (prefab != null)
        {
            var m = Instantiate(prefab, go.transform);
            m.transform.localPosition = new Vector3(0f, -1f, 0f);     // ноги модели — на газоне (центр капсулы на высоте 1 м)
            m.transform.localRotation = Quaternion.identity;
            go.GetComponent<Renderer>().enabled = false;
            TintKit(m, c, lum);
            model = m.transform;
        }
        else
        {
            // «Нос» показывает, куда смотрит игрок
            Prim(PrimitiveType.Cube, go.transform, new Vector3(0f, 0.5f, 0.45f), new Vector3(0.25f, 0.15f, 0.3f),
                 lum > 0.7f ? new Color(0.15f, 0.15f, 0.15f) : Color.white);
        }

        var p = go.AddComponent<Player>();
        p.Init(this, team, role, home, name);
        if (model != null) p.AttachModel(model);
        // Мяч не сталкивается с капсулами физически — касания считает Ball (контроль, блоки)
        Physics.IgnoreCollision(ball.Col, go.GetComponent<Collider>());
        return p;
    }

    static GameObject playerModelPrefab;
    static bool playerModelLoaded;
    static GameObject PlayerModelPrefab
    {
        get
        {
            if (!playerModelLoaded) { playerModelPrefab = Resources.Load<GameObject>("Models/PlayerModel"); playerModelLoaded = true; }
            return playerModelPrefab;
        }
    }

    /// <summary>
    /// Форма в цвет команды: футболка и гетры — цвет формы, шорты — тёмные (или светлые для тёмной формы).
    /// Текстуру у этих частей убираем, чтобы цвет был чистым. Части ищем по имени меша (у Mixamo: *_Shirt, *_Shorts, *_Socks).
    /// </summary>
    static void TintKit(GameObject model, Color kit, float lum)
    {
        Color shorts = lum < 0.3f ? new Color(0.9f, 0.9f, 0.9f) : new Color(0.12f, 0.12f, 0.14f);
        foreach (var r in model.GetComponentsInChildren<Renderer>())
        {
            string n = r.name.ToLowerInvariant();
            Color? col = n.Contains("shirt") || n.Contains("sock") ? kit : n.Contains("short") ? shorts : (Color?)null;
            if (col == null) continue;
            foreach (var mat in r.materials)
            {
                mat.mainTexture = null;
                mat.color = col.Value;
            }
        }
    }

    /// <summary>
    /// Создаёт недостающие индикаторы. Вызывается и из UpdateIndicators: если Unity перекомпилировала скрипты
    /// прямо во время Play, Start() не повторяется, а новые поля остаются пустыми.
    /// </summary>
    void CreateIndicators()
    {
        if (marker == null) marker = NoShadow(Prim(PrimitiveType.Sphere, null, Vector3.zero, Vector3.one * 0.16f, Color.yellow)).transform;
        if (nextSwitch == null) nextSwitch = NoShadow(Prim(PrimitiveType.Cube, null, Vector3.zero, Vector3.one * 0.13f, new Color(0.3f, 0.85f, 1f))).transform;
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    /// <summary>Индикаторы не отбрасывают и не принимают тени.</summary>
    static GameObject NoShadow(GameObject go)
    {
        var r = go.GetComponent<Renderer>();
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go;
    }

    /// <summary>
    /// Кого выберет LB: партнёр (кроме текущего), который быстрее всех вступит в игру —
    /// ближе к мячу и стоит между мячом и своими воротами (тот, кто остался за спиной атаки, получает штраф).
    /// </summary>
    Player NextSwitchTarget()
    {
        Vector3 b = ball.transform.position;
        b.y = 0f;
        Vector3 ownGoal = GoalOf(HumanTeam);
        float ballToGoal = Vector3.Distance(b, ownGoal);
        Player best = null;
        float bestScore = float.MaxValue;
        foreach (var p in players)
        {
            if (p.team != HumanTeam || p.role != Role.Field || p == controlled) continue;
            float score = Vector3.Distance(p.Position, b);
            if (Vector3.Distance(p.Position, ownGoal) > ballToGoal + 1f) score += 4f;   // не «под мячом», а позади атаки
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>Жёлтый шар над тобой, стрелка прицела и оранжевая линия/кольцо к тому, кому уйдёт пас.</summary>
    /// <summary>
    /// Как в FIFA: над твоим игроком — жёлтый маркер (и имя в HUD), над тем, на кого переключит LB, — голубой ромб.
    /// Линий «куда уйдёт пас» нет: направление паса задаёшь стиком.
    /// </summary>
    void UpdateIndicators()
    {
        if (marker == null || nextSwitch == null) CreateIndicators();
        bool show = InMatch && controlled != null && phase != Phase.Over;
        marker.gameObject.SetActive(show);
        // Берём transform (он интерполируется между шагами физики), а не rigidbody — иначе маркер дрожит на бегу
        if (show) marker.position = Flat(controlled.transform.position) + Vector3.up * 2.15f;

        Player next = show && !controlled.HasBall && !IsTaker(controlled) ? NextSwitchTarget() : null;
        nextSwitch.gameObject.SetActive(next != null);
        if (next != null)
        {
            nextSwitch.position = Flat(next.transform.position) + Vector3.up * 2.15f;
            nextSwitch.rotation = Quaternion.Euler(45f, Time.time * 180f, 45f);
        }
    }

    // ------------------------------------------------------------ поле

    void BuildArena()
    {
        Transform root = new GameObject("Arena").transform;

        // Газон с запасом за линиями (зона аутов), верхняя грань на y = 0, и тёмная «трибуна» вокруг
        Prim(PrimitiveType.Cube, root, new Vector3(0f, -0.5f, 0f), new Vector3(length + 10f, 1f, width + 10f),
             new Color(0.2f, 0.55f, 0.25f), true);
        Prim(PrimitiveType.Cube, root, new Vector3(0f, -0.6f, 0f), new Vector3(length + 60f, 1f, width + 60f),
             new Color(0.12f, 0.13f, 0.16f), false);
        // Полосы газона, как на стадионе
        for (int i = 0; i < 10; i += 2)
            Prim(PrimitiveType.Cube, root, new Vector3(-L + length / 10f * (i + 0.5f), 0.004f, 0f),
                 new Vector3(length / 10f, 0.005f, width), new Color(0.22f, 0.6f, 0.27f));

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

        foreach (float s in new[] { -1f, 1f })   // s = -1 — твои ворота, +1 — соперника
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

        // Рекламные щиты за дальней боковой линией (декор, без коллайдеров)
        for (int i = 0; i < 8; i++)
            Prim(PrimitiveType.Cube, root, new Vector3(-L + 2.5f + i * 5f, 0.5f, W + 3.5f), new Vector3(4.6f, 1f, 0.2f),
                 i % 2 == 0 ? new Color(0.85f, 0.3f, 0.1f) : new Color(0.1f, 0.12f, 0.2f));
    }

    void BuildGoal(Transform root, float s)
    {
        float gw = goalWidth * 0.5f, gh = goalHeight, gd = goalDepth;
        Color net = new Color(0.8f, 0.8f, 0.8f);

        // Штанги и перекладина — с коллайдерами, мяч от них отскакивает (имя «Post» — для встряски камеры)
        Prim(PrimitiveType.Cube, root, new Vector3(s * L, gh * 0.5f,  gw), new Vector3(0.2f, gh, 0.2f), Color.white, true).name = "Post";
        Prim(PrimitiveType.Cube, root, new Vector3(s * L, gh * 0.5f, -gw), new Vector3(0.2f, gh, 0.2f), Color.white, true).name = "Post";
        Prim(PrimitiveType.Cube, root, new Vector3(s * L, gh, 0f), new Vector3(0.2f, 0.2f, goalWidth + 0.2f), Color.white, true).name = "Post";

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
        trig.name = s < 0 ? "GoalTrigger_Home" : "GoalTrigger_Away";
        trig.GetComponent<Renderer>().enabled = false;
        var col = trig.GetComponent<Collider>();
        col.isTrigger = true;
        if (s < 0) goalLeft = col; else goalRight = col;
    }

    static void Fence(Transform root, Vector3 center, Vector3 size) =>
        Prim(PrimitiveType.Cube, root, center, size, Color.clear, true).GetComponent<Renderer>().enabled = false;

    Transform Line(Transform root, Vector3 pos, Vector3 size) =>
        Prim(PrimitiveType.Cube, root, pos, size, Color.white).transform;

    /// <summary>Примитив с цветом; коллайдер удаляется, если он не нужен (разметка, индикаторы, декор).</summary>
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

    // ------------------------------------------------------------ HUD матча

    void OnGUI()
    {
        if (!InMatch) return;
        UI.Begin();
        float w = UI.Width;
        var prof = Profile.Current;

        // Табло сверху по центру: клуб | счёт | соперник, под ним таймер
        float cx = w * 0.5f;
        UI.Box(new Rect(cx - 360, 18, 720, 58), new Color(0.06f, 0.07f, 0.09f, 0.92f));
        UI.Label(new Rect(cx - 350, 18, 250, 58), prof.clubName.ToUpper(), UI.Head, 26, Color.white, TextAnchor.MiddleRight);
        UI.Box(new Rect(cx - 90, 24, 80, 46), new Color(1f, 1f, 1f, 0.08f));
        UI.Box(new Rect(cx + 10, 24, 80, 46), new Color(1f, 1f, 1f, 0.08f));
        UI.Label(new Rect(cx - 90, 18, 80, 58), scoreRed.ToString(), UI.Head, 38, Color.white, TextAnchor.MiddleCenter);
        UI.Label(new Rect(cx + 10, 18, 80, 58), scoreBlue.ToString(), UI.Head, 38, Color.white, TextAnchor.MiddleCenter);
        UI.Label(new Rect(cx + 100, 18, 250, 58), Catalog.OpponentClub.ToUpper(), UI.Head, 26, Color.white, TextAnchor.MiddleLeft);
        int secs = Mathf.CeilToInt(Mathf.Max(timeLeft, 0f));
        UI.Box(new Rect(cx - 70, 80, 140, 44), UI.Lime);
        UI.Label(new Rect(cx - 70, 80, 140, 44), $"{secs / 60:00}:{secs % 60:00}", UI.Head, 30, UI.Dark, TextAnchor.MiddleCenter);

        // Имена игроков вдоль табло: свои слева, соперники справа; твой — подчёркнут
        float x = 30;
        foreach (var p in players)
            if (p.team == HumanTeam)
            {
                UI.Box(new Rect(x, 136, 140, 32), new Color(0.06f, 0.07f, 0.09f, 0.75f));
                if (p == controlled) UI.Box(new Rect(x, 164, 140, 4), UI.Lime);
                UI.Label(new Rect(x, 136, 140, 32), p.displayName, UI.Body, 18, Color.white, TextAnchor.MiddleCenter);
                x += 146;
            }
        x = w - 30 - 140;
        foreach (var p in players)
            if (p.team != HumanTeam)
            {
                UI.Box(new Rect(x, 136, 140, 32), new Color(0.06f, 0.07f, 0.09f, 0.75f));
                UI.Label(new Rect(x, 136, 140, 32), p.displayName, UI.Body, 18, Color.white, TextAnchor.MiddleCenter);
                x -= 146;
            }

        if (practiceIndex >= 0)
            UI.Label(new Rect(0, 176, w, 34), $"{Catalog.Practices[practiceIndex].title}: забей {Catalog.Practices[practiceIndex].goalsNeeded}",
                     UI.Body, 22, UI.Lime, TextAnchor.MiddleCenter);

        if (!string.IsNullOrEmpty(banner))
        {
            UI.Box(new Rect(0, 1080 * 0.36f, w, 110), new Color(0f, 0f, 0f, 0.55f));
            UI.Label(new Rect(0, 1080 * 0.36f, w, 110), banner.ToUpper(), UI.Head, 64, Color.white, TextAnchor.MiddleCenter);
            if (!string.IsNullOrEmpty(bannerSub))
            {
                UI.Box(new Rect(0, 1080 * 0.36f + 110, w, 44), new Color(0f, 0f, 0f, 0.45f));
                UI.Label(new Rect(0, 1080 * 0.36f + 110, w, 44), bannerSub, UI.Body, 28, UI.Lime, TextAnchor.MiddleCenter);
            }
        }
        if (phase == Phase.Over)
            UI.Label(new Rect(0, 1080 * 0.36f + 110, w, 40), "Возврат в меню…  (A / Enter — сразу)", UI.Body, 22, Color.white, TextAnchor.MiddleCenter);

        // Вспышка: финт, сейв, штанга, забегание…
        string flash = FlashText;
        if (!string.IsNullOrEmpty(flash) && string.IsNullOrEmpty(banner))
            UI.Label(new Rect(0, 1080 * 0.24f, w, 70), flash, UI.Head, 48, UI.Lime, TextAnchor.MiddleCenter);

        // Стандарт твоей команды — подсказка
        if (IsTaker(controlled))
            UI.Label(new Rect(0, 214, w, 40), SetPieceName(spType).ToUpper() + ":  " +
                     (GameInput.UsingGamepad ? "B — удар (RB+B — закрученный)   A — пас   X — навес   Y — на ход"
                                             : "K — удар (E+K — закрученный)   J — пас   L — навес   I — на ход"),
                     UI.Body, 24, Color.white, TextAnchor.MiddleCenter);

        // Карточка твоего игрока снизу слева: имя, характеристики, выносливость
        if (controlled != null)
        {
            UI.Box(new Rect(40, 950, 380, 60), new Color(0.06f, 0.07f, 0.09f, 0.9f));
            UI.Box(new Rect(40, 950, 60, 60), Catalog.Kits[prof.kit].color);
            UI.Label(new Rect(115, 950, 220, 60), controlled.displayName.ToUpper(), UI.Head, 28, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(300, 950, 110, 60), IsTaker(controlled) ? "СТАНДАРТ" : "", UI.Body, 20, UI.Lime, TextAnchor.MiddleRight);
            UI.Box(new Rect(40, 1010, 380, 8), new Color(0f, 0f, 0f, 0.6f));
            UI.Box(new Rect(40, 1010, 380 * controlled.stamina, 8), Color.Lerp(UI.Orange, UI.Lime, controlled.stamina));
            UI.Label(new Rect(40, 1020, 600, 30),
                $"СКО {60 + prof.speedLvl * 7}   УДР {58 + prof.shotLvl * 7}   КОН {62 + prof.controlLvl * 7}   ВЫН {Mathf.RoundToInt(controlled.stamina * 100)}",
                UI.Body, 20, new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleLeft);

            // Шкала силы: удар (B) — от лайма к красному, навес / пас на ход (X / Y) — голубая
            float ch = Mathf.Max(controlled.charge, controlled.passCharge);
            if (ch > 0f)
            {
                bool pass = controlled.passCharge > controlled.charge;
                UI.Box(new Rect(40, 910, 380, 26), new Color(0f, 0f, 0f, 0.7f));
                UI.Box(new Rect(43, 913, 374 * ch, 20),
                       pass ? new Color(0.3f, 0.8f, 1f) : Color.Lerp(UI.Lime, new Color(1f, 0.3f, 0.2f), ch));
                UI.Label(new Rect(40, 880, 380, 28), pass ? "СИЛА ПАСА" : "СИЛА УДАРА", UI.Body, 20, Color.white, TextAnchor.MiddleLeft);
            }

            // Имя над игроком, как в FIFA
            Vector3 sp = cam.WorldToScreenPoint(Flat(controlled.transform.position) + Vector3.up * 2.55f);
            if (sp.z > 0f)
            {
                float sx = sp.x / UI.Scale, sy = (Screen.height - sp.y) / UI.Scale;
                UI.Box(new Rect(sx - 70, sy - 16, 140, 28), new Color(0.06f, 0.07f, 0.09f, 0.75f));
                UI.Label(new Rect(sx - 70, sy - 16, 140, 28), controlled.displayName, UI.Body, 18, Color.yellow, TextAnchor.MiddleCenter);
            }
        }

        // Соперник с мячом — снизу справа
        Player owner = ball.Owner;
        if (owner != null && owner.team != HumanTeam)
        {
            UI.Box(new Rect(w - 420, 950, 380, 60), new Color(0.06f, 0.07f, 0.09f, 0.9f));
            UI.Label(new Rect(w - 405, 950, 300, 60), owner.displayName.ToUpper(), UI.Head, 28, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(w - 160, 950, 110, 60), "С МЯЧОМ", UI.Body, 20, UI.Orange, TextAnchor.MiddleRight);
        }

        DrawRadar(new Rect(w * 0.5f - 150, 860, 300, 180));

        if (prof.showHints && !paused)
        {
            bool def = owner != null && owner.team != HumanTeam;
            string hint = GameInput.UsingGamepad
                ? (def ? "A (держать) — сдерживание   X — подкат   B — отбор   Y — вратарь   RB — прессинг   LB / R — смена   LT — выжидание"
                       : "A — пас   B — удар   X / Y (держать) — навес / на ход   RB — укрывание   LB — забегание / смена   R — финты   RT — спринт")
                : (def ? "J (держать) — сдерживание   L — подкат   K — отбор   I — вратарь   E — прессинг   Q / TFGH — смена   Space — выжидание"
                       : "J — пас   K — удар   L / I (держать) — навес / на ход   E — укрывание   Q — забегание / смена   TFGH — финты   Shift — спринт");
            UI.Label(new Rect(0, 1050, w, 30), hint, UI.Body, 18, new Color(1f, 1f, 1f, 0.8f), TextAnchor.MiddleCenter);
        }
    }

    /// <summary>Радар (мини-карта) как в FIFA: поле, игроки команд, мяч; ты — с жёлтой обводкой.</summary>
    void DrawRadar(Rect r)
    {
        UI.Box(r, new Color(0.05f, 0.25f, 0.1f, 0.7f));
        UI.Box(new Rect(r.center.x - 1, r.y, 2, r.height), new Color(1f, 1f, 1f, 0.35f));
        Color home = Catalog.Kits[Profile.Current.kit].color;
        foreach (var p in players)
        {
            Vector2 pt = RadarPoint(r, p.Position);
            if (p == controlled) UI.Box(new Rect(pt.x - 7, pt.y - 7, 14, 14), Color.yellow);
            UI.Box(new Rect(pt.x - 5, pt.y - 5, 10, 10), p.team == HumanTeam ? home : Catalog.OpponentBlue);
        }
        Vector2 bp = RadarPoint(r, ball.transform.position);
        UI.Box(new Rect(bp.x - 4, bp.y - 4, 8, 8), Color.white);
    }

    Vector2 RadarPoint(Rect r, Vector3 world) =>
        new Vector2(r.x + (world.x / length + 0.5f) * r.width, r.y + (0.5f - world.z / width) * r.height);
}
