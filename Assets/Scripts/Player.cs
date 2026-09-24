using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public enum Team { Red = 0, Blue = 1 }
public enum Role { Field, Keeper }

/// <summary>
/// Игрок-капсула. Один класс на всех: управление с клавиатуры (если MatchManager отдал ему управление),
/// ИИ полевого, ИИ вратаря и розыгрыш стандартов. Мячом владеет не игрок, а Ball (см. Ball.Owner) —
/// игрок только двигается, поворачивается и бьёт.
/// Создаётся из кода в MatchManager — руками на сцену вешать не нужно.
/// </summary>
public class Player : MonoBehaviour
{
    [Header("Скорости, м/с")]
    public float aiSpeed = 5.5f;
    public float runSpeed = 6.2f;          // твой игрок
    public float sprintSpeed = 8f;         // твой игрок с Shift
    public float keeperSpeed = 4.5f;
    public float accel = 35f;

    [Header("Мяч")]
    public float controlRadius = 0.95f;    // в этом радиусе игрок может взять мяч
    public float shotMin = 14f, shotMax = 28f;
    public float chargeTime = 0.8f;        // время полного заряда удара
    public float passArriveSpeed = 7f;     // с какой скоростью пас приходит к партнёру
    public float aiShootDistance = 12f;

    [Header("Состояние (заполняет MatchManager)")]
    public Team team;
    public Role role;
    public Vector3 homePos;

    [HideInInspector] public float charge; // 0..1 — заряд удара (HUD)

    MatchManager mm;
    Rigidbody rb;
    Vector3 desiredVel;
    Vector3 aim;                           // направление взгляда/удара твоего игрока
    float kickCooldown, lostTimer, thinkTimer, holdTimer;
    float passBuffer, shotBuffer, bufferedCharge;   // буфер нажатий: можно нажать чуть раньше, чем мяч пришёл

    // ------------------------------------------------------------ свойства для Ball / MatchManager

    public Vector3 Position => new Vector3(rb.position.x, 0f, rb.position.z);
    public Vector3 Velocity => new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
    public Vector3 Facing { get { Vector3 f = transform.forward; f.y = 0f; return f.normalized; } }
    public Vector3 Aim => aim;
    public bool IsControlled => mm.controlled == this;
    public bool HasBall => Ball.Owner == this;
    public bool JustKicked => kickCooldown > 0f;
    public bool CanControlBall => kickCooldown <= 0f && lostTimer <= 0f;
    bool CanKick => HasBall || mm.IsTaker(this);

    Ball Ball => mm.ball;
    Vector3 BallPos => new Vector3(mm.ball.Body.position.x, 0f, mm.ball.Body.position.z);
    Vector3 AttackDir => team == Team.Red ? Vector3.right : Vector3.left;
    public static Team Opp(Team t) => t == Team.Red ? Team.Blue : Team.Red;
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    public void Init(MatchManager m, Team t, Role r, Vector3 home)
    {
        mm = m; team = t; role = r; homePos = home;
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

    public void ResetTo(Vector3 p, Vector3 facing)
    {
        p.y = 1f;
        rb.position = p; transform.position = p;
        rb.linearVelocity = Vector3.zero;
        facing = Flat(facing).sqrMagnitude > 0.01f ? Flat(facing).normalized : AttackDir;
        Quaternion look = Quaternion.LookRotation(facing);
        rb.rotation = look; transform.rotation = look;
        desiredVel = Vector3.zero; aim = facing;
        charge = 0f; kickCooldown = 0f; lostTimer = 0f; passBuffer = 0f; shotBuffer = 0f;
    }

    public void OnLostBall() => lostTimer = 0.4f;   // после отбора нельзя мгновенно вернуть мяч

    // ------------------------------------------------------------ цикл

    void Update()
    {
        float dt = Time.deltaTime;
        kickCooldown -= dt; lostTimer -= dt; thinkTimer -= dt;
        passBuffer -= dt; shotBuffer -= dt;
        holdTimer = HasBall ? holdTimer + dt : 0f;

        if (mm.Stopped) { desiredVel = Vector3.zero; charge = 0f; return; }  // гол, аут, конец матча — все стоят

        if (IsControlled) HumanUpdate();
        else if (mm.IsTaker(this)) AITakerUpdate();
        else if (role == Role.Keeper) KeeperUpdate();
        else FieldUpdate();
    }

    void FixedUpdate()
    {
        Vector3 v = rb.linearVelocity;
        Vector3 flat = Vector3.MoveTowards(Flat(v), desiredVel, accel * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector3(flat.x, v.y, flat.z);

        // Твой игрок, исполнитель стандарта и вратарь с мячом смотрят по aim, остальные — куда бегут
        bool useAim = IsControlled || mm.IsTaker(this) || (role == Role.Keeper && HasBall);
        Vector3 face = useAim ? aim : flat;
        if (face.sqrMagnitude > 0.01f)
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, Quaternion.LookRotation(face), 14f * Time.fixedDeltaTime));
    }

    // ------------------------------------------------------------ твой игрок

    void HumanUpdate()
    {
        Vector3 dir = InputDir();
        bool taker = mm.IsTaker(this);

        if (taker) desiredVel = Vector3.zero;   // на стандарте стоим, WASD только крутит направление
        else
        {
            float spd = GameInput.SprintHeld() ? sprintSpeed : runSpeed;
            desiredVel = dir * spd;
            // Пас летит тебе, а ты не жмёшь стрелки — игрок сам выходит навстречу мячу
            if (dir.sqrMagnitude < 0.01f && mm.passReceiver == this) MoveTo(InterceptPoint(), 1f);
        }
        if (dir.sqrMagnitude > 0.01f) aim = dir.normalized;

        if (GameInput.PassPressed()) passBuffer = 0.25f;
        if (GameInput.ShootHeld()) charge = Mathf.Min(1f, charge + Time.deltaTime / chargeTime);
        if (GameInput.ShootReleased()) { shotBuffer = 0.25f; bufferedCharge = charge; charge = 0f; }

        if (passBuffer > 0f && CanKick)
        {
            passBuffer = 0f;
            // Нет партнёра в направлении прицела — на стандарте всё равно отдаём мяч (не застреваем)
            if (!TryPass(aim, 70f) && taker) Kick(aim, 12f, 0.3f, null);
        }
        if (shotBuffer > 0f && CanKick)
        {
            shotBuffer = 0f;
            Shoot(aim, bufferedCharge);
        }

        if (GameInput.SwitchPressed()) mm.SwitchControl();
    }

    Vector3 InputDir()
    {
        Vector2 input = GameInput.Move();
        Transform cam = mm.cam.transform;
        Vector3 f = Flat(cam.forward).normalized;
        Vector3 r = Flat(cam.right).normalized;
        return Vector3.ClampMagnitude(f * input.y + r * input.x, 1f);
    }

    // ------------------------------------------------------------ ИИ полевого

    void FieldUpdate()
    {
        if (HasBall) { AIWithBall(); return; }

        Player owner = Ball.Owner;
        bool weHaveBall = owner != null && owner.team == team;
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
        else if (!weHaveBall && mm.IsChaser(this))
        {
            target = BallPos + Flat(Ball.Body.linearVelocity) * 0.25f;  // прессинг / бег к свободному мячу
            mul = 1f;
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

        float lineX = gx + inField * 0.8f;
        float half = mm.goalWidth * 0.5f;
        Vector3 b = BallPos, bv = Flat(Ball.Body.linearVelocity);

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

    static Vector3 KeepAway(Vector3 target, Vector3 spot, float radius)
    {
        Vector3 d = Flat(target - spot);
        if (d.magnitude >= radius) return target;
        if (d.sqrMagnitude < 0.01f) d = Vector3.forward;
        return spot + d.normalized * radius;
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

    /// <summary>Пас лучшему партнёру в секторе prefDir ± maxAngle. Сила рассчитана так, чтобы мяч дошёл «в ноги».</summary>
    bool TryPass(Vector3 prefDir, float maxAngle)
    {
        if (!CanKick) return false;
        Player mate = mm.FindPassTarget(this, prefDir, maxAngle);
        if (mate == null) return false;

        // Упреждение: куда партнёр добежит, пока летит мяч (2 итерации достаточно)
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

    void Kick(Vector3 dir, float power, float lift, Player receiver)
    {
        Ball.Kick(dir, power, lift, this);
        kickCooldown = 0.3f;   // не «подбираем» и не блокируем свой же удар
        aim = Flat(dir).normalized;
        mm.OnKick(this, receiver);
    }
}

/// <summary>
/// Обёртка ввода: работает и с новым Input System, и со старым Input Manager.
/// WASD/стрелки — бег, Shift — спринт, J — пас, K — удар (зажать для силы), Q — смена игрока, R — рестарт.
/// </summary>
public static class GameInput
{
#if ENABLE_INPUT_SYSTEM
    static Keyboard K => Keyboard.current;

    public static Vector2 Move()
    {
        if (K == null) return Vector2.zero;
        float x = (K.dKey.isPressed || K.rightArrowKey.isPressed ? 1f : 0f) - (K.aKey.isPressed || K.leftArrowKey.isPressed ? 1f : 0f);
        float y = (K.wKey.isPressed || K.upArrowKey.isPressed ? 1f : 0f) - (K.sKey.isPressed || K.downArrowKey.isPressed ? 1f : 0f);
        return new Vector2(x, y);
    }
    public static bool SprintHeld()     => K != null && K.leftShiftKey.isPressed;
    public static bool PassPressed()    => K != null && K.jKey.wasPressedThisFrame;
    public static bool ShootHeld()      => K != null && K.kKey.isPressed;
    public static bool ShootReleased()  => K != null && K.kKey.wasReleasedThisFrame;
    public static bool SwitchPressed()  => K != null && K.qKey.wasPressedThisFrame;
    public static bool RestartPressed() => K != null && K.rKey.wasPressedThisFrame;
#else
    public static Vector2 Move() => new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    public static bool SprintHeld()     => Input.GetKey(KeyCode.LeftShift);
    public static bool PassPressed()    => Input.GetKeyDown(KeyCode.J);
    public static bool ShootHeld()      => Input.GetKey(KeyCode.K);
    public static bool ShootReleased()  => Input.GetKeyUp(KeyCode.K);
    public static bool SwitchPressed()  => Input.GetKeyDown(KeyCode.Q);
    public static bool RestartPressed() => Input.GetKeyDown(KeyCode.R);
#endif
}
