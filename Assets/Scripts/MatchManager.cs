using System.Collections.Generic;
using UnityEngine;

public enum SetPieceType { Kickoff, ThrowIn, Corner, GoalKick }

/// <summary>Итог матча для экрана результата в меню.</summary>
public class MatchResult
{
    public string title, score;
    public bool good;
    public List<string> lines = new List<string>();
}

/// <summary>
/// Ядро игры: поле, игроки, правила (гол, аут, угловой, от ворот), счёт и время, переключение игрока,
/// камера (сбоку / изометрия / облёт в меню), пауза, награды и возврат в меню. HUD матча рисуется здесь,
/// меню и пауза — в MainMenu.
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

    [Header("Размеры поля, м")]
    public float length = 40f, width = 24f;
    public float goalWidth = 6f, goalHeight = 2f, goalDepth = 2f;
    public float boxDepth = 5f, boxHalfWidth = 5f;   // штрафная

    [Header("Паузы")]
    public float goalPause = 1.5f;
    public float outPause = 1f;
    public float resultScreenTime = 4f;

    [Header("Камера сбоку (трансляция)")]
    public float sidePitch = 50f, sideFov = 40f, sideDistance = 30f;
    [Header("Камера изометрия")]
    public Vector3 isoAngles = new Vector3(45f, 45f, 0f);
    public float isoSize = 11f, isoDistance = 40f;
    public float camSmooth = 4f;

    [HideInInspector] public List<Player> players = new List<Player>();
    [HideInInspector] public Player controlled;      // кем ты сейчас управляешь
    [HideInInspector] public Player passReceiver;    // кому летит пас (он выходит на мяч)
    public const Team HumanTeam = Team.Red;

    public MatchResult LastResult { get; set; }       // показывается в меню после матча
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
    string banner;
    int practiceIndex = -1;

    SetPieceType spType, nextType;
    Team spTeam, nextTeam;
    Vector3 spSpot, nextSpot;
    Player taker, pressHelper;

    Collider goalLeft, goalRight;                    // триггеры ворот твоей команды (-X) и соперника (+X)
    readonly Player[] chaser = new Player[2];
    Transform marker, aimArrow, passRing, passLine;  // индикаторы твоего игрока и паса

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
        if (GetComponent<MainMenu>() == null) gameObject.AddComponent<MainMenu>();
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

    void Update()
    {
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
                CheckOut();
                break;

            case Phase.Over:
                resultTimer -= Time.unscaledDeltaTime;
                if (resultTimer <= 0f || GameInput.Down(Btn.Confirm)) QuitToMenu();
                break;
        }

        if (passReceiver != null && (passTimer -= Time.deltaTime) <= 0f) passReceiver = null;
        UpdateChasers();
    }

    void LateUpdate()
    {
        UpdateCamera();
        UpdateIndicators();
    }

    // ------------------------------------------------------------ камера

    void ApplyCameraProjection()
    {
        bool iso = InMatch && Profile.Current.cameraMode == 1;
        cam.orthographic = iso;
        if (iso) { cam.orthographicSize = isoSize; cam.transform.rotation = Quaternion.Euler(isoAngles); }
        else cam.fieldOfView = InMenu ? 35f : sideFov;
    }

    void UpdateCamera()
    {
        float k = 1f - Mathf.Exp(-camSmooth * Time.unscaledDeltaTime);
        Vector3 b = ball.transform.position;

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

        if (Profile.Current.cameraMode == 1)
        {
            // Изометрия: фиксированный угол, камера тянется за мячом
            Vector3 focus = new Vector3(b.x, 0f, b.z);
            cam.transform.rotation = Quaternion.Euler(isoAngles);
            cam.transform.position = Vector3.Lerp(cam.transform.position, focus - cam.transform.forward * isoDistance, k);
        }
        else
        {
            // Сбоку, как в телетрансляции: смотрим поперёк поля, по X ведём мяч, по Z смещаемся слабо
            float fx = Mathf.Clamp(b.x, -(L - 15f), L - 15f);
            Vector3 focus = new Vector3(fx, 0f, b.z * 0.3f);
            Quaternion rot = Quaternion.Euler(sidePitch, 0f, 0f);
            cam.transform.rotation = Quaternion.Slerp(cam.transform.rotation, rot, k);
            cam.transform.position = Vector3.Lerp(cam.transform.position, focus - (rot * Vector3.forward) * sideDistance, k);
        }
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
                StopForSetPiece(SetPieceType.GoalKick, defending,
                                new Vector3(side * (L - 1.5f), 0f, 0f), outPause, "От ворот");
        }
    }

    /// <summary>Вызывается мячом при входе в любой триггер.</summary>
    public void OnBallTrigger(Collider c)
    {
        if (!InMatch || phase != Phase.Play) return;
        Team conceded;
        if (c == goalLeft) { scoreBlue++; conceded = Team.Red; banner = "ГОЛ! " + Catalog.OpponentClub; }
        else if (c == goalRight) { scoreRed++; conceded = Team.Blue; banner = "ГОЛ! " + Profile.Current.clubName; }
        else return;

        // Тренировка: набрал нужное число голов — сразу итог
        if (practiceIndex >= 0 && scoreRed >= Catalog.Practices[practiceIndex].goalsNeeded) { EndMatch(); return; }

        // С центра начинают пропустившие (на тренировке — всегда ты)
        StopForSetPiece(SetPieceType.Kickoff, practiceIndex >= 0 ? HumanTeam : conceded, Vector3.zero, goalPause, null);
    }

    /// <summary>Остановка игры: все замирают, через pause секунд — розыгрыш стандарта.</summary>
    void StopForSetPiece(SetPieceType type, Team team, Vector3 spot, float pause, string title)
    {
        phase = Phase.Stopped;
        phaseTimer = pause;
        nextType = type; nextTeam = team; nextSpot = spot;
        passReceiver = null;
        if (title != null) banner = $"{title}: {TeamName(team)}";
    }

    void BeginSetPiece(SetPieceType type, Team team, Vector3 spot)
    {
        phase = Phase.SetPiece;
        phaseTimer = 0f;
        banner = null;
        passReceiver = null;

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
        Vector3 inField = type == SetPieceType.ThrowIn ? new Vector3(0f, 0f, -Mathf.Sign(spot.z))
                        : type == SetPieceType.Corner ? (-spot).normalized
                        : attack;
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
    }

    /// <summary>Награды и статистика. Всё сохраняется в профиль.</summary>
    MatchResult GiveRewards()
    {
        var p = Profile.Current;
        var r = new MatchResult { score = $"{scoreRed} : {scoreBlue}" };

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

    public void OnKick(Player kicker, Player receiver)
    {
        if (phase == Phase.SetPiece) phase = Phase.Play;   // стандарт разыгран — время пошло
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

    public void RequestKeeperRush() => keeperRushUntil = Time.time + 0.1f;
    public void RequestTeammatePress() => teammatePressUntil = Time.time + 0.1f;

    /// <summary>LB / Q: переключиться на игрока своей команды, ближайшего к мячу (кроме текущего).</summary>
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
        if (best != null) controlled = best;
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
    public Player FindPassTarget(Player passer, Vector3 prefDir, float maxAngle, bool ignoreLane)
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
        float diff = Catalog.DifficultySpeed[prof.difficulty];

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
            p.ApplyDifficulty(diff);
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
        // «Нос» показывает, куда смотрит игрок
        Prim(PrimitiveType.Cube, go.transform, new Vector3(0f, 0.5f, 0.45f), new Vector3(0.25f, 0.15f, 0.3f),
             lum > 0.7f ? new Color(0.15f, 0.15f, 0.15f) : Color.white);

        var p = go.AddComponent<Player>();
        p.Init(this, team, role, home, name);
        // Мяч не сталкивается с капсулами физически — касания считает Ball (контроль, блоки)
        Physics.IgnoreCollision(ball.Col, go.GetComponent<Collider>());
        return p;
    }

    void CreateIndicators()
    {
        Color accent = new Color(1f, 0.55f, 0.1f);
        marker = Prim(PrimitiveType.Sphere, null, Vector3.zero, Vector3.one * 0.4f, Color.yellow).transform;
        aimArrow = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(0.12f, 0.02f, 1.2f), accent).transform;
        passRing = Prim(PrimitiveType.Cylinder, null, Vector3.zero, new Vector3(1.3f, 0.01f, 1.3f), accent).transform;
        passLine = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(0.08f, 0.01f, 1f), accent).transform;
    }

    /// <summary>Жёлтый шар над тобой, стрелка прицела и оранжевая линия/кольцо к тому, кому уйдёт пас.</summary>
    void UpdateIndicators()
    {
        bool show = InMatch && controlled != null && phase != Phase.Over;
        marker.gameObject.SetActive(show);
        aimArrow.gameObject.SetActive(show);
        Player target = null;
        if (show)
        {
            Vector3 pos = controlled.Position;
            marker.position = pos + Vector3.up * 2.4f;
            aimArrow.position = pos + controlled.Aim * 1.4f + Vector3.up * 0.03f;
            aimArrow.rotation = Quaternion.LookRotation(controlled.Aim);
            if (controlled.HasBall || IsTaker(controlled)) target = FindPassTarget(controlled, controlled.Aim, 70f, false);
        }
        passRing.gameObject.SetActive(target != null);
        passLine.gameObject.SetActive(target != null);
        if (target != null)
        {
            Vector3 from = new Vector3(ball.transform.position.x, 0.03f, ball.transform.position.z);
            Vector3 to = target.Position + Vector3.up * 0.03f;
            passRing.position = target.Position + Vector3.up * 0.02f;
            passLine.position = (from + to) * 0.5f;
            passLine.rotation = Quaternion.LookRotation(to - from);
            passLine.localScale = new Vector3(0.08f, 0.01f, Vector3.Distance(from, to));
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
        }
        if (phase == Phase.Over)
            UI.Label(new Rect(0, 1080 * 0.36f + 110, w, 40), "Возврат в меню…  (A / Enter — сразу)", UI.Body, 22, Color.white, TextAnchor.MiddleCenter);

        // Карточка твоего игрока снизу слева
        if (controlled != null)
        {
            UI.Box(new Rect(40, 950, 380, 60), new Color(0.06f, 0.07f, 0.09f, 0.9f));
            UI.Box(new Rect(40, 950, 60, 60), Catalog.Kits[prof.kit].color);
            UI.Label(new Rect(115, 950, 220, 60), controlled.displayName.ToUpper(), UI.Head, 28, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(300, 950, 110, 60), IsTaker(controlled) ? "СТАНДАРТ" : "НАП", UI.Body, 20, UI.Lime, TextAnchor.MiddleRight);
            UI.Label(new Rect(40, 1012, 600, 30),
                $"СКО {60 + prof.speedLvl * 7}   УДР {58 + prof.shotLvl * 7}   КОН {62 + prof.controlLvl * 7}",
                UI.Body, 20, new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleLeft);

            // Шкала силы удара
            if (controlled.charge > 0f)
            {
                UI.Box(new Rect(40, 910, 380, 26), new Color(0f, 0f, 0f, 0.7f));
                UI.Box(new Rect(43, 913, 374 * controlled.charge, 20), Color.Lerp(UI.Lime, new Color(1f, 0.3f, 0.2f), controlled.charge));
            }
        }

        // Соперник с мячом — снизу справа
        Player owner = ball.Owner;
        if (owner != null && owner.team != HumanTeam)
        {
            UI.Box(new Rect(w - 420, 950, 380, 60), new Color(0.06f, 0.07f, 0.09f, 0.9f));
            UI.Label(new Rect(w - 405, 950, 300, 60), owner.displayName.ToUpper(), UI.Head, 28, Color.white, TextAnchor.MiddleLeft);
            UI.Label(new Rect(w - 160, 950, 110, 60), "С МЯЧОМ", UI.Body, 20, new Color(1f, 0.55f, 0.1f), TextAnchor.MiddleRight);
        }

        if (prof.showHints && !paused)
        {
            string hint = GameInput.UsingGamepad
                ? (owner != null && owner.team != HumanTeam
                    ? "B — отбор   X — подкат   A (держать) — опека   RB — прессинг партнёра   Y — выход вратаря   LB — смена"
                    : "A — пас   X — навес   Y — пас на ход   B — удар   RT — спринт   LB / правый стик — смена   Start — пауза")
                : (owner != null && owner.team != HumanTeam
                    ? "K — отбор   L — подкат   J (держать) — опека   E — прессинг партнёра   I — выход вратаря   Q — смена"
                    : "J — пас   L — навес   I — пас на ход   K — удар   Shift — спринт   Q — смена   Esc — пауза");
            UI.Label(new Rect(0, 1040, w, 34), hint, UI.Body, 20, new Color(1f, 1f, 1f, 0.8f), TextAnchor.MiddleCenter);
        }
    }
}
