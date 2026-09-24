using UnityEngine;

public enum Team { Red = 0, Blue = 1 }
public enum Role { Field, Keeper }
public enum PassKind { Ground, Lob, Through }

/// <summary>
/// Игрок-капсула. Один класс на всех: управление с клавиатуры/геймпада (если MatchManager отдал ему управление),
/// ИИ полевого, ИИ вратаря и розыгрыш стандартов. Мячом владеет не игрок, а Ball (см. Ball.Owner) —
/// игрок только двигается, поворачивается, бьёт и отбирает.
/// Создаётся из кода в MatchManager — руками на сцену вешать не нужно.
/// </summary>
public class Player : MonoBehaviour
{
    [Header("Скорости, м/с")]
    public float aiSpeed = 5.5f;
    public float runSpeed = 6.2f;          // твой игрок
    public float sprintSpeed = 8f;         // твой игрок со спринтом
    public float keeperSpeed = 4.5f;
    public float accel = 35f;

    [Header("Мяч")]
    public float controlRadius = 0.95f;    // в этом радиусе игрок может взять мяч
    public float shotMin = 14f, shotMax = 28f;
    public float chargeTime = 0.8f;        // время полного заряда удара
    public float passArriveSpeed = 7f;     // с какой скоростью пас приходит к партнёру
    public float aiShootDistance = 12f;

    [Header("Отбор")]
    public float slideSpeed = 10f, slideTime = 0.4f, slideRecover = 0.5f;
    public float tackleTime = 0.25f, tackleCooldownTime = 0.8f;

    [Header("Состояние (заполняет MatchManager)")]
    public Team team;
    public Role role;
    public Vector3 homePos;
    public string displayName;

    [HideInInspector] public float charge; // 0..1 — заряд удара (HUD)

    MatchManager mm;
    Rigidbody rb;
    Vector3 desiredVel;
    Vector3 aim;                           // направление взгляда/удара твоего игрока
    float kickCooldown, lostTimer, thinkTimer, holdTimer;
    float tackleTimer, tackleCooldown, slideTimer, recoverTimer;
    Vector3 slideDir;
    float passBuffer, shotBuffer, bufferedCharge;   // буфер нажатий: можно нажать чуть раньше, чем мяч пришёл
    PassKind bufferedPass;
    bool shotArmed;

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
    public bool CanControlBall => kickCooldown <= 0f && lostTimer <= 0f && slideTimer <= 0f && recoverTimer <= 0f;
    bool CanKick => HasBall || mm.IsTaker(this);

    Ball Ball => mm.ball;
    Vector3 BallPos => new Vector3(mm.ball.Body.position.x, 0f, mm.ball.Body.position.z);
    Vector3 AttackDir => team == Team.Red ? Vector3.right : Vector3.left;
    public static Team Opp(Team t) => t == Team.Red ? Team.Blue : Team.Red;
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    public void Init(MatchManager m, Team t, Role r, Vector3 home, string name)
    {
        mm = m; team = t; role = r; homePos = home; displayName = name;
        if (r == Role.Keeper) controlRadius = 1.2f;   // вратарю «руки длиннее»

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
        controlRadius += 0.03f * p.controlLvl;
    }

    /// <summary>Сложность соперника: множитель скорости.</summary>
    public void ApplyDifficulty(float mult)
    {
        aiSpeed *= mult;
        keeperSpeed *= mult;
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
        tackleTimer = 0f; slideTimer = 0f; recoverTimer = 0f;
    }

    public void OnLostBall() => lostTimer = 0.4f;   // после отбора нельзя мгновенно вернуть мяч

    // ------------------------------------------------------------ цикл

    void Update()
    {
        float dt = Time.deltaTime;
        kickCooldown -= dt; lostTimer -= dt; thinkTimer -= dt;
        passBuffer -= dt; shotBuffer -= dt;
        tackleTimer -= dt; tackleCooldown -= dt; recoverTimer -= dt;
        holdTimer = HasBall ? holdTimer + dt : 0f;

        if (mm.Stopped) { desiredVel = Vector3.zero; charge = 0f; slideTimer = 0f; return; }  // гол, аут, пауза — все стоят

        // Подкат: едем по инерции, по пути выбиваем мяч; потом короткое «вставание»
        if (slideTimer > 0f)
        {
            slideTimer -= dt;
            desiredVel = slideDir * slideSpeed;
            if (!Ball.Held && Vector3.Distance(Position, BallPos) < 1.2f && Ball.Body.position.y < 1f)
            {
                Vector3 poke = (slideDir + new Vector3(Random.Range(-0.3f, 0.3f), 0f, Random.Range(-0.3f, 0.3f))).normalized;
                Kick(poke, 9f, 0.6f, null);
                slideTimer = 0f;
            }
            if (slideTimer <= 0f) recoverTimer = slideRecover;
            return;
        }
        if (recoverTimer > 0f) { desiredVel = Vector3.zero; return; }

        if (IsControlled) HumanUpdate();
        else if (mm.IsTaker(this)) AITakerUpdate();
        else if (role == Role.Keeper) KeeperUpdate();
        else FieldUpdate();

        // Отбор: короткий выпад к мячу
        if (Tackling) desiredVel = (BallPos - Position).normalized * 7.5f;
    }

    void FixedUpdate()
    {
        Vector3 v = rb.linearVelocity;
        float a = Sliding || Tackling ? accel * 4f : accel;
        Vector3 flat = Vector3.MoveTowards(Flat(v), desiredVel, a * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector3(flat.x, v.y, flat.z);

        // Твой игрок, исполнитель стандарта и вратарь с мячом смотрят по aim, остальные — куда бегут
        bool useAim = IsControlled || mm.IsTaker(this) || (role == Role.Keeper && HasBall);
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
        bool jockey = GameInput.Held(Btn.Jockey);

        if (taker) desiredVel = Vector3.zero;   // на стандарте стоим, стик/WASD только крутит направление
        else
        {
            float spd = GameInput.Held(Btn.Sprint) && !jockey ? sprintSpeed : runSpeed;
            if (jockey) spd *= 0.55f;           // LT: медленно, лицом к мячу (укрывание / защитная стойка)
            desiredVel = dir * spd;

            if (defending && GameInput.Held(Btn.Pass))
                MoveTo(ContainPoint(owner), 1f);                  // A (удерживать): опека — держимся между мячом и воротами
            else if (!defending && dir.sqrMagnitude < 0.01f && mm.passReceiver == this)
                MoveTo(InterceptPoint(), 1f);                     // пас летит тебе — игрок сам выходит на мяч
        }
        if (dir.sqrMagnitude > 0.01f) aim = dir.normalized;
        if (jockey && defending && (BallPos - Position).sqrMagnitude > 0.01f) aim = (BallPos - Position).normalized;

        if (defending && !taker)
        {
            // ---- защита (раскладка FIFA)
            if (GameInput.Down(Btn.Shoot)) StandingTackle();       // B / Круг — отбор
            if (GameInput.Down(Btn.Lob)) SlideTackle(aim);         // X / Квадрат — подкат
            if (GameInput.Held(Btn.Through)) mm.RequestKeeperRush();       // Y / Треугольник — вратарь выходит
            if (GameInput.Held(Btn.TeamPress)) mm.RequestTeammatePress();  // RB / R1 — партнёр прессингует
            charge = 0f; passBuffer = 0f; shotBuffer = 0f; shotArmed = false;
        }
        else
        {
            // ---- атака
            if (GameInput.Down(Btn.Pass)) { passBuffer = 0.25f; bufferedPass = PassKind.Ground; }   // A — пас
            if (GameInput.Down(Btn.Lob)) { passBuffer = 0.25f; bufferedPass = PassKind.Lob; }      // X — навес
            if (GameInput.Down(Btn.Through)) { passBuffer = 0.25f; bufferedPass = PassKind.Through; } // Y — пас на ход
            // B — удар. Заряд идёт, только если кнопку нажали уже в атаке (после отбора на B удар сам не вылетит)
            if (GameInput.Down(Btn.Shoot)) shotArmed = true;
            if (shotArmed && GameInput.Held(Btn.Shoot)) charge = Mathf.Min(1f, charge + Time.deltaTime / chargeTime);
            if (shotArmed && GameInput.Up(Btn.Shoot)) { shotBuffer = 0.25f; bufferedCharge = charge; charge = 0f; shotArmed = false; }

            if (passBuffer > 0f && CanKick)
            {
                passBuffer = 0f;
                bool ok = bufferedPass == PassKind.Lob ? TryLob(aim)
                        : bufferedPass == PassKind.Through ? TryThrough(aim) || TryPass(aim, 70f)
                        : TryPass(aim, 70f);
                if (!ok && taker) Kick(aim, 12f, 0.3f, null);    // на стандарте не застреваем, даже если некому
            }
            if (shotBuffer > 0f && CanKick)
            {
                shotBuffer = 0f;
                Shoot(aim, bufferedCharge);
            }
        }

        if (GameInput.Down(Btn.Switch)) mm.SwitchControl();                          // LB / L1
        else if (GameInput.RightStickFlick(out Vector2 rs)) mm.SwitchControlDir(mm.CameraRelative(rs)); // флик правым стиком
    }

    // ------------------------------------------------------------ ИИ полевого

    void FieldUpdate()
    {
        if (HasBall) { AIWithBall(); return; }

        Player owner = Ball.Owner;
        bool weHaveBall = owner != null && owner.team == team;
        bool pressing = !weHaveBall && (mm.IsChaser(this) || mm.IsPressHelper(this));
        Vector3 target;
        float mul = 0.85f;

        if (mm.passReceiver == this)
        {
            target = InterceptPoint();                     // пас идёт мне — выхожу на траекторию
            mul = 1f;
        }
        else if (mm.SetPieceActive)
        {
            target = mm.FormationPos(this, mm.SetPieceTeam == team);
            if (mm.SetPieceTeam != team) target = KeepAway(target, mm.SetPieceSpot, 4f); // на чужом стандарте — 4 м от мяча
        }
        else if (pressing)
        {
            target = BallPos + Flat(Ball.Body.linearVelocity) * 0.25f;  // прессинг / бег к свободному мячу
            mul = 1f;
            // Рядом с соперником, у которого мяч, — иногда пробуем отобрать
            if (owner != null && owner.team != team && thinkTimer <= 0f && tackleCooldown <= 0f)
            {
                thinkTimer = 0.3f;
                if (Vector3.Distance(Position, owner.Position) < 1.6f && Random.value < mm.AiTackleChance) StandingTackle();
            }
        }
        else
        {
            target = mm.FormationPos(this, weHaveBall);    // держим позицию; в атаке — выше
        }

        MoveTo(mm.ClampToField(target, 0.3f), mul);
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

        if (thinkTimer > 0f) return;                       // решения — не каждый кадр
        thinkTimer = 0.2f + Random.value * 0.15f;

        if (Vector3.Distance(me, goal) < aiShootDistance && Vector3.Angle(Facing, goal - me) < 70f)
        {
            float spread = mm.goalWidth * 0.5f - 0.5f;
            Vector3 aimPt = goal + Vector3.forward * Random.Range(-spread, spread);
            Kick(aimPt - BallPos, Random.Range(18f, 25f), Random.Range(0.5f, 2.5f), null);
            return;
        }
        if (dOpp < 2.2f || Random.value < 0.1f)
            TryPass(toGoal, 110f);
    }

    // ------------------------------------------------------------ ИИ вратаря

    void KeeperUpdate()
    {
        float gx = mm.OwnGoalX(team);
        float inField = -Mathf.Sign(gx);
        Vector3 fieldDir = new Vector3(inField, 0f, 0f);

        if (HasBall)
        {
            // Поймал — чуть держит мяч и разыгрывает: пас своему или вынос в поле
            desiredVel = Vector3.zero;
            aim = fieldDir;
            if (holdTimer > 0.6f && !TryPass(fieldDir, 100f))
                Kick(new Vector3(inField, 0f, Random.Range(-0.6f, 0.6f)), Random.Range(16f, 21f), 3f, null);
            return;
        }

        Vector3 b = BallPos, bv = Flat(Ball.Body.linearVelocity);

        // Y / Треугольник в защите: твой вратарь выходит на мяч
        if (team == MatchManager.HumanTeam && mm.KeeperRush) { MoveTo(b, 1f); return; }

        float lineX = gx + inField * 0.8f;
        float half = mm.goalWidth * 0.5f;

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
        if (inBox && bv.magnitude < 6f && !mm.SetPieceActive) target = b;

        MoveTo(target, 1f);
    }

    // ------------------------------------------------------------ ИИ на стандарте

    void AITakerUpdate()
    {
        desiredVel = Vector3.zero;
        Vector3 toGoal = (mm.GoalOf(Opp(team)) - BallPos).normalized;
        aim = toGoal;
        if (mm.SetPieceTime < 1f) return;                  // пауза, чтобы все успели расставиться

        if (mm.SetPieceKind == SetPieceType.Corner && TryLob(toGoal)) return;   // угловой — навес в штрафную
        if (!TryPass(toGoal, 180f))
            Kick(toGoal, 16f, 2f, null);
    }

    // ------------------------------------------------------------ действия

    void MoveTo(Vector3 target, float speedMul)
    {
        float speed = role == Role.Keeper ? keeperSpeed : IsControlled ? runSpeed : aiSpeed;
        Vector3 d = Flat(target - transform.position);
        float dist = d.magnitude;
        desiredVel = dist < 0.15f ? Vector3.zero : d / dist * speed * speedMul * Mathf.Clamp01(dist * 1.5f);
    }

    /// <summary>Ближайшая к игроку точка на траектории катящегося мяча.</summary>
    Vector3 InterceptPoint()
    {
        Vector3 b = BallPos, v = Flat(Ball.Body.linearVelocity);
        if (v.sqrMagnitude < 1f) return b;
        Vector3 dir = v.normalized;
        return b + dir * Mathf.Max(0f, Vector3.Dot(Position - b, dir));
    }

    /// <summary>Точка опеки: 1.3 м от соперника с мячом в сторону своих ворот.</summary>
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

    void StandingTackle()
    {
        if (tackleCooldown > 0f) return;
        tackleTimer = tackleTime;
        tackleCooldown = tackleCooldownTime;
    }

    void SlideTackle(Vector3 dir)
    {
        if (tackleCooldown > 0f || dir.sqrMagnitude < 0.01f) return;
        slideDir = Flat(dir).normalized;
        slideTimer = slideTime;
        tackleCooldown = tackleCooldownTime + slideRecover;
    }

    /// <summary>
    /// Удар по воротам. Если целишься примерно в ворота (±35°) — автонаводка: мяч летит в точку створа,
    /// куда указывает прицел, но не дальше штанг. Сила и высота зависят от заряда.
    /// </summary>
    void Shoot(Vector3 dir, float power01)
    {
        Vector3 goal = mm.GoalOf(Opp(team));
        Vector3 toGoal = goal - BallPos;
        if (Vector3.Angle(dir, toGoal) < 35f && toGoal.magnitude < 28f && Mathf.Abs(dir.x) > 0.1f)
        {
            float half = mm.goalWidth * 0.5f - 0.4f;
            float zHit = BallPos.z + dir.z * (goal.x - BallPos.x) / dir.x;
            dir = new Vector3(goal.x, 0f, Mathf.Clamp(zHit, -half, half)) - BallPos;
        }
        Kick(dir, Mathf.Lerp(shotMin, shotMax, power01), Mathf.Lerp(0.5f, 4.5f, power01), null);
    }

    /// <summary>Пас низом лучшему партнёру в секторе prefDir ± maxAngle. Сила — чтобы мяч дошёл «в ноги».</summary>
    bool TryPass(Vector3 prefDir, float maxAngle)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, maxAngle, false);
        if (mate == null) return false;

        // Упреждение: куда партнёр добежит, пока катится мяч (2 итерации достаточно)
        Vector3 target = mate.Position;
        float power = passArriveSpeed;
        for (int i = 0; i < 2; i++)
        {
            float d = Vector3.Distance(BallPos, target);
            // v0² = v1² + 2·a·d — мяч с трением качения придёт к партнёру со скоростью passArriveSpeed
            power = Mathf.Clamp(Mathf.Sqrt(passArriveSpeed * passArriveSpeed + 2f * Ball.rollDecel * d), 8f, 24f);
            float t = d / ((power + passArriveSpeed) * 0.5f);
            target = mate.Position + mate.Velocity * Mathf.Min(t, 1.2f);
        }
        Kick(target - BallPos, power, 0.2f, mate);
        return true;
    }

    /// <summary>Пас на ход (Y / Треугольник): мяч уходит в свободную зону на 5 м перед партнёром.</summary>
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
        const float arrive = 4f;
        float power = Mathf.Clamp(Mathf.Sqrt(arrive * arrive + 2f * Ball.rollDecel * d), 8f, 22f);
        Kick(target - BallPos, power, 0.15f, mate);
        return true;
    }

    /// <summary>Навес / заброс (X / Квадрат): мяч по дуге над игроками к партнёру или в точку по прицелу.</summary>
    bool TryLob(Vector3 prefDir)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, 70f, true);
        Vector3 target = mate != null ? mate.Position + mate.Velocity * 0.8f : BallPos + Flat(prefDir).normalized * 14f;
        Vector3 d = target - BallPos;
        float dist = Mathf.Min(d.magnitude, 28f);
        float flight = Mathf.Clamp(0.55f + dist * 0.045f, 0.7f, 1.6f);   // время полёта
        // Горизонтальная скорость — пролететь dist за flight; вертикальная — чтобы упасть ровно через flight (g = 9.81)
        Kick(d, dist / flight, 9.81f * flight * 0.5f, mate);
        return true;
    }

    void Kick(Vector3 dir, float power, float lift, Player receiver)
    {
        Ball.Kick(dir, power, lift, this);
        kickCooldown = 0.3f;   // не «подбираем» и не блокируем свой же удар
        aim = Flat(dir).normalized;
        mm.OnKick(this, receiver);
    }
}
