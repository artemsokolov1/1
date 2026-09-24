using UnityEngine;

/// <summary>
/// Мяч. Физика: отскоки от земли/штанг/сетки, трение качения, ограничение скорости.
/// Контроль: каждый физический кадр мяч сам определяет «владельца» — ближайшего игрока в радиусе контроля,
/// который способен его принять (мяч не слишком быстрый). Владелец ведёт мяч перед собой.
/// Быстрый мяч, попавший в игрока, отскакивает от тела (блок), а не прилипает.
/// С физическими капсулами игроков мяч НЕ сталкивается (Physics.IgnoreCollision) — все касания считаются здесь,
/// поэтому нет хаотичных «выстрелов» мячом при столкновении с бегущим игроком.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class Ball : MonoBehaviour
{
    [Header("Физика")]
    public float rollDecel = 3.5f;             // трение качения, м/с²
    public float maxSpeed = 32f;
    [Range(0f, 1f)] public float bounciness = 0.45f;

    [Header("Контроль мяча")]
    public float holdDistance = 0.8f;          // на каком расстоянии перед игроком держится мяч при ведении
    public float holdStiffness = 14f;          // насколько жёстко мяч тянется к точке ведения
    public float trapSpeed = 16f;              // мяч медленнее этого (относительно игрока) полевой принимает
    public float keeperTrapMultiplier = 1.6f;  // вратарь ловит мячи быстрее
    public float bodyRadius = 0.5f;            // радиус «тела» игрока для отскоков
    public float ownerBonus = 0.35f;           // чтобы отобрать мяч, нужно быть ближе владельца на столько метров
    public float tackleReach = 0.5f;           // отбор: дополнительный радиус
    public float tackleBonus = 0.6f;           // отбор: перевес в борьбе за мяч

    public Rigidbody Body { get; private set; }
    public SphereCollider Col { get; private set; }
    public float Radius { get; private set; }
    public Player Owner { get; private set; }  // кто сейчас ведёт мяч (null — мяч свободен)
    [HideInInspector] public Player lastTouch; // кто последним касался — для аутов/угловых
    public bool Held => Body.isKinematic;      // мяч закреплён на точке стандарта (аут, угловой...)

    void Awake()
    {
        Body = GetComponent<Rigidbody>();
        Body.mass = 0.45f;
        Body.linearDamping = 0.05f;
        Body.angularDamping = 1f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        Col = GetComponent<SphereCollider>();
        Radius = Col.radius * transform.lossyScale.x;
        Col.material = new PhysicsMaterial("Ball")
        {
            bounciness = bounciness,
            dynamicFriction = 0.2f,
            staticFriction = 0.2f,
            bounceCombine = PhysicsMaterialCombine.Maximum,
            frictionCombine = PhysicsMaterialCombine.Minimum
        };
    }

    void FixedUpdate()
    {
        if (Held) return;

        UpdateOwner();
        if (Owner != null) ApplyControl(Owner);
        else
        {
            BodyBlocks();
            ApplyRollingFriction();
        }
    }

    // ------------------------------------------------------------ владение

    void UpdateOwner()
    {
        var mm = MatchManager.I;
        if (mm == null) return;
        if (mm.Stopped) { Owner = null; return; }   // пауза после гола/аута — мяч ничей (вратарь не «вытащит» его из сетки)

        Player best = null;
        float bestScore = float.MaxValue;
        if (Body.position.y < 0.9f)   // высокий мяч не принять
        {
            foreach (var p in mm.players)
            {
                if (!p.CanControlBall) continue;
                float dist = Flat(Body.position - p.Position).magnitude;
                // Отбор (B / Круг): на короткое время длиннее «нога» и приоритет над владельцем
                float reach = p.controlRadius + (p.Tackling ? tackleReach : 0f);
                if (dist > reach) continue;

                float maxRel = trapSpeed * (p.role == Role.Keeper ? keeperTrapMultiplier : 1f);
                if (p != Owner && Flat(Body.linearVelocity - p.Velocity).magnitude > maxRel) continue; // слишком быстрый

                float score = dist - (p == Owner ? ownerBonus : 0f) - (p.Tackling ? tackleBonus : 0f);
                if (score < bestScore) { bestScore = score; best = p; }
            }
        }

        if (best == Owner) return;
        if (Owner != null) Owner.OnLostBall();
        Owner = best;
        if (Owner != null)
        {
            lastTouch = Owner;
            mm.OnPossession(Owner);
        }
    }

    /// <summary>Ведение: мяч тянется к точке перед игроком и прижимается к земле.</summary>
    void ApplyControl(Player p)
    {
        Vector3 hold = p.Position + p.Facing * holdDistance;
        Vector3 v = p.Velocity + Flat(hold - Body.position) * holdStiffness;
        v = Vector3.ClampMagnitude(v, p.Velocity.magnitude + 6f);
        Body.linearVelocity = new Vector3(v.x, Mathf.Min(Body.linearVelocity.y, 0f), v.z);
        Body.angularVelocity = Vector3.Cross(Vector3.up, v) / Radius;
    }

    /// <summary>Свободный мяч попал в игрока — отскок от «тела» с потерей энергии (блок удара/паса).</summary>
    void BodyBlocks()
    {
        var mm = MatchManager.I;
        if (mm == null) return;
        float minDist = bodyRadius + Radius;
        foreach (var p in mm.players)
        {
            if (p.JustKicked || Body.position.y > 2f) continue;   // свой только что отданный мяч не блокируем
            Vector3 d = Flat(Body.position - p.Position);
            float dist = d.magnitude;
            if (dist >= minDist) continue;

            Vector3 n = dist > 0.001f ? d / dist : p.Facing;
            float vn = Vector3.Dot(Flat(Body.linearVelocity - p.Velocity), n);
            if (vn < 0f) Body.linearVelocity -= n * vn * 1.4f;     // отражаем нормальную составляющую (упругость 0.4)
            Vector3 outPos = p.Position + n * minDist;
            Body.position = new Vector3(outPos.x, Body.position.y, outPos.z);
            lastTouch = p;
        }
    }

    void ApplyRollingFriction()
    {
        Vector3 v = Body.linearVelocity;
        Vector3 flat = Flat(v);
        if (transform.position.y <= Radius + 0.05f)
        {
            float s = Mathf.Max(0f, flat.magnitude - rollDecel * Time.fixedDeltaTime);
            flat = flat.normalized * s;
            Body.angularVelocity = Vector3.Cross(Vector3.up, flat) / Radius; // качение без проскальзывания
        }
        flat = Vector3.ClampMagnitude(flat, maxSpeed);
        Body.linearVelocity = new Vector3(flat.x, v.y, flat.z);
    }

    // ------------------------------------------------------------ команды

    /// <summary>Удар: направление по земле, скорость (м/с), подъём (вертикальная скорость).</summary>
    public void Kick(Vector3 dir, float power, float lift, Player by)
    {
        Body.isKinematic = false;
        if (Owner != null && Owner != by) Owner.OnLostBall();   // выбили подкатом — бывший владелец не подхватит сразу
        Owner = null;
        lastTouch = by;
        dir = Flat(dir).normalized;
        Body.linearVelocity = dir * power + Vector3.up * lift;
        Body.angularVelocity = Vector3.zero;
    }

    /// <summary>Закрепить мяч на точке (стандарт): он не двигается, пока его не пробьют.</summary>
    public void Hold(Vector3 pos)
    {
        if (!Body.isKinematic)
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            Body.isKinematic = true;
        }
        Owner = null;
        pos.y = Radius;
        Body.position = pos;
        transform.position = pos;
    }

    void OnTriggerEnter(Collider other)
    {
        if (MatchManager.I != null) MatchManager.I.OnBallTrigger(other);
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
}
