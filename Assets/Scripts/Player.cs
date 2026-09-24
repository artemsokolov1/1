using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public enum Team { Red = 0, Blue = 1 }
public enum Role { Field, Keeper }

/// <summary>
/// Игрок-капсула. Один класс на всех: управление с клавиатуры (isHuman),
/// ИИ полевого (бежать к мячу / держать позицию) и ИИ вратаря.
/// Создаётся из кода в MatchManager — руками на сцену вешать не нужно.
/// </summary>
public class Player : MonoBehaviour
{
    [Header("Параметры (скорость переопределяется в Init по роли)")]
    public float speed = 5.5f;
    public float accel = 40f;            // насколько резко игрок набирает/сбрасывает скорость
    public float controlRadius = 1.1f;   // дистанция до мяча, с которой можно бить/пасовать
    public float shotMin = 14f, shotMax = 28f;
    public float chargeTime = 0.8f;      // время полного заряда удара
    public float passExtra = 4f;         // запас скорости паса «на подходе» к партнёру
    public float aiShootDistance = 13f;  // ИИ бьёт по воротам ближе этой дистанции

    [Header("Состояние (заполняет MatchManager)")]
    public Team team;
    public Role role;
    public bool isHuman;
    public Vector3 homePos;              // позиция при разводке и «якорь» для удержания позиции
    [HideInInspector] public float charge; // 0..1, заряд удара игрока (для HUD)

    MatchManager mm;
    Rigidbody rb;
    Vector3 desiredVel;                  // желаемая скорость, считается в Update, применяется в FixedUpdate
    Vector3 aim;                         // куда смотрит/бьёт игрок-человек
    float kickCooldown, thinkTimer;

    Ball Ball => mm.ball;
    Vector3 BallPos => mm.ball.Body.position;
    Vector3 AttackDir => team == Team.Red ? Vector3.right : Vector3.left;
    static Team Opp(Team t) => t == Team.Red ? Team.Blue : Team.Red;
    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    public void Init(MatchManager m, Team t, Role r, bool human, Vector3 home)
    {
        mm = m; team = t; role = r; isHuman = human; homePos = home;
        if (r == Role.Keeper) { speed = 4.5f; controlRadius = 1.4f; } // вратарь медленнее, но «руки длиннее»
        if (human) speed = 6.5f;                                      // человеку чуть быстрее, чем ИИ

        rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 70f;
        rb.constraints = RigidbodyConstraints.FreezeRotation;         // капсула не падает
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // Без трения — чтобы игроки не «липли» к бортам и друг к другу
        GetComponent<Collider>().material = new PhysicsMaterial("Player")
        {
            dynamicFriction = 0f, staticFriction = 0f, bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        ResetTo(home);
    }

    public void ResetTo(Vector3 p)
    {
        p.y = 1f; // центр капсулы высотой 2 м
        rb.position = p; transform.position = p;
        rb.linearVelocity = Vector3.zero;
        Quaternion look = Quaternion.LookRotation(AttackDir);
        rb.rotation = look; transform.rotation = look;
        desiredVel = Vector3.zero; aim = AttackDir; charge = 0f; kickCooldown = 0f;
    }

    // ---------------------------------------------------------------- цикл

    void Update()
    {
        if (mm.Frozen) { desiredVel = Vector3.zero; charge = 0f; return; } // пауза после гола / конец матча
        kickCooldown -= Time.deltaTime;
        thinkTimer -= Time.deltaTime;

        if (isHuman) HumanUpdate();
        else if (role == Role.Keeper) KeeperUpdate();
        else FieldUpdate();
    }

    void FixedUpdate()
    {
        // Плавно подводим горизонтальную скорость к желаемой, вертикаль оставляем физике
        Vector3 v = rb.linearVelocity;
        Vector3 flat = Vector3.MoveTowards(Flat(v), desiredVel, accel * Time.fixedDeltaTime);
        rb.linearVelocity = new Vector3(flat.x, v.y, flat.z);

        Vector3 face = isHuman ? aim : flat;
        if (face.sqrMagnitude > 0.01f)
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, Quaternion.LookRotation(face), 15f * Time.fixedDeltaTime));

        if (role == Role.Field) Dribble();
    }

    // ---------------------------------------------------------------- человек

    void HumanUpdate()
    {
        // Движение относительно камеры: W — «вверх по экрану»
        Vector2 input = GameInput.Move();
        Transform cam = mm.cam.transform;
        Vector3 f = Flat(cam.forward).normalized;
        Vector3 r = Flat(cam.right).normalized;
        Vector3 dir = Vector3.ClampMagnitude(f * input.y + r * input.x, 1f);
        desiredVel = dir * speed;
        if (dir.sqrMagnitude > 0.01f) aim = dir.normalized;

        // Пас — по нажатию, партнёру в направлении взгляда
        if (GameInput.PassPressed()) TryPass(aim);

        // Удар — зажать для заряда силы, отпустить для удара в направлении взгляда
        if (GameInput.ShootHeld()) charge = Mathf.Min(1f, charge + Time.deltaTime / chargeTime);
        if (GameInput.ShootReleased())
        {
            if (HasBall()) Kick(aim, Mathf.Lerp(shotMin, shotMax, charge), Mathf.Lerp(0.5f, 5f, charge));
            charge = 0f;
        }
    }

    // ---------------------------------------------------------------- ИИ полевого

    void FieldUpdate()
    {
        Vector3 ball = Flat(BallPos);
        Vector3 goal = mm.GoalOf(Opp(team));          // ворота соперника
        bool chaser = mm.IsChaser(this);              // ближайший к мячу в команде бежит к мячу
        Vector3 target;

        if (chaser)
        {
            Vector3 toGoal = (goal - ball).normalized;
            if (HasBall())
            {
                // Решение раз в 0.25 с, чтобы ИИ не «дёргался» каждый кадр
                if (thinkTimer <= 0f)
                {
                    thinkTimer = 0.25f;
                    if (AIWithBall(goal)) return;
                }
                target = ball + toGoal * 2f;          // ведём мяч к воротам (Dribble держит его перед собой)
            }
            else
            {
                target = ball - toGoal * 0.7f;        // заходим за мяч со стороны своих ворот
                Vector3 me = Flat(transform.position);
                if (Vector3.Dot(me - ball, toGoal) > 0f)
                {
                    // Стоим между мячом и чужими воротами — обегаем мяч сбоку, а не толкаем его назад
                    Vector3 side = Vector3.Cross(Vector3.up, toGoal);
                    if (Vector3.Dot(me - ball, side) < 0f) side = -side;
                    target += side * 1.2f;
                }
            }
        }
        else
        {
            target = mm.FormationPos(this);           // держим позицию (сдвинутую за мячом)
        }

        MoveTo(mm.ClampToField(target, 0.6f), chaser ? 1f : 0.8f);
    }

    /// <summary>ИИ владеет мячом: удар, если близко к воротам; пас, если прессингуют. true — мяч отдан.</summary>
    bool AIWithBall(Vector3 goal)
    {
        Vector3 me = Flat(transform.position);
        if (Vector3.Distance(me, goal) < aiShootDistance)
        {
            float spread = mm.goalWidth * 0.5f - 0.5f;
            Vector3 aimPt = goal + Vector3.forward * Random.Range(-spread, spread);
            Kick(aimPt - Flat(BallPos), Random.Range(shotMin + 4f, shotMax - 2f), Random.Range(0.5f, 3f));
            return true;
        }
        mm.NearestOpponent(this, out float dOpp);
        if ((dOpp < 2.5f && Random.value < 0.6f) || Random.value < 0.05f)
            return TryPass(goal - me);
        return false;
    }

    // ---------------------------------------------------------------- ИИ вратаря

    void KeeperUpdate()
    {
        float gx = mm.OwnGoalX(team);        // x линии своих ворот
        float inField = -Mathf.Sign(gx);     // направление «в поле»
        float lineX = gx + inField * 0.8f;   // стоим чуть впереди линии
        float half = mm.goalWidth * 0.5f;
        Vector3 b = BallPos, bv = Ball.Body.linearVelocity;

        // 1) Базово — на линии ворот, смещаясь за мячом
        float z = b.z * 0.5f;
        // 2) Мяч летит в ворота — встаём в точку, где траектория пересечёт линию
        if (bv.x * inField < -2f)
        {
            float t = (lineX - b.x) / bv.x;
            if (t > 0f && t < 1.5f) z = b.z + bv.z * t;
        }
        Vector3 target = new Vector3(lineX, 0f, Mathf.Clamp(z, -half + 0.4f, half - 0.4f));

        // 3) Медленный мяч в штрафной — выходим и забираем
        bool inBox = Mathf.Abs(b.x - gx) < mm.boxDepth && Mathf.Abs(b.z) < mm.boxHalfWidth;
        if (inBox && Flat(bv).magnitude < 6f) target = Flat(b);

        MoveTo(target, 1f);

        // 4) Мяч в досягаемости — отбиваем: пас своему или выбиваем в поле
        if (HasBall())
        {
            if (Random.value < 0.5f && TryPass(new Vector3(inField, 0f, 0f))) return;
            Kick(new Vector3(inField, 0f, Random.Range(-0.7f, 0.7f)), Random.Range(16f, 22f), 3f);
        }
    }

    // ---------------------------------------------------------------- общие действия

    void MoveTo(Vector3 target, float speedMul)
    {
        Vector3 d = Flat(target - transform.position);
        float dist = d.magnitude;
        // У точки плавно тормозим (Clamp01), чтобы не «дрожать»
        desiredVel = dist < 0.2f ? Vector3.zero : d / dist * speed * speedMul * Mathf.Clamp01(dist);
    }

    bool HasBall()
    {
        if (kickCooldown > 0f) return false;
        Vector3 d = BallPos - transform.position;
        return BallPos.y < 1.5f && new Vector2(d.x, d.z).magnitude <= controlRadius;
    }

    void Kick(Vector3 dir, float power, float lift)
    {
        Ball.Kick(dir, power, lift, this);
        kickCooldown = 0.4f; // чтобы тут же не «подобрать» свой же удар
    }

    /// <summary>Пас лучшему партнёру: ближе к направлению prefDir и не слишком далеко.</summary>
    bool TryPass(Vector3 prefDir)
    {
        if (!HasBall()) return false;
        prefDir = Flat(prefDir).normalized;

        Player best = null;
        float bestScore = float.MinValue;
        foreach (var p in mm.players)
        {
            if (p == this || p.team != team || p.role == Role.Keeper) continue;
            Vector3 to = Flat(p.transform.position - transform.position);
            float d = to.magnitude;
            if (d < 2f) continue;
            float score = Vector3.Dot(prefDir, to / d) * 10f - d * 0.3f;
            if (score > bestScore) { bestScore = score; best = p; }
        }
        if (best == null) return false;

        // Упреждение: пасуем туда, где партнёр будет через ~0.4 с
        Vector3 target = best.transform.position + best.rb.linearVelocity * 0.4f;
        Vector3 dir = Flat(target - BallPos);
        // Сила из v² = 2·a·d — мяч докатится с учётом трения, + небольшой запас
        float power = Mathf.Sqrt(2f * Ball.rollDecel * dir.magnitude) + passExtra;
        Kick(dir, power, 0.3f);
        return true;
    }

    /// <summary>Ведение: если мяч прямо перед бегущим игроком — мягко держим его перед собой.</summary>
    void Dribble()
    {
        if (kickCooldown > 0f || desiredVel.sqrMagnitude < 1f) return;
        Rigidbody b = Ball.Body;
        Vector3 toBall = Flat(b.position - rb.position);
        if (b.position.y > 0.7f || toBall.magnitude > controlRadius) return;
        if (Flat(b.linearVelocity - rb.linearVelocity).magnitude > 12f) return; // быстрый мяч не «прилипает» — просто отскочит
        Vector3 fwd = desiredVel.normalized;
        if (Vector3.Dot(fwd, toBall.normalized) < 0.2f) return;                  // мяч сбоку/сзади — не ведём

        Vector3 hold = Flat(rb.position) + fwd * 0.9f;                           // точка перед носом
        Vector3 v = desiredVel + (hold - Flat(b.position)) * 10f;
        b.linearVelocity = new Vector3(v.x, b.linearVelocity.y, v.z);
        Ball.lastTouch = this;
    }
}

/// <summary>
/// Обёртка ввода: работает и с новым Input System, и со старым Input Manager.
/// WASD/стрелки — бег, J — пас, K — удар (зажать для силы), R — рестарт.
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
    public static bool PassPressed()    => K != null && K.jKey.wasPressedThisFrame;
    public static bool ShootHeld()      => K != null && K.kKey.isPressed;
    public static bool ShootReleased()  => K != null && K.kKey.wasReleasedThisFrame;
    public static bool RestartPressed() => K != null && K.rKey.wasPressedThisFrame;
#else
    public static Vector2 Move() => new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    public static bool PassPressed()    => Input.GetKeyDown(KeyCode.J);
    public static bool ShootHeld()      => Input.GetKey(KeyCode.K);
    public static bool ShootReleased()  => Input.GetKeyUp(KeyCode.K);
    public static bool RestartPressed() => Input.GetKeyDown(KeyCode.R);
#endif
}
