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
    public float aiSpeed = 4.7f;
    public float runSpeed = 5f;            // твой игрок
    public float sprintSpeed = 7.4f;       // твой игрок со спринтом (RT)
    public float keeperSpeed = 3.6f;
    [Header("Движение: быстрый отклик, лёгкая инерция только на полном спринте")]
    public float accel = 32f;              // ускорение, м/с² (до бега ~0.2 с, до спринта ~0.35 с)
    public float braking = 55f;            // торможение, м/с²
    public float turnRateSlow = 2000f;     // скорость поворота на бегу, °/с (почти мгновенно)
    public float turnRateFast = 800f;      // на полном спринте, °/с — чуть шире дуга, но без «слоу-мо»
    public float animRunSpeed = 3.6f;      // с какой скоростью (м/с) «бежит» анимация бега Mixamo при воспроизведении 1×

    [Header("Выносливость (0…1)")]
    public float staminaDrain = 0.035f;    // в секунду спринта (полный запас — ~30 с непрерывного спринта)
    public float staminaRecover = 0.05f;   // в секунду без спринта

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
    public float diveSpeed = 6f, diveTime = 0.4f, diveReach = 0.4f;
    [HideInInspector] public float keeperSkill = 0.6f;     // шанс, что вратарь вообще прыгнет
    [HideInInspector] public float keeperReaction = 0.25f; // время реакции вратаря на удар, с

    [Header("Состояние (заполняет MatchManager)")]
    public Team team;
    public Role role;
    public Vector3 homePos;
    public string displayName;

    [HideInInspector] public float charge;       // 0..1 — заряд удара (HUD)
    [HideInInspector] public float passCharge;   // 0..1 — заряд навеса / паса на ход (HUD)
    public float passChargeTime = 0.9f;
    [HideInInspector] public float stamina = 1f; // 0..1

    MatchManager mm;
    Rigidbody rb;
    Transform model;                     // 3D-модель (если есть): анимируется по скорости
    Animator anim;
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    const float PlayerModelRunThreshold = 5.5f;   // порог «бег» в аниматоре (PlayerModelSetup.RunThreshold)
    Vector3 desiredVel;
    Vector3 aim;
    float kickCooldown, lostTimer, thinkTimer, holdTimer;
    float tackleTimer, tackleCooldown, slideTimer, recoverTimer;
    bool tackleResolved;
    float diveTimer, diveCooldown, skillTimer, headerBuffer, openTimer;
    float shotSeenAt = -1f, afterPassRunUntil, slideCooldown;
    float slideElapsed, slideVisualTimer, fallTimer;

    // Замах: удар/пас уходит не сразу, а через windupTimer — в это время мяч можно отобрать, и удар сорвётся
    struct PendingKick { public Vector3 dir; public float power, lift, side, top; public Player receiver; }
    PendingKick pending;
    bool hasPending;
    float windupTimer, nextWindup, followTimer;

    // Касания при ведении и борьба корпусами
    float dribblePhase, jostleTime;
    [HideInInspector] public float DribbleOffset;   // насколько мяч «оттолкнут» вперёд в этот момент (касание в ритм шагов)
    Vector3 slideDir, diveDir, skillVel, openTarget;
    float passBuffer, shotBuffer, bufferedCharge;
    PassKind bufferedPass;
    float bufferedPassPower = -1f;                // сила навеса/паса на ход (−1 — подбирается автоматически)
    bool bufferedFinesse, shotArmed, headerShot;
    bool lobArmed, throughArmed, throughLofted, groundArmed;
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
    float SprintSpeedNow => Mathf.Lerp(runSpeed, sprintSpeed, Mathf.Clamp01(stamina / 0.15f));   // совсем уставший не ускоряется
    public static Team Opp(Team t) => t == Team.Red ? Team.Blue : Team.Red;
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    /// <summary>Приблизительно нормальная случайная величина (сумма трёх равномерных), σ ≈ 1.</summary>
    static float Gauss() => (Random.value + Random.value + Random.value - 1.5f) * 2f;

    public void Init(MatchManager m, Team t, Role r, Vector3 home, string name)
    {
        mm = m; team = t; role = r; homePos = home; displayName = name;
        if (r == Role.Keeper) controlRadius = 1.0f;
        var capsule = GetComponent<CapsuleCollider>();
        if (capsule != null) capsule.radius = 0.35f;   // игроки «уже», чем капсула по умолчанию: можно подойти вплотную к мячу

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
        keeperSkill = new[] { 0.35f, 0.55f, 0.75f }[level];
        keeperReaction = new[] { 0.32f, 0.25f, 0.18f }[level];
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
        slideVisualTimer = 0f; fallTimer = 0f;
        hasPending = false; followTimer = 0f; DribbleOffset = 0f; jostleTime = 0f;
    }

    /// <summary>Подключить 3D-модель: капсула остаётся физикой, модель только показывает и анимирует.</summary>
    public void AttachModel(Transform m)
    {
        model = m;
        anim = m.GetComponentInChildren<Animator>();
    }

    /// <summary>
    /// Анимация: смесь «покой → бег» по скорости; на спринте анимация ускоряется.
    /// На подкате модель ложится на спину ногами вперёд, в прыжке вратаря — на бок.
    /// </summary>
    void LateUpdate()
    {
        if (model == null) return;
        float speed = Velocity.magnitude;
        if (anim != null)
        {
            // Бег Mixamo — трусца ~3.6 м/с, а игроки бегают 5.5–9 м/с: без ускорения ноги «плывут» (эффект слоу-мо).
            // Полный бег в смеси уже с 2.5 м/с, дальше скорость воспроизведения = реальная скорость / скорость анимации.
            anim.SetFloat(SpeedHash, speed * (PlayerModelRunThreshold / 2.5f));
            anim.speed = speed > 2.5f ? Mathf.Clamp(speed / animRunSpeed, 1f, 2.5f) : 1f;
            if (slideVisualTimer > 0f || fallTimer > 0f) anim.speed = 0.05f;   // в подкате/падении ноги не «бегут»
        }
        Quaternion rot = Quaternion.identity;
        Vector3 pos = new Vector3(0f, -1f, 0f);
        float rate = 18f;
        if (slideVisualTimer > 0f)
        {
            // Подкат: ложимся на бок-спину ногами вперёд по ходу подката (поворот вокруг стоп), пока не встанем
            rot = Quaternion.Euler(-78f, 0f, 0f);
            pos.y = -0.95f;
            rate = 30f;
        }
        else if (fallTimer > 0f)
        {
            rot = Quaternion.Euler(80f, 0f, 0f);                          // сбили — падает вперёд
            rate = 22f;
        }
        else if (hasPending)
        {
            rot = Quaternion.Euler(-12f, 0f, 0f);                         // замах: корпус чуть назад
            rate = 25f;
        }
        else if (followTimer > 0f)
        {
            rot = Quaternion.Euler(14f, 0f, 0f);                          // удар: корпус вперёд за мячом
            rate = 25f;
        }
        else if (Diving)
        {
            float side = Vector3.Dot(diveDir, transform.right) >= 0f ? -1f : 1f;   // падаем в сторону прыжка
            rot = Quaternion.Euler(0f, 0f, 75f * side);
            pos.y = -0.8f;                                                  // немного над газоном — в полёте
        }
        float k = 1f - Mathf.Exp(-rate * Time.deltaTime);
        model.localRotation = Quaternion.Slerp(model.localRotation, rot, k);
        model.localPosition = Vector3.Lerp(model.localPosition, pos, k);
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
        tackleTimer -= dt; tackleCooldown -= dt; recoverTimer -= dt; diveCooldown -= dt; slideCooldown -= dt;
        slideVisualTimer -= dt; fallTimer -= dt; followTimer -= dt;
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
            slideTimer = 0f; diveTimer = 0f; skillTimer = 0f; hasPending = false;
            return;
        }

        // Замах: пока идёт — игрок притормаживает; по окончании удар уходит, если мяч всё ещё у него
        if (hasPending)
        {
            windupTimer -= dt;
            desiredVel = Velocity * 0.45f;
            if (windupTimer <= 0f)
            {
                hasPending = false;
                if (CanKick) { DoKick(pending.dir, pending.power, pending.lift, pending.receiver, pending.side, pending.top); followTimer = 0.18f; }
                else if (IsControlled) mm.Flash("ОТОБРАЛИ!");        // мяч отобрали во время замаха — удар сорвался
            }
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
        Vector3 flat = Sliding || Tackling || Diving
            ? Vector3.MoveTowards(Flat(v), desiredVel, 140f * Time.fixedDeltaTime)   // рывки — мгновенно
            : Locomotion(Flat(v), desiredVel, Time.fixedDeltaTime);
        rb.linearVelocity = new Vector3(flat.x, v.y, flat.z);

        UpdateDribbleTouch(flat.magnitude, Time.fixedDeltaTime);
        if (!Sliding && !Diving && role == Role.Field && !mm.Stopped) Jostle(Time.fixedDeltaTime);

        // Куда смотрит игрок: на бегу — туда, куда реально бежит (мяч у ноги идёт по дуге вместе с ним),
        // стоя или на стандарте — по прицелу стика
        bool useAim = IsControlled || mm.IsTaker(this) || (role == Role.Keeper && HasBall) || skillTimer > 0f;
        if (useAim && !mm.IsTaker(this) && skillTimer <= 0f && flat.magnitude > 1.5f) useAim = false;
        Vector3 face = Sliding ? slideDir : useAim ? aim : flat;
        if (!useAim && !Sliding && flat.sqrMagnitude < 1f) face = BallPos - Position;   // стоим — смотрим на мяч
        bool lying = (slideVisualTimer > 0f && !Sliding) || fallTimer > 0f;    // лежим — не крутимся
        if (face.sqrMagnitude > 0.01f && !lying)
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, Quaternion.LookRotation(face), 14f * Time.fixedDeltaTime));
    }

    /// <summary>
    /// Модель движения с инерцией:
    ///  • скорость и направление меняются раздельно;
    ///  • разгон быстрый (до бега ~0.15 с, до спринта ~0.3 с), торможение ещё быстрее;
    ///  • на бегу поворот почти мгновенный, на полном спринте — немного шире дуга;
    ///  • с мячом повороты чуть медленнее.
    /// </summary>
    Vector3 Locomotion(Vector3 cur, Vector3 want, float dt)
    {
        float speed = cur.magnitude;
        float wantSpeed = want.magnitude;
        Vector3 dir = speed > 0.2f ? cur / speed : wantSpeed > 0.01f ? want / wantSpeed : Facing;

        if (wantSpeed > 0.01f)
        {
            Vector3 wantDir = want / wantSpeed;
            float sprintK = Mathf.Clamp01((speed - runSpeed) / Mathf.Max(sprintSpeed - runSpeed, 0.1f));   // 0 — бег, 1 — полный спринт
            float turnRate = Mathf.Lerp(turnRateSlow, turnRateFast, sprintK) * (HasBall ? 0.9f : 1f);
            dir = Vector3.RotateTowards(dir, wantDir, turnRate * Mathf.Deg2Rad * dt, 0f);
        }

        float top = Mathf.Max(sprintSpeed, 1f);
        float a = wantSpeed > speed ? accel * (1f - 0.3f * Mathf.Clamp01(speed / top)) : braking;
        speed = Mathf.MoveTowards(speed, wantSpeed, a * dt);
        return dir * speed;
    }

    /// <summary>
    /// Касания при ведении: мяч не «приклеен» намертво — на бегу игрок подталкивает его в ритм шагов
    /// (раз в ~2.4 м пути). Шагом касаний нет, на бегу — чуть-чуть, на спринте — дальше (в этот момент мяч легче отобрать).
    /// </summary>
    void UpdateDribbleTouch(float speed, float dt)
    {
        if (!HasBall || Shielding || speed < runSpeed * 0.6f) { DribbleOffset = Mathf.MoveTowards(DribbleOffset, 0f, dt); dribblePhase = 0f; return; }
        float amp = speed > runSpeed + 0.4f ? 0.32f : 0.12f;
        dribblePhase += speed * dt / 2.4f;
        DribbleOffset = amp * Mathf.Sin(Mathf.Repeat(dribblePhase, 1f) * Mathf.PI);   // толчок вперёд и мяч снова у ноги
    }

    /// <summary>Сила в борьбе корпусами: укрывающий мяч и быстрее бегущий — сильнее.</summary>
    public float Strength => 1f + (Shielding ? 0.8f : 0f) + (HasBall ? 0.2f : 0f) + 0.4f * Mathf.Clamp01(Velocity.magnitude / sprintSpeed);

    /// <summary>
    /// Борьба корпусами: соперник вплотную (≤ 0.9 м) — оба толкаются, более слабого отжимает в сторону.
    /// Если соперник долго толкается с владельцем мяча, который не укрывает мяч, — мяч может выскочить.
    /// </summary>
    void Jostle(float dt)
    {
        bool contact = false;
        foreach (var o in mm.players)
        {
            if (o.team == team || o.role == Role.Keeper) continue;
            Vector3 d = Position - o.Position;
            float dist = d.magnitude;
            if (dist > 0.9f || dist < 0.01f) continue;
            contact = true;
            float share = o.Strength / (Strength + o.Strength);                 // чем соперник сильнее, тем сильнее отжимает меня
            float push = Mathf.Clamp01((0.9f - dist) / 0.3f) * 7f * share;       // м/с² — боковое отталкивание
            rb.linearVelocity += d / dist * push * dt;
        }
        if (!contact || !HasBall || Shielding) { jostleTime = 0f; return; }
        jostleTime += dt;
        if (jostleTime > 0.6f)
        {
            jostleTime = 0f;
            if (Random.value < 0.3f)                                              // мяч выскочил в борьбе
                DoKick(Facing + new Vector3(Random.Range(-0.8f, 0.8f), 0f, Random.Range(-0.8f, 0.8f)), 3.5f, 0.2f, null, 0f, 0f);
        }
    }

    // ------------------------------------------------------------ твой игрок

    void HumanUpdate()
    {
        Vector3 dir = mm.CameraRelative(GameInput.Move());
        bool taker = mm.IsTaker(this);
        Player owner = Ball.Owner;
        bool defending = owner != null && owner.team != team;
        bool modifier = GameInput.Held(Btn.Modifier);   // RB / R1
        // RT — спринт. Спринт всегда главнее: пока зажат RT, игрок никогда не замедляется (LT и укрывание игнорируются)
        bool wantSprint = GameInput.Held(Btn.Sprint);
        bool slow = !wantSprint && GameInput.Held(Btn.Jockey);   // LT / L2 — выжидание, только без спринта
        // Пас летит тебе — игрок сам выходит на мяч; стиком в это время выбираешь направление первого касания
        bool receiving = mm.passReceiver == this && owner == null && !Ball.Held;

        if (taker) desiredVel = Vector3.zero;           // на стандарте стоим, стик только целится
        else if (receiving) MoveTo(ReceivePoint(), wantSprint ? 1.35f : 1f);
        else
        {
            float spd = wantSprint ? SprintSpeedNow : runSpeed;
            sprinting = wantSprint && dir.sqrMagnitude > 0.1f;
            if (role == Role.Keeper) spd = runSpeed * 0.8f;                                   // вратарь с мячом в руках
            else if (HasBall && !wantSprint && (slow || modifier)) { shielding = true; spd = runSpeed * 0.5f; }  // укрывание корпусом
            else if (slow) spd = runSpeed * 0.55f;                                           // выжидание лицом к атаке
            desiredVel = dir * spd;
            if (role == Role.Keeper) desiredVel = KeepInsideBox(desiredVel);

            if (defending && GameInput.Held(Btn.Pass))
                MoveTo(ContainPoint(owner), wantSprint ? 1.3f : 1f);    // A (держать): сдерживание лицом к лицу
        }
        if (dir.sqrMagnitude > 0.01f) aim = dir.normalized;
        if (slow && defending && (BallPos - Position).sqrMagnitude > 0.01f) aim = (BallPos - Position).normalized;

        // X — подкат не только когда мяч у соперника, но и когда он ничей (после отскока, паса соперника, плохого приёма).
        // Исключение: мяч летит тебе от партнёра — тогда X это навес с первого касания.
        bool looseNotMine = owner == null && !Ball.Held && !receiving && !taker;
        if (looseNotMine && GameInput.Down(Btn.Lob)) { SlideTackle(dir.sqrMagnitude > 0.01f ? dir : (BallPos - Position)); return; }

        if (defending && !taker)
        {
            // ---------------- ОБОРОНА (смена игрока — LB / правый стик — в MatchManager, работает всегда)
            if (GameInput.Down(Btn.Lob)) SlideTackle(dir.sqrMagnitude > 0.01f ? dir : (BallPos - Position));   // X / Квадрат — подкат (по стику, иначе к мячу)
            if (GameInput.Down(Btn.Shoot)) StandingTackle();               // B / Круг — отбор, толчок корпусом
            if (GameInput.Held(Btn.Through)) mm.RequestKeeperRush();       // Y / Треугольник — выход вратаря
            if (GameInput.Held(Btn.Modifier)) mm.RequestTeammatePress();   // RB / R1 — прессинг партнёра
            charge = 0f; passCharge = 0f; passBuffer = 0f; shotBuffer = 0f;
            shotArmed = false; lobArmed = false; throughArmed = false; groundArmed = false;
            return;
        }

        // ---------------- АТАКА / СВОБОДНЫЙ МЯЧ
        // A — пас по земле: держишь — набираешь силу, отпускаешь — пас (с RB — прострел).
        // Даже короткое нажатие доводит мяч до адресата; сила делает пас быстрее и позволяет отдать дальнему партнёру.
        if (GameInput.Down(Btn.Pass)) { groundArmed = true; lobArmed = false; throughArmed = false; passCharge = 0f; headerBuffer = 0.3f; headerShot = false; }
        if (groundArmed && GameInput.Held(Btn.Pass)) passCharge = FillCharge(passCharge, passChargeTime);
        if (groundArmed && GameInput.Up(Btn.Pass))
        {
            passBuffer = 0.3f; bufferedPass = modifier ? PassKind.Driven : PassKind.Ground; bufferedPassPower = passCharge;
            groundArmed = false; passCharge = 0f;
        }

        // X — навес и Y — пас на ход: держишь — набираешь силу (дальность), отпускаешь — удар
        if (GameInput.Down(Btn.Lob)) { lobArmed = true; throughArmed = false; groundArmed = false; passCharge = 0f; }
        if (GameInput.Down(Btn.Through)) { throughArmed = true; lobArmed = false; groundArmed = false; passCharge = 0f; throughLofted = modifier; }
        if (lobArmed && GameInput.Held(Btn.Lob) || throughArmed && GameInput.Held(Btn.Through))
            passCharge = FillCharge(passCharge, passChargeTime);
        if (lobArmed && GameInput.Up(Btn.Lob))
        {
            passBuffer = 0.3f; bufferedPass = PassKind.Lob; bufferedPassPower = passCharge;
            lobArmed = false; passCharge = 0f;
        }
        if (throughArmed && GameInput.Up(Btn.Through))
        {
            passBuffer = 0.3f; bufferedPass = throughLofted ? PassKind.LobThrough : PassKind.Through; bufferedPassPower = passCharge;
            throughArmed = false; passCharge = 0f;
        }

        // B — удар (держать — сильнее; с RB — закрученный). У вратаря — выбивание.
        if (GameInput.Down(Btn.Shoot)) { shotArmed = true; headerBuffer = 0.3f; headerShot = true; }
        if (shotArmed && GameInput.Held(Btn.Shoot)) charge = FillCharge(charge, chargeTime);
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
            PassKind kind = bufferedPass;
            float pw = bufferedPassPower;
            bool lofted = kind == PassKind.Lob || kind == PassKind.LobThrough;
            // Замах: пас 0.1–0.16 с, навес 0.18–0.26 с — чем сильнее, тем дольше
            float windup = (lofted ? 0.18f : 0.1f) + 0.08f * Mathf.Max(pw, 0f);
            WithWindup(windup, () =>
            {
                bool ok;
                switch (kind)
                {
                    case PassKind.Lob: ok = TryLob(aim, false, pw); break;
                    case PassKind.LobThrough: ok = TryLob(aim, true, pw); break;
                    case PassKind.Through: ok = TryThrough(aim, pw) || TryPass(aim, 70f, false); break;
                    case PassKind.Driven: ok = TryPass(aim, 70f, true, pw); break;
                    default: ok = TryPass(aim, 70f, false, pw); break;
                }
                if (!ok && (taker || role == Role.Keeper)) { Kick(aim, 12f, 0.3f, null); ok = true; }   // на стандарте не застреваем
                return ok;
            });
        }
        if (shotBuffer > 0f && CanKick)
        {
            shotBuffer = 0f;
            float c = bufferedCharge;
            bool fin = bufferedFinesse;
            // Замах удара: 0.12 с на лёгкий, до 0.3 с на удар в полную силу; закрученный — чуть дольше
            float windup = 0.12f + 0.18f * PowerCurve(c) + (fin ? 0.05f : 0f);
            if (role == Role.Keeper) WithWindup(0.25f, () => { DropKick(aim, c); return true; });
            else WithWindup(windup, () => { Shoot(aim, c, fin); return true; });
        }
    }

    /// <summary>
    /// Шкала силы заполняется неравномерно: быстро в начале и всё медленнее к концу (как в FIFA) —
    /// лёгкое нажатие даёт аккуратный удар, а «дожать» до максимума нужно постараться.
    /// </summary>
    float FillCharge(float c, float time) => Mathf.Min(1f, c + Time.deltaTime / time * (1.5f - c));

    /// <summary>
    /// Кривая силы: эффект растёт медленнее шкалы (степень 1.6) — половина шкалы ≈ треть мощности,
    /// по-настоящему сильный удар только ближе к полной шкале.
    /// </summary>
    static float PowerCurve(float c) => Mathf.Pow(Mathf.Clamp01(c), 1.6f);

    /// <summary>Вратарь с мячом в руках не выходит за пределы штрафной.</summary>
    Vector3 KeepInsideBox(Vector3 vel)
    {
        float gx = mm.OwnGoalX(team);
        Vector3 next = Position + vel * 0.15f;
        if (Mathf.Abs(next.x - gx) > mm.boxDepth - 0.3f || Mathf.Sign(next.x - gx) == Mathf.Sign(gx)) vel.x = 0f;
        if (Mathf.Abs(next.z) > mm.boxHalfWidth - 0.3f) vel.z = 0f;
        return vel;
    }

    /// <summary>Вратарь выбивает мяч с рук (B): далеко и высоко, сила — от заряда.</summary>
    void DropKick(Vector3 dir, float power01)
    {
        if (!CanKick) return;
        float e = PowerCurve(power01);
        Kick(dir, Mathf.Lerp(16f, 26f, e), Mathf.Lerp(6f, 10f, e), null, 0f, 0.15f);
    }

    /// <summary>
    /// Финты правым стиком (относительно направления взгляда):
    /// вперёд — толчок мяча на ход и рывок; назад — протяжка с разворотом; в сторону — уход с мячом в сторону.
    /// Во время финта мяч сложнее отобрать.
    /// </summary>
    public void SkillMove(Vector3 d)
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
        else if (weHaveBall && Time.time < afterPassRunUntil)
        {
            // Отдал пас — не убегаем на позицию, а открываемся вперёд под ответный пас
            target = Position + AttackDir * 5f + new Vector3(0f, 0f, -Position.z * 0.25f);
            mul = 1.1f;
        }
        else if (weHaveBall)
        {
            target = OpenSpace();                                                   // открываемся рядом с мячом под пас
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
        Vector3 basePos = mm.SupportPos(this);   // точка поддержки рядом с игроком с мячом, а не далёкая «позиция»
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
            float c = Random.Range(0.6f, 1f);
            bool fin = Random.value < 0.3f;
            WithWindup(0.14f + 0.14f * c, () => { Shoot(aimPt - BallPos, c, fin); return true; });
            return;
        }
        if (dOpp < 2.2f || Random.value < 0.1f)
        {
            if (Random.value < 0.25f && WithWindup(0.12f, () => TryThrough(toGoal))) return;
            WithWindup(0.1f, () => TryPass(toGoal, 110f, false));
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
            if (holdTimer > 0.6f)
                WithWindup(0.22f, () =>
                {
                    if (TryPass(fieldDir, 100f, false)) return true;
                    Kick(new Vector3(inField, 0f, Random.Range(-0.6f, 0.6f)), Random.Range(16f, 21f), 3f, null);
                    return true;
                });
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

        // Удар в сторону ворот: вратарь замечает его не сразу, а через время реакции
        if (bv.x * inField < -10f) { if (shotSeenAt < 0f) shotSeenAt = Time.time; }
        else shotSeenAt = -1f;
        bool reacted = shotSeenAt >= 0f && Time.time - shotSeenAt >= keeperReaction;

        // Прыжок: удар идёт в створ, а вратарь не успевает дойти шагом
        if (diveCooldown <= 0f && reacted)
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

        // 1) На линии ворот, смещаясь за мячом (с запаздыванием на реакцию — вратарь не «приклеен» к мячу)
        Vector3 seen = b - bv * keeperReaction;
        float z = seen.z * 0.5f;
        // 2) Мяч летит в ворота — встаём в точку пересечения траектории с линией (только когда среагировал)
        if (reacted)
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
            {
                Vector3 pen = goal + Vector3.forward * (Random.value < 0.5f ? -half : half) - BallPos;
                float c = Random.Range(0.7f, 0.95f);
                WithWindup(0.3f, () => { Shoot(pen, c, false); return true; });
                return;
            }
            case SetPieceType.FreeKick:
                if (Vector3.Distance(BallPos, goal) < 16f)
                {
                    Vector3 fk = goal + Vector3.forward * Random.Range(-half, half) - BallPos;
                    float c = Random.Range(0.65f, 0.9f);
                    WithWindup(0.3f, () => { Shoot(fk, c, true); return true; });
                    return;
                }
                break;
            case SetPieceType.Corner:
                if (WithWindup(0.22f, () => TryLob(toGoal, false))) return;   // угловой — навес в штрафную
                break;
        }
        WithWindup(0.15f, () =>
        {
            if (!TryPass(toGoal, 180f, false)) Kick(toGoal, 16f, 2f, null);
            return true;
        });
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
        if (slideCooldown > 0f || slideTimer > 0f) return;
        if (Flat(dir).sqrMagnitude < 0.01f) dir = Facing;
        slideDir = Flat(dir).normalized;
        slideTimer = slideTime;
        slideElapsed = 0f;
        slideVisualTimer = slideTime + slideRecover;   // модель лежит весь подкат и пока встаёт
        hasPending = false;                            // подкат отменяет замах
        slideCooldown = slideTime + slideRecover + 0.15f;   // своя перезарядка: сразу после вставания можно снова
    }

    /// <summary>Подкат: едем по инерции. Сначала мяч — чисто выбили; сначала соперник (обычно сзади) — фол.</summary>
    /// <summary>
    /// Подкат: едем по газону. Если первым достал мяч — чисто выбил его. Если первым врезался в соперника:
    /// контакт засчитывается только после начала подката (0.08 с), вплотную (≤ 0.6 м) и если соперник впереди по ходу
    /// подката. Сзади — почти всегда фол, спереди у мяча — иногда, иначе чистый отбор. Соперник в любом случае падает.
    /// </summary>
    void SlideUpdate(float dt)
    {
        slideTimer -= dt;
        slideElapsed += dt;
        desiredVel = slideDir * slideSpeed;
        if (!Ball.Held && Vector3.Distance(Position, BallPos) < 1.1f && Ball.Body.position.y < 1f)
        {
            Vector3 poke = (slideDir + new Vector3(Random.Range(-0.3f, 0.3f), 0f, Random.Range(-0.3f, 0.3f))).normalized;
            Kick(poke, 9f, 0.6f, null);
            slideTimer = 0f;
        }
        else if (slideElapsed > 0.08f)
        {
            foreach (var o in mm.players)
            {
                if (o.team == team) continue;
                Vector3 to = o.Position - Position;
                float d = to.magnitude;
                if (d > 0.6f || d < 0.001f || Vector3.Dot(slideDir, to / d) < 0.3f) continue;   // не впереди — не задел

                bool fromBehind = Vector3.Dot(o.Facing, slideDir) > 0.5f;                       // бежим в одну сторону — подкат сзади
                bool nearBall = o.HasBall || Vector3.Distance(o.Position, BallPos) < 1.5f;
                float foulChance = fromBehind ? 0.85f : nearBall ? 0.3f : 0.15f;
                if (o.HasBall) Kick((BallPos - o.Position).normalized + slideDir, 5f, 0.2f, null);   // мяч в любом случае уходит от него
                o.Trip();
                if (Random.value < foulChance) mm.OnFoul(this, o);
                slideTimer = 0f;
                break;
            }
        }
        if (slideTimer <= 0f) recoverTimer = slideRecover;
    }

    /// <summary>Сбили подкатом — падает и секунду не участвует в игре.</summary>
    public void Trip()
    {
        recoverTimer = 0.8f;
        fallTimer = 0.9f;
        slideTimer = 0f;
        hasPending = false;                            // сбили — удар/пас сорвался
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

        float charge01 = power01;
        power01 = PowerCurve(power01);                     // нелинейная сила: мощь только ближе к полной шкале
        float power = finesse ? Mathf.Lerp(shotMin, shotMax * 0.8f, power01) : Mathf.Lerp(shotMin, shotMax, power01);
        float lift = finesse ? Mathf.Lerp(1.5f, 4f, power01) : Mathf.Lerp(0.5f, 4.5f, power01);
        if (charge01 > 0.95f) lift += 0.8f;                // «перебор» шкалы — мяч уходит заметно выше

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
    bool TryPass(Vector3 prefDir, float maxAngle, bool driven, float power01 = -1f)
    {
        if (!CanKick) return false;
        // Сила выбирает дистанцию: короткое нажатие — ближний партнёр, долгое — дальний (как в FIFA)
        float preferDist = power01 > 0.25f ? Mathf.Lerp(6f, 28f, power01) : -1f;
        Player mate = mm.FindPassTarget(this, prefDir, maxAngle, false, 28f, preferDist);
        if (mate == null) return false;

        float arrive = driven ? drivenArriveSpeed : passArriveSpeed;
        if (power01 > 0f) arrive = Mathf.Lerp(arrive, arrive + 7f, PowerCurve(power01));   // сильнее — быстрее доходит
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

    /// <summary>
    /// Пас на ход (Y): мяч уходит в свободную зону перед партнёром. Сила (заряд) задаёт, насколько далеко
    /// «на ход»: короткое нажатие — 3 м перед ним, полный заряд — 9 м. power01 &lt; 0 — автоматически (ИИ).
    /// </summary>
    bool TryThrough(Vector3 prefDir, float power01 = -1f)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, 70f, false);
        if (mate == null) return false;

        Vector3 run = mate.Velocity.sqrMagnitude > 1f
            ? (mate.AttackDir * 0.6f + mate.Velocity.normalized * 0.4f).normalized
            : mate.AttackDir;
        float lead = power01 < 0f ? 5f : Mathf.Lerp(3f, 9f, power01);
        Vector3 target = mm.ClampToField(mate.Position + run * lead, 1f);
        float d = Vector3.Distance(BallPos, target);
        const float arrive = 5f;
        float power = Mathf.Clamp(Mathf.Sqrt(arrive * arrive + 2f * Ball.rollDecel * (d + 1f)), 10f, 24f);
        Kick(target - BallPos, power, 0f, mate);
        return true;
    }

    /// <summary>
    /// Навес / длинный пас / перевод (X) или пас на ход навесом (RB+Y). Мяч летит по дуге над игроками
    /// с лёгким обратным вращением; скорость рассчитана на время полёта с поправкой на сопротивление воздуха.
    /// Сила (заряд X) задаёт дальность: 8…34 м. Если в направлении прицела есть партнёр примерно на этой
    /// дистанции — мяч летит ему, иначе — в точку. power01 &lt; 0 — дальность подбирается под партнёра (ИИ).
    /// </summary>
    bool TryLob(Vector3 prefDir, bool throughBall, float power01 = -1f)
    {
        if (!CanKick) return false;
        prefDir = Flat(prefDir).normalized;
        Player mate;
        Vector3 target;
        if (power01 < 0f)
        {
            mate = mm.FindPassTarget(this, prefDir, 70f, true, 36f);
            target = mate != null ? mate.Position : BallPos + prefDir * 14f;
        }
        else
        {
            float want = Mathf.Lerp(8f, 34f, power01);
            mate = null;
            float bestScore = float.MaxValue;
            foreach (var p in mm.players)
            {
                if (p == this || p.team != team || p.role == Role.Keeper) continue;
                Vector3 to = p.Position - Position;
                float angle = Vector3.Angle(prefDir, to);
                float miss = Mathf.Abs(to.magnitude - want);
                if (angle > 45f || miss > 9f) continue;
                float score = angle + miss * 2f;
                if (score < bestScore) { bestScore = score; mate = p; }
            }
            target = mate != null ? mate.Position : BallPos + prefDir * want;
        }
        if (mate != null) target += throughBall ? mate.AttackDir * 5f : mate.Velocity * 0.8f;
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

    /// <summary>
    /// Удар по мячу. Если перед вызовом задан замах (WithWindup) — удар уходит через это время, иначе сразу
    /// (финты, выбивание в подкате, отбор, удар головой).
    /// </summary>
    void Kick(Vector3 dir, float power, float lift, Player receiver, float side = 0f, float top = 0f)
    {
        if (nextWindup > 0f)
        {
            pending = new PendingKick { dir = dir, power = power, lift = lift, receiver = receiver, side = side, top = top };
            hasPending = true;
            windupTimer = nextWindup;
            nextWindup = 0f;
            passBuffer = 0f; shotBuffer = 0f;
            return;
        }
        DoKick(dir, power, lift, receiver, side, top);
    }

    /// <summary>Выполнить action с замахом: Kick внутри него будет отложен на time секунд.</summary>
    bool WithWindup(float time, System.Func<bool> action)
    {
        nextWindup = time;
        bool ok = action();
        nextWindup = 0f;                   // если action так и не ударил — замах не «протекает» в следующий удар
        return ok;
    }

    void DoKick(Vector3 dir, float power, float lift, Player receiver, float side, float top)
    {
        Ball.Kick(dir, power, lift, this, side, top);
        kickCooldown = 0.3f;   // не «подбираем» и не блокируем свой же удар
        aim = Flat(dir).normalized;
        mm.OnKick(this, receiver, lift > 3f);
        if (receiver != null && receiver != this) afterPassRunUntil = Time.time + 1.6f;   // отдал — открывайся («стенка»)
    }
}
