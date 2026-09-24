using UnityEngine;

public enum Team { Red = 0, Blue = 1 }
public enum Role { Field, Keeper }
public enum PassKind { Ground, Driven, Lob, Through, LobThrough }

/// <summary>
/// Игрок-капсула. Один класс на всех: управление с геймпада/клавиатуры (раскладка FIFA 20),
/// ИИ полевого (прессинг, опека, открывание, забегания), ИИ вратаря (позиция, прыжки, отбивания) и стандарты.
/// Мячом владеет Ball (Ball.Owner); игрок двигается, бьёт, отбирает и «заказывает» положение мяча (укрывание).
/// Создаётся из кода в MatchManager.
/// </summary>
public class Player : MonoBehaviour
{
    [Header("Скорости, м/с")]
    public float aiSpeed = 5.5f;
    public float runSpeed = 6.2f;          // твой игрок
    public float sprintSpeed = 8f;         // твой игрок со спринтом (RT)
    public float keeperSpeed = 4.5f;
    public float accel = 35f;

    [Header("Выносливость (0…1)")]
    public float staminaDrain = 0.12f;     // в секунду спринта
    public float staminaRecover = 0.07f;   // в секунду без спринта

    [Header("Мяч")]
    public float controlRadius = 1.1f;     // в этом радиусе мяч «прилипает» к игроку
    public float shotMin = 14f, shotMax = 28f;
    public float chargeTime = 0.8f;
    public float passArriveSpeed = 8.5f;   // обычный пас приходит «в ноги» с этой скоростью (даже при коротком нажатии)
    public float drivenArriveSpeed = 11f;  // прострел (RB+A) — быстрее, но его сложнее принять
    public float aiShootDistance = 12f;
    [HideInInspector] public float touchBonus;        // прокачка «Контроль»: мягче первое касание
    [HideInInspector] public float shotAccuracy = 1f; // прокачка «Удар»: меньше разброс

    [Header("Отбор и вратарь")]
    public float slideSpeed = 10f, slideTime = 0.4f, slideRecover = 0.5f;
    public float tackleTime = 0.35f, tackleCooldownTime = 0.9f, tackleLunge = 8.5f, tackleRange = 2.2f;
    public float diveSpeed = 7f, diveTime = 0.4f, diveReach = 0.6f;
    [HideInInspector] public float keeperSkill = 0.85f;  // шанс, что вратарь успеет прыгнуть

    [Header("Состояние (заполняет MatchManager)")]
    public Team team;
    public Role role;
    public Vector3 homePos;
    public string displayName;

    [HideInInspector] public float charge;       // 0..1 — заряд удара (HUD)
    [HideInInspector] public float stamina = 1f; // 0..1

    MatchManager mm;
    Rigidbody rb;
    Vector3 desiredVel;
    Vector3 aim;
    float kickCooldown, lostTimer, thinkTimer, holdTimer;
    float tackleTimer, tackleCooldown, slideTimer, recoverTimer;
    bool tackleResolved;
    float diveTimer, diveCooldown, skillTimer, headerBuffer, openTimer;
    Vector3 slideDir, diveDir, skillVel, openTarget;
    float passBuffer, shotBuffer, bufferedCharge;
    PassKind bufferedPass;
    bool bufferedFinesse, shotArmed, headerShot;
    bool sprinting, shielding;

    // ------------------------------------------------------------ свойства для Ball / MatchManager

    public Vector3 Position => new Vector3(rb.position.x, 0f, rb.position.z);
    public Vector3 Velocity => new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
    public Vector3 Facing { get { Vector3 f = transform.forward; f.y = 0f; return f.normalized; } }
    public Vector3 Aim => aim;
    public bool IsControlled => mm.controlled == this;
    public bool HasBall => Ball.Owner == this;
    public bool JustKicked => kickCooldown > 0f;
    public bool Tackling => tackleTimer > 0f;
    public bool Sliding => slideTimer > 0f;
    public bool Diving => diveTimer > 0f;
    public bool IsSprinting => sprinting;
    public bool Shielding => shielding && HasBall;
    public float ShieldBonus => (Shielding ? 0.4f : 0f) + (skillTimer > 0f ? 0.5f : 0f);
    public bool CanControlBall => kickCooldown <= 0f && lostTimer <= 0f && slideTimer <= 0f && recoverTimer <= 0f;
    bool CanKick => HasBall || mm.IsTaker(this);

    /// <summary>При укрывании мяч держится со стороны, противоположной ближайшему сопернику.</summary>
    public Vector3 ShieldDir
    {
        get
        {
            Player opp = mm.NearestOpponent(this, out float d);
            if (opp == null || d > 3f) return Facing;
            Vector3 away = Position - opp.Position;
            return away.sqrMagnitude > 0.01f ? away.normalized : Facing;
        }
    }

    Ball Ball => mm.ball;
    Vector3 BallPos => new Vector3(mm.ball.Body.position.x, 0f, mm.ball.Body.position.z);
    Vector3 AttackDir => team == Team.Red ? Vector3.right : Vector3.left;
    float SprintSpeedNow => Mathf.Lerp(runSpeed, sprintSpeed, Mathf.Clamp01(stamina / 0.25f));   // уставший не ускоряется
    public static Team Opp(Team t) => t == Team.Red ? Team.Blue : Team.Red;
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    /// <summary>Приблизительно нормальная случайная величина (сумма трёх равномерных), σ ≈ 1.</summary>
    static float Gauss() => (Random.value + Random.value + Random.value - 1.5f) * 2f;

    public void Init(MatchManager m, Team t, Role r, Vector3 home, string name)
    {
        mm = m; team = t; role = r; homePos = home; displayName = name;
        if (r == Role.Keeper) controlRadius = 1.3f;

        rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 70f;
        rb.constraints = RigidbodyConstraints.FreezeRotation;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        GetComponent<Collider>().material = new PhysicsMaterial("Player")
        {
            dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        ResetTo(home, AttackDir);
    }

    /// <summary>Прокачка из профиля (только твоя команда).</summary>
    public void ApplyUpgrades(Profile p)
    {
        runSpeed += 0.25f * p.speedLvl;
        sprintSpeed += 0.3f * p.speedLvl;
        aiSpeed += 0.2f * p.speedLvl;
        keeperSpeed += 0.1f * p.speedLvl;
        shotMax += 1f * p.shotLvl;
        shotMin += 0.5f * p.shotLvl;
        shotAccuracy = 1f - 0.08f * p.shotLvl;
        controlRadius += 0.03f * p.controlLvl;
        touchBonus = 0.8f * p.controlLvl;
    }

    /// <summary>Сложность соперника: скорость и реакция вратаря.</summary>
    public void ApplyDifficulty(int level)
    {
        float mult = Catalog.DifficultySpeed[level];
        aiSpeed *= mult;
        keeperSpeed *= mult;
        keeperSkill = new[] { 0.55f, 0.75f, 0.9f }[level];
    }

    public void ResetTo(Vector3 p, Vector3 facing)
    {
        p.y = 1f;
        rb.position = p; transform.position = p;
        rb.linearVelocity = Vector3.zero;
        facing = Flat(facing).sqrMagnitude > 0.01f ? Flat(facing).normalized : AttackDir;
        Quaternion look = Quaternion.LookRotation(facing);
        rb.rotation = look; transform.rotation = look;
        desiredVel = Vector3.zero; aim = facing;
        charge = 0f; kickCooldown = 0f; lostTimer = 0f; passBuffer = 0f; shotBuffer = 0f; shotArmed = false;
        tackleTimer = 0f; slideTimer = 0f; recoverTimer = 0f; diveTimer = 0f; skillTimer = 0f; headerBuffer = 0f;
    }

    public void OnLostBall() => lostTimer = 0.4f;     // после отбора нельзя мгновенно вернуть мяч
    public void OnHeavyTouch() => lostTimer = 0.2f;   // мяч отскочил от ноги — секунду догоняем
    public void OnParry() { kickCooldown = 0.5f; diveTimer = 0f; recoverTimer = 0.4f; }

    // ------------------------------------------------------------ цикл

    void Update()
    {
        float dt = Time.deltaTime;
        kickCooldown -= dt; lostTimer -= dt; thinkTimer -= dt; openTimer -= dt;
        passBuffer -= dt; shotBuffer -= dt; headerBuffer -= dt;
        tackleTimer -= dt; tackleCooldown -= dt; recoverTimer -= dt; diveCooldown -= dt;
        holdTimer = HasBall ? holdTimer + dt : 0f;

        // Выносливость: спринт тратит, шаг восстанавливает
        stamina = sprinting && Velocity.magnitude > runSpeed * 0.9f
            ? Mathf.Max(0f, stamina - staminaDrain * dt)
            : Mathf.Min(1f, stamina + staminaRecover * dt);
        sprinting = false;
        shielding = false;

        if (mm.Stopped)
        {
            desiredVel = Vector3.zero; charge = 0f;
            slideTimer = 0f; diveTimer = 0f; skillTimer = 0f;
            return;
        }

        if (slideTimer > 0f) { SlideUpdate(dt); return; }
        if (diveTimer > 0f)
        {
            diveTimer -= dt;
            desiredVel = diveDir * diveSpeed;
            if (diveTimer <= 0f) recoverTimer = 0.45f;   // после прыжка — подняться с газона
            return;
        }
        if (recoverTimer > 0f) { desiredVel = Vector3.zero; return; }
        if (skillTimer > 0f) { skillTimer -= dt; desiredVel = skillVel; return; }   // финт: короткий рывок с мячом

        if (IsControlled) HumanUpdate();
        else if (mm.IsTaker(this)) AITakerUpdate();
        else if (role == Role.Keeper) KeeperUpdate();
        else if (!AIHeader()) FieldUpdate();

        // Отбор: выпад к мячу, как только дотянулся — исход
        if (Tackling && !tackleResolved)
        {
            desiredVel = (BallPos - Position).normalized * tackleLunge;
            if (Vector3.Distance(Position, BallPos) < controlRadius + 0.3f && Ball.Body.position.y < 1f) ResolveTackle();
        }
    }

    void FixedUpdate()
    {
        Vector3 v = rb.linearVelocity;
        Vector3 cur = Flat(v);
        // На высокой скорости резко развернуться нельзя: при смене направления разгон слабее (инерция)
        float turn = cur.sqrMagnitude > 1f && desiredVel.sqrMagnitude > 1f ? Vector3.Angle(cur, desiredVel) : 0f;
        float a = Sliding || Tackling || Diving ? accel * 4f : accel * Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(turn / 150f) * Mathf.Clamp01(cur.magnitude / sprintSpeed));
        Vector3 flat = Vector3.MoveTowards(cur, desiredVel, a * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector3(flat.x, v.y, flat.z);

        bool useAim = IsControlled || mm.IsTaker(this) || (role == Role.Keeper && HasBall) || skillTimer > 0f;
        Vector3 face = Sliding ? slideDir : useAim ? aim : flat;
        if (face.sqrMagnitude > 0.01f)
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, Quaternion.LookRotation(face), 14f * Time.fixedDeltaTime));
    }

    // ------------------------------------------------------------ твой игрок

    void HumanUpdate()
    {
        Vector3 dir = mm.CameraRelative(GameInput.Move());
        bool taker = mm.IsTaker(this);
        Player owner = Ball.Owner;
        bool defending = owner != null && owner.team != team;
        bool modifier = GameInput.Held(Btn.Modifier);   // RB / R1
        bool slow = GameInput.Held(Btn.Jockey);         // LT / L2

        if (taker) desiredVel = Vector3.zero;           // на стандарте стоим, стик только целится
        else
        {
            bool wantSprint = GameInput.Held(Btn.Sprint) && !slow;
            float spd = wantSprint ? SprintSpeedNow : runSpeed;
            sprinting = wantSprint && dir.sqrMagnitude > 0.1f;
            if (HasBall && (slow || modifier)) { shielding = true; spd = runSpeed * 0.5f; }  // укрывание корпусом
            else if (slow) spd = runSpeed * 0.55f;                                           // выжидание лицом к атаке
            desiredVel = dir * spd;

            if (defending && GameInput.Held(Btn.Pass))
                MoveTo(ContainPoint(owner), 1f);                         // A (держать): сдерживание лицом к лицу
            else if (!defending && dir.sqrMagnitude < 0.01f && mm.passReceiver == this)
                MoveTo(ReceivePoint(), 1f);                              // пас тебе — игрок сам выходит на мяч
        }
        if (dir.sqrMagnitude > 0.01f) aim = dir.normalized;
        if (slow && defending && (BallPos - Position).sqrMagnitude > 0.01f) aim = (BallPos - Position).normalized;

        if (defending && !taker)
        {
            // ---------------- ОБОРОНА
            if (GameInput.Down(Btn.Lob)) SlideTackle(aim);                 // X / Квадрат — подкат (как в FIFA)
            if (GameInput.Down(Btn.Shoot)) StandingTackle();               // B / Круг — отбор, толчок корпусом
            if (GameInput.Held(Btn.Through)) mm.RequestKeeperRush();       // Y / Треугольник — выход вратаря
            if (GameInput.Held(Btn.Modifier)) mm.RequestTeammatePress();   // RB / R1 — прессинг партнёра
            if (GameInput.Down(Btn.Switch)) mm.SwitchControl();            // LB / L1 — смена игрока
            else if (GameInput.RightStickFlick(out Vector2 rsd)) mm.SwitchControlDir(mm.CameraRelative(rsd));
            charge = 0f; passBuffer = 0f; shotBuffer = 0f; shotArmed = false;
            return;
        }

        // ---------------- АТАКА / СВОБОДНЫЙ МЯЧ
        if (GameInput.Down(Btn.Pass)) { passBuffer = 0.25f; bufferedPass = modifier ? PassKind.Driven : PassKind.Ground; headerBuffer = 0.3f; headerShot = false; }
        if (GameInput.Down(Btn.Lob)) { passBuffer = 0.25f; bufferedPass = PassKind.Lob; }
        if (GameInput.Down(Btn.Through)) { passBuffer = 0.25f; bufferedPass = modifier ? PassKind.LobThrough : PassKind.Through; }
        if (GameInput.Down(Btn.Shoot)) { shotArmed = true; headerBuffer = 0.3f; headerShot = true; }
        if (shotArmed && GameInput.Held(Btn.Shoot)) charge = Mathf.Min(1f, charge + Time.deltaTime / chargeTime);
        if (shotArmed && GameInput.Up(Btn.Shoot))
        {
            shotBuffer = 0.25f; bufferedCharge = charge; bufferedFinesse = modifier;
            charge = 0f; shotArmed = false;
        }

        // Мяч на уровне головы — удар/скидка головой
        if (headerBuffer > 0f && BallInHeaderZone())
        {
            headerBuffer = 0f; passBuffer = 0f; shotBuffer = 0f; shotArmed = false; charge = 0f;
            Header(headerShot, aim);
            return;
        }

        if (passBuffer > 0f && CanKick)
        {
            passBuffer = 0f;
            bool ok;
            switch (bufferedPass)
            {
                case PassKind.Lob: ok = TryLob(aim, false); break;
                case PassKind.LobThrough: ok = TryLob(aim, true); break;
                case PassKind.Through: ok = TryThrough(aim) || TryPass(aim, 70f, false); break;
                case PassKind.Driven: ok = TryPass(aim, 70f, true); break;
                default: ok = TryPass(aim, 70f, false); break;
            }
            if (!ok && taker) Kick(aim, 12f, 0.3f, null);   // на стандарте не застреваем, даже если некому
        }
        if (shotBuffer > 0f && CanKick)
        {
            shotBuffer = 0f;
            Shoot(aim, bufferedCharge, bufferedFinesse);
        }

        // LB / L1: с мячом — забегание партнёра, без мяча — смена игрока
        if (GameInput.Down(Btn.Switch))
        {
            if (HasBall || taker) mm.CallRun(this);
            else mm.SwitchControl();
        }
        // Правый стик: с мячом — финт, без мяча — смена игрока в направлении стика
        if (GameInput.RightStickFlick(out Vector2 rs))
        {
            Vector3 d = mm.CameraRelative(rs);
            if (HasBall) SkillMove(d);
            else if (!taker) mm.SwitchControlDir(d);
        }
    }

    /// <summary>
    /// Финты правым стиком (относительно направления взгляда):
    /// вперёд — толчок мяча на ход и рывок; назад — протяжка с разворотом; в сторону — уход с мячом в сторону.
    /// Во время финта мяч сложнее отобрать.
    /// </summary>
    void SkillMove(Vector3 d)
    {
        if (d.sqrMagnitude < 0.01f) return;
        Vector3 f = Facing;
        float fwd = Vector3.Dot(d.normalized, f);
        if (fwd > 0.6f)
        {
            Kick(f, runSpeed + 5f, 0.1f, this);               // мяч вперёд на ход, сам его догоняешь
            skillTimer = 0.35f;
            skillVel = f * SprintSpeedNow;
            mm.Flash("ТОЛЧОК НА ХОД");
        }
        else if (fwd < -0.6f)
        {
            aim = -f;                                          // протяжка: разворот с мячом на 180°
            skillTimer = 0.3f;
            skillVel = -f * 2f;
            mm.Flash("ПРОТЯЖКА");
        }
        else
        {
            Vector3 side = Flat(d).normalized;
            aim = (side * 0.8f + f * 0.2f).normalized;          // уход в сторону (ролл / ножницы)
            skillTimer = 0.35f;
            skillVel = side * 7f;
            mm.Flash("ФИНТ");
        }
    }

    // ------------------------------------------------------------ ИИ полевого

    void FieldUpdate()
    {
        if (HasBall) { AIWithBall(); return; }

        Player owner = Ball.Owner;
        bool weHaveBall = owner != null && owner.team == team;
        bool oppHasBall = owner != null && owner.team != team;
        bool pressing = !weHaveBall && (mm.IsChaser(this) || mm.IsPressHelper(this));
        Vector3 target;
        float mul = 0.85f;

        if (mm.passReceiver == this)
        {
            target = ReceivePoint();                        // пас идёт мне — выхожу на траекторию / место приземления
            mul = 1.1f;
        }
        else if (mm.IsRunner(this))
        {
            target = Position + AttackDir * 6f + new Vector3(0f, 0f, -Position.z * 0.3f);  // забегание за спину защитникам
            mul = 1.15f;
        }
        else if (mm.SetPieceActive)
        {
            if (mm.WallSpot(this, out Vector3 wall)) target = wall;                 // стенка на штрафном
            else
            {
                target = mm.FormationPos(this, mm.SetPieceTeam == team);
                if (mm.SetPieceTeam != team) target = KeepAway(target, mm.SetPieceSpot, 4f);
                target = mm.KeepOutOfPenaltyBox(target);                            // на пенальти — все за штрафной
            }
        }
        else if (pressing)
        {
            target = Ball.Grounded ? BallPos + Flat(Ball.Body.linearVelocity) * 0.25f : Ball.PredictRest();
            mul = Vector3.Distance(Position, target) > 6f ? 1.15f : 1f;             // далеко — спринтуем
            if (oppHasBall && thinkTimer <= 0f && tackleCooldown <= 0f)
            {
                thinkTimer = 0.3f;
                if (Vector3.Distance(Position, owner.Position) < 1.6f && Random.value < mm.AiTackleChance) StandingTackle();
            }
        }
        else if (oppHasBall && mm.MarkTarget(this, out Vector3 markPos))
        {
            target = Vector3.Lerp(mm.FormationPos(this, false), markPos, 0.7f);     // персональная опека, но держим строй
        }
        else if (weHaveBall)
        {
            target = OpenSpace();                                                   // открываемся под пас
        }
        else
        {
            target = mm.FormationPos(this, false);
        }

        MoveTo(mm.ClampToField(target, 0.3f), mul);
    }

    /// <summary>Открывание: из нескольких точек возле своей позиции выбираем самую свободную и с чистой линией паса.</summary>
    Vector3 OpenSpace()
    {
        if (openTimer > 0f) return openTarget;
        openTimer = 0.4f + Random.value * 0.2f;
        Vector3 basePos = mm.FormationPos(this, true);
        Vector3 ball = BallPos;
        float bestScore = float.MinValue;
        openTarget = basePos;
        for (int i = 0; i < 7; i++)
        {
            Vector3 c = i == 0 ? basePos : basePos + Quaternion.Euler(0f, i * 60f, 0f) * Vector3.forward * 3f;
            c = mm.ClampToField(c, 1f);
            float free = 6f, lane = 3f;
            foreach (var o in mm.players)
            {
                if (o.team == team) continue;
                free = Mathf.Min(free, Vector3.Distance(o.Position, c));
                lane = Mathf.Min(lane, MatchManager.DistanceToSegment(o.Position, ball, c));
            }
            float score = free + lane * 1.2f - Vector3.Distance(c, basePos) * 0.3f;
            if (score > bestScore) { bestScore = score; openTarget = c; }
        }
        return openTarget;
    }

    /// <summary>ИИ с мячом: ведёт к воротам, обходя соперника; бьёт вблизи; под прессингом — пасует.</summary>
    void AIWithBall()
    {
        Vector3 me = Position;
        Vector3 goal = mm.GoalOf(Opp(team));
        Vector3 toGoal = (goal - me).normalized;
        Player opp = mm.NearestOpponent(this, out float dOpp);

        Vector3 dir = toGoal;
        if (opp != null && dOpp < 4f)
            dir = (toGoal + Flat(me - opp.Position).normalized * (4f - dOpp) * 0.5f).normalized;
        desiredVel = dir * aiSpeed * 0.85f;
        shielding = opp != null && dOpp < 1.3f && Random.value < 0.5f;   // вплотную — иногда укрывает мяч

        if (thinkTimer > 0f) return;
        thinkTimer = 0.2f + Random.value * 0.15f;

        if (Vector3.Distance(me, goal) < aiShootDistance && Vector3.Angle(Facing, goal - me) < 70f)
        {
            float spread = mm.goalWidth * 0.5f - 0.5f;
            Vector3 aimPt = goal + Vector3.forward * Random.Range(-spread, spread);
            Shoot(aimPt - BallPos, Random.Range(0.45f, 0.9f), Random.value < 0.3f);
            return;
        }
        if (dOpp < 2.2f || Random.value < 0.1f)
        {
            if (Random.value < 0.25f && TryThrough(toGoal)) return;
            TryPass(toGoal, 110f, false);
        }
    }

    /// <summary>ИИ-игра головой: у чужих ворот — удар, у своих — вынос/скидка.</summary>
    bool AIHeader()
    {
        if (role == Role.Keeper || !BallInHeaderZone()) return false;
        Vector3 goal = mm.GoalOf(Opp(team));
        if (Vector3.Distance(Position, goal) < 14f) Header(true, goal - Position);
        else Header(false, AttackDir + new Vector3(0f, 0f, Random.Range(-0.6f, 0.6f)));
        return true;
    }

    // ------------------------------------------------------------ ИИ вратаря

    void KeeperUpdate()
    {
        float gx = mm.OwnGoalX(team);
        float inField = -Mathf.Sign(gx);
        Vector3 fieldDir = new Vector3(inField, 0f, 0f);

        if (HasBall)
        {
            desiredVel = Vector3.zero;
            aim = fieldDir;
            if (holdTimer > 0.6f && !TryPass(fieldDir, 100f, false))
                Kick(new Vector3(inField, 0f, Random.Range(-0.6f, 0.6f)), Random.Range(16f, 21f), 3f, null);
            return;
        }

        float lineX = gx + inField * 0.8f;
        float half = mm.goalWidth * 0.5f;
        Vector3 b = BallPos, bv = Flat(Ball.Body.linearVelocity);

        // Пенальти/штрафной против нас — до удара стоим в центре ворот
        if (mm.SetPieceActive && mm.SetPieceTeam != team && mm.SetPieceKind == SetPieceType.Penalty)
        {
            MoveTo(new Vector3(gx + inField * 0.3f, 0f, 0f), 1f);
            return;
        }

        // Прыжок: удар идёт в створ, а вратарь не успевает дойти шагом
        if (diveCooldown <= 0f && bv.x * inField < -10f)
        {
            float t = (gx + inField * 0.3f - b.x) / bv.x;
            float zc = b.z + bv.z * t;
            if (t > 0.05f && t < 0.8f && Mathf.Abs(zc) < half + 0.3f)
            {
                diveCooldown = 1f;                                       // одно решение на удар
                float need = zc - Position.z;
                if (Mathf.Abs(need) > keeperSpeed * t + controlRadius * 0.8f && Random.value < keeperSkill)
                {
                    diveDir = new Vector3(0f, 0f, Mathf.Sign(need));
                    diveTimer = diveTime;
                    return;
                }
            }
        }

        // Y / Треугольник в обороне: твой вратарь выходит на мяч
        if (team == MatchManager.HumanTeam && mm.KeeperRush) { MoveTo(b, 1.1f); return; }

        // 1) На линии ворот, смещаясь за мячом
        float z = b.z * 0.5f;
        // 2) Мяч летит в ворота — встаём в точку пересечения траектории с линией
        if (bv.x * inField < -2f)
        {
            float t = (lineX - b.x) / bv.x;
            if (t > 0f && t < 1.5f) z = b.z + bv.z * t;
        }
        Vector3 target = new Vector3(lineX, 0f, Mathf.Clamp(z, -half + 0.4f, half - 0.4f));

        // 3) Медленный свободный мяч в штрафной — выходим и забираем
        bool inBox = Mathf.Abs(b.x - gx) < mm.boxDepth && Mathf.Abs(b.z) < mm.boxHalfWidth;
        if (inBox && bv.magnitude < 6f && !mm.SetPieceActive && Ball.Owner == null) target = b;

        MoveTo(target, 1f);
    }

    // ------------------------------------------------------------ ИИ на стандарте

    void AITakerUpdate()
    {
        desiredVel = Vector3.zero;
        Vector3 goal = mm.GoalOf(Opp(team));
        Vector3 toGoal = (goal - BallPos).normalized;
        aim = toGoal;
        if (mm.SetPieceTime < 1f) return;                  // пауза, чтобы все успели расставиться

        float half = mm.goalWidth * 0.5f - 0.4f;
        switch (mm.SetPieceKind)
        {
            case SetPieceType.Penalty:
                Shoot(goal + Vector3.forward * (Random.value < 0.5f ? -half : half) - BallPos, Random.Range(0.55f, 0.85f), false);
                return;
            case SetPieceType.FreeKick:
                if (Vector3.Distance(BallPos, goal) < 16f)
                {
                    Shoot(goal + Vector3.forward * Random.Range(-half, half) - BallPos, Random.Range(0.5f, 0.8f), true);
                    return;
                }
                break;
            case SetPieceType.Corner:
                if (TryLob(toGoal, false)) return;          // угловой — навес в штрафную
                break;
        }
        if (!TryPass(toGoal, 180f, false))
            Kick(toGoal, 16f, 2f, null);
    }

    // ------------------------------------------------------------ движение

    void MoveTo(Vector3 target, float speedMul)
    {
        float speed = role == Role.Keeper ? keeperSpeed : IsControlled ? runSpeed : aiSpeed;
        if (speedMul > 1f)
        {
            sprinting = true;                                                   // ИИ тоже устаёт от рывков
            speedMul = Mathf.Lerp(1f, speedMul, Mathf.Clamp01(stamina / 0.25f));
        }
        Vector3 d = Flat(target - transform.position);
        float dist = d.magnitude;
        desiredVel = dist < 0.15f ? Vector3.zero : d / dist * speed * speedMul * Mathf.Clamp01(dist * 1.5f);
    }

    /// <summary>Куда бежать за пасом: мяч в воздухе — к месту приземления, по земле — на траекторию.</summary>
    Vector3 ReceivePoint()
    {
        if (!Ball.Grounded && Ball.Body.position.y > 0.9f) return Ball.PredictRest();
        Vector3 b = BallPos, v = Flat(Ball.Body.linearVelocity);
        if (v.sqrMagnitude < 1f) return b;
        Vector3 dir = v.normalized;
        return b + dir * Mathf.Max(0f, Vector3.Dot(Position - b, dir));
    }

    /// <summary>Сдерживание: 1.3 м от соперника с мячом в сторону своих ворот.</summary>
    Vector3 ContainPoint(Player owner)
    {
        Vector3 toOwnGoal = (mm.GoalOf(team) - owner.Position).normalized;
        return owner.Position + toOwnGoal * 1.3f;
    }

    static Vector3 KeepAway(Vector3 target, Vector3 spot, float radius)
    {
        Vector3 d = Flat(target - spot);
        if (d.magnitude >= radius) return target;
        if (d.sqrMagnitude < 0.01f) d = Vector3.forward;
        return spot + d.normalized * radius;
    }

    // ------------------------------------------------------------ отбор

    /// <summary>Отбор (B / Круг): выпад к мячу. Если дотянулся — исход решается сразу (см. ResolveTackle).</summary>
    void StandingTackle()
    {
        if (tackleCooldown > 0f) return;
        if (Vector3.Distance(Position, BallPos) > tackleRange + controlRadius) return;   // слишком далеко — не тратим отбор
        tackleTimer = tackleTime;
        tackleCooldown = tackleCooldownTime;
        tackleResolved = false;
    }

    /// <summary>
    /// Исход отбора: спереди шанс выше, сзади и против укрывающего мяч — ниже. Удачный отбор чаще забирает мяч,
    /// иногда просто выбивает его. Неудачный — игрок «проваливается» на мгновение.
    /// </summary>
    void ResolveTackle()
    {
        tackleResolved = true;
        Player owner = Ball.Owner;
        if (owner == null) { if (!Ball.Held) Ball.ForceOwner(this); return; }   // мяч ничей — просто забрали
        if (owner.team == team) return;

        bool fromBehind = Vector3.Dot(owner.Facing, (Position - owner.Position).normalized) < -0.3f;
        float chance = 0.8f - (owner.Shielding ? 0.25f : 0f) - (fromBehind ? 0.2f : 0f)
                     - (owner.IsSprinting ? 0f : 0.05f)
                     + (team == MatchManager.HumanTeam ? 0.05f : 0f);
        if (Random.value < chance)
        {
            if (Random.value < 0.7f) Ball.ForceOwner(this);
            else Kick((BallPos - owner.Position).normalized + Random.insideUnitSphere * 0.5f, 6f, 0.3f, null);  // выбил
            if (IsControlled) mm.Flash("ОТБОР!");
        }
        else
        {
            recoverTimer = 0.35f;                          // промах — секунду теряешь равновесие
            if (IsControlled) mm.Flash("МИМО");
        }
        tackleTimer = 0f;
    }

    void SlideTackle(Vector3 dir)
    {
        if (tackleCooldown > 0f || dir.sqrMagnitude < 0.01f) return;
        slideDir = Flat(dir).normalized;
        slideTimer = slideTime;
        tackleCooldown = tackleCooldownTime + slideRecover;
    }

    /// <summary>Подкат: едем по инерции. Сначала мяч — чисто выбили; сначала соперник (обычно сзади) — фол.</summary>
    void SlideUpdate(float dt)
    {
        slideTimer -= dt;
        desiredVel = slideDir * slideSpeed;
        if (!Ball.Held && Vector3.Distance(Position, BallPos) < 1.2f && Ball.Body.position.y < 1f)
        {
            Vector3 poke = (slideDir + new Vector3(Random.Range(-0.3f, 0.3f), 0f, Random.Range(-0.3f, 0.3f))).normalized;
            Kick(poke, 9f, 0.6f, null);
            slideTimer = 0f;
        }
        else
        {
            foreach (var o in mm.players)
            {
                if (o.team == team || Vector3.Distance(o.Position, Position) > 0.85f) continue;
                mm.OnFoul(this, o);
                slideTimer = 0f;
                break;
            }
        }
        if (slideTimer <= 0f) recoverTimer = slideRecover;
    }

    // ------------------------------------------------------------ удары и пасы

    bool BallInHeaderZone()
    {
        if (Ball.Owner != null || Ball.Held || kickCooldown > 0f) return false;
        float y = Ball.Body.position.y;
        return y > 0.9f && y < 2.5f && Vector3.Distance(Position, BallPos) < 1.1f;
    }

    /// <summary>Игра головой: удар в створ (если ворота рядом) или скидка/вынос в направлении.</summary>
    void Header(bool shot, Vector3 dir)
    {
        Vector3 goal = mm.GoalOf(Opp(team));
        Vector3 toGoal = goal - BallPos;
        if (shot && toGoal.magnitude < 16f && Vector3.Angle(dir, toGoal) < 60f)
        {
            float half = mm.goalWidth * 0.5f - 0.4f;
            Vector3 aimPt = goal + Vector3.forward * Mathf.Clamp(Random.Range(-half, half) * 0.8f, -half, half);
            Kick(aimPt - BallPos, Random.Range(12f, 16f), Random.Range(-1f, 1f), null);
            mm.Flash("ГОЛОВОЙ!");
            return;
        }
        Player mate = mm.FindPassTarget(this, dir, 70f, true);
        if (mate != null)
        {
            Vector3 d = mate.Position - BallPos;
            Kick(d, Mathf.Clamp(d.magnitude * 0.8f + 4f, 7f, 15f), 2f, mate);
        }
        else Kick(dir, 12f, 2.5f, null);
    }

    /// <summary>
    /// Удар. Автонаводка в створ (±35°), сила и высота от заряда.
    /// Разброс растёт от силы удара, спринта, прессинга и разворота (как в FIFA: «перебор силы» — выше ворот).
    /// Закрученный (RB+B): слабее, точнее, мяч стартует наружу и Магнус заворачивает его в угол.
    /// </summary>
    void Shoot(Vector3 dir, float power01, bool finesse)
    {
        Vector3 goal = mm.GoalOf(Opp(team));
        Vector3 toGoal = goal - BallPos;
        float half = mm.goalWidth * 0.5f - 0.4f;
        Vector3 target = BallPos + Flat(dir).normalized * 20f;
        bool onTarget = Vector3.Angle(dir, toGoal) < 35f && toGoal.magnitude < 28f && Mathf.Abs(dir.x) > 0.1f;
        if (onTarget)
        {
            float zHit = BallPos.z + dir.z * (goal.x - BallPos.x) / dir.x;
            target = new Vector3(goal.x, 0f, Mathf.Clamp(zHit, -half, half));
        }
        Vector3 shot = target - BallPos;
        float dist = shot.magnitude;

        float power = finesse ? Mathf.Lerp(shotMin, shotMax * 0.8f, power01) : Mathf.Lerp(shotMin, shotMax, power01);
        float lift = finesse ? Mathf.Lerp(1.5f, 4f, power01) : Mathf.Lerp(0.5f, 4.5f, power01);

        mm.NearestOpponent(this, out float dOpp);
        float err = 1.5f + 6f * power01 * power01
                  + (sprinting ? 3f : 0f)
                  + (dOpp < 1.5f ? 3f : 0f)
                  + (Vector3.Angle(Facing, shot) > 60f ? 4f : 0f);
        err *= shotAccuracy * (finesse ? 0.5f : 1f);

        float side = 0f;
        if (finesse)
        {
            // Мяч стартует наружу от центра ворот, вращение возвращает его в цель.
            // Боковое смещение от Магнуса ≈ ½·k·spin·d² (не зависит от скорости) → нужный угол старта.
            const float spin = 0.8f;
            Vector3 right = Vector3.Cross(Vector3.up, shot.normalized);
            float outward = Mathf.Sign(Vector3.Dot(right, target - goal) + 0.001f);
            float angle = Mathf.Atan(0.5f * Ball.magnusK * spin * dist) * Mathf.Rad2Deg;
            shot = Quaternion.Euler(0f, angle * outward, 0f) * shot;
            side = -outward * spin;
        }

        shot = Quaternion.Euler(0f, Gauss() * err, 0f) * shot;
        lift += Gauss() * err * 0.12f + power01 * power01 * 0.6f;   // сильный удар чаще уходит выше
        Kick(shot, power, Mathf.Max(lift, 0f), null, side, finesse ? 0f : -0.2f);
        if (finesse) mm.Flash("ЗАКРУЧЕННЫЙ");
        if (power > 24f) mm.Shake(0.08f);
    }

    /// <summary>Пас низом (A) или прострел (RB+A) лучшему партнёру в секторе. Сила — чтобы мяч пришёл «в ноги».</summary>
    bool TryPass(Vector3 prefDir, float maxAngle, bool driven)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, maxAngle, false);
        if (mate == null) return false;

        float arrive = driven ? drivenArriveSpeed : passArriveSpeed;
        Vector3 target = mate.Position;
        float power = arrive;
        for (int i = 0; i < 2; i++)
        {
            // v0² = v1² + 2·a·d — с трением качения мяч придёт к партнёру со скоростью arrive.
            // +1.5 м запаса: мяч стартует перед пасующим и должен дойти «в ноги», а не остановиться перед ними.
            float d = Vector3.Distance(BallPos, target) + 1.5f;
            power = Mathf.Clamp(Mathf.Sqrt(arrive * arrive + 2f * Ball.rollDecel * d), 10f, driven ? 28f : 24f);
            float t = d / ((power + arrive) * 0.5f);
            target = mate.Position + mate.Velocity * Mathf.Min(t, 1.2f);
        }
        Vector3 dir = target - BallPos;
        mm.NearestOpponent(this, out float dOpp);
        float err = (0.5f + dir.magnitude * 0.05f + (dOpp < 1.5f ? 1.5f : 0f) + (driven ? 1f : 0f))
                  * (team == MatchManager.HumanTeam ? 1f - 0.05f * Profile.Current.controlLvl : 1f);
        dir = Quaternion.Euler(0f, Gauss() * err, 0f) * dir;
        Kick(dir, power, 0f, mate);                     // пас строго по земле — без подскоков, которые гасят скорость
        return true;
    }

    /// <summary>Пас на ход (Y): мяч уходит в свободную зону в 5 м перед партнёром.</summary>
    bool TryThrough(Vector3 prefDir)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, 70f, false);
        if (mate == null) return false;

        Vector3 run = mate.Velocity.sqrMagnitude > 1f
            ? (mate.AttackDir * 0.6f + mate.Velocity.normalized * 0.4f).normalized
            : mate.AttackDir;
        Vector3 target = mm.ClampToField(mate.Position + run * 5f, 1f);
        float d = Vector3.Distance(BallPos, target);
        const float arrive = 5f;
        float power = Mathf.Clamp(Mathf.Sqrt(arrive * arrive + 2f * Ball.rollDecel * (d + 1f)), 10f, 22f);
        Kick(target - BallPos, power, 0f, mate);
        return true;
    }

    /// <summary>
    /// Навес / длинный пас / перевод (X) или пас на ход навесом (RB+Y). Мяч летит по дуге над игроками
    /// с лёгким обратным вращением; скорость рассчитана на время полёта с поправкой на сопротивление воздуха.
    /// </summary>
    bool TryLob(Vector3 prefDir, bool throughBall)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, 70f, true, 36f);
        Vector3 target = mate != null
            ? mate.Position + (throughBall ? mate.AttackDir * 5f : mate.Velocity * 0.8f)
            : BallPos + Flat(prefDir).normalized * 14f;
        target = mm.ClampToField(target, 0.5f);
        Vector3 d = target - BallPos;
        float dist = Mathf.Min(d.magnitude, 36f);
        float flight = Mathf.Clamp(0.55f + dist * 0.045f, 0.7f, 1.8f);
        float horiz = dist / flight * (1f + 0.5f * Ball.dragK * dist);   // сопротивление воздуха «съедает» дальность
        const float backspin = 0.15f;
        float g = 9.81f - Ball.magnusK * backspin * horiz * horiz * 0.5f; // обратное вращение немного держит мяч в воздухе
        Kick(d, horiz, Mathf.Max(g, 6f) * flight * 0.5f, mate, 0f, backspin);
        return true;
    }

    void Kick(Vector3 dir, float power, float lift, Player receiver, float side = 0f, float top = 0f)
    {
        Ball.Kick(dir, power, lift, this, side, top);
        kickCooldown = 0.3f;   // не «подбираем» и не блокируем свой же удар
        aim = Flat(dir).normalized;
        mm.OnKick(this, receiver, lift > 3f);
    }
}
