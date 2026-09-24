using UnityEngine;

/// <summary>
/// Мяч.
///
/// Полёт (по исследованиям аэродинамики футбольного мяча):
///  • сопротивление воздуха квадратичное: a = −k·|v|·v (реальный мяч: Cd ≈ 0.25…0.3 → k ≈ 0.013 1/м);
///  • эффект Магнуса: вращение даёт силу поперёк скорости, a = k·spin·v² (C_L ≈ 0.25 при сильном вращении).
///    Боковое вращение закручивает мяч, обратное — «подвешивает» навес, верхнее — «кладёт» удар вниз.
/// Контроль: каждый физический кадр мяч определяет владельца — ближайшего игрока в радиусе контроля.
///  • мягкий мяч принимается, быстрый — отскакивает от ноги (жёсткое первое касание);
///  • быстрый удар в тело — отскок (блок); вратарь ловит медленные и отбивает быстрые удары.
/// С капсулами игроков мяч физически не сталкивается (Physics.IgnoreCollision): все касания считаются здесь.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class Ball : MonoBehaviour
{
    [Header("Полёт")]
    public float dragK = 0.012f;               // квадратичное сопротивление воздуха
    public float magnusK = 0.012f;             // сила Магнуса на единицу вращения
    public float spinDecay = 0.6f;             // затухание вращения в воздухе, 1/с (на земле — в 4 раза быстрее)
    public float rollDecel = 3.5f;             // трение качения, м/с²
    public float maxSpeed = 34f;
    [Range(0f, 1f)] public float bounciness = 0.5f;

    [Header("Контроль мяча")]
    public float holdDistance = 0.8f;          // мяч перед игроком при обычном ведении
    public float sprintHoldDistance = 1.3f;    // на спринте касания длиннее — мяч легче отобрать
    public float shieldHoldDistance = 0.7f;    // при укрывании мяч с дальней от соперника стороны
    public float holdStiffness = 14f;
    public float trapSpeed = 16f;              // быстрее этого (относительно игрока) полевой мяч не остановит вовсе
    public float softTouchSpeed = 9f;          // до этой скорости приём чистый, выше — мяч отскакивает от ноги
    public float keeperCatchSpeed = 20f;       // вратарь ловит медленнее этого…
    public float keeperParrySpeed = 36f;       // …и отбивает до этого
    public float bodyRadius = 0.5f;
    public float ownerBonus = 0.35f;           // чтобы отобрать мяч, нужно быть ближе владельца на столько
    public float tackleReach = 0.5f;           // отбор: дополнительный радиус
    public float tackleBonus = 0.6f;           // отбор: перевес в борьбе за мяч

    public Rigidbody Body { get; private set; }
    public SphereCollider Col { get; private set; }
    public float Radius { get; private set; }
    public Player Owner { get; private set; }
    [HideInInspector] public Player lastTouch;
    [HideInInspector] public float sideSpin, topSpin;   // −1…1: +side — закрутка вправо, +top — обратное вращение (подъём)
    public bool Held => Body.isKinematic;
    public bool Grounded => Body.position.y <= Radius + 0.05f;

    void Awake()
    {
        Body = GetComponent<Rigidbody>();
        Body.mass = 0.45f;
        Body.linearDamping = 0f;                // сопротивление считаем сами (квадратичное)
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
        if (Owner != null)
        {
            ApplyControl(Owner);
            sideSpin = topSpin = 0f;
        }
        else
        {
            BodyBlocks();
            ApplyAerodynamics();
            ApplyRollingFriction();
        }
    }

    // ------------------------------------------------------------ владение

    void UpdateOwner()
    {
        var mm = MatchManager.I;
        if (mm == null) return;
        if (mm.Stopped) { Owner = null; return; }   // пауза после гола/аута — мяч ничей

        Player best = null, parry = null;
        float bestScore = float.MaxValue;
        float y = Body.position.y;
        foreach (var p in mm.players)
        {
            if (!p.CanControlBall) continue;
            bool keeper = p.role == Role.Keeper;
            if (y > (keeper ? 2.4f : 0.9f)) continue;            // высокий мяч полевой не примет (только головой), вратарь — руками

            float dist = Flat(Body.position - p.Position).magnitude;
            float reach = p.controlRadius + (p.Tackling ? tackleReach : 0f) + (p.Diving ? p.diveReach : 0f);
            if (dist > reach) continue;

            float rel = Flat(Body.linearVelocity - p.Velocity).magnitude;
            if (p != Owner)
            {
                if (keeper)
                {
                    if (rel > keeperParrySpeed) continue;
                    if (rel > keeperCatchSpeed) { parry = p; continue; }   // слишком сильно, чтобы поймать, — отобьёт
                }
                else if (rel > trapSpeed) continue;
            }

            float score = dist - (p == Owner ? ownerBonus + p.ShieldBonus : 0f) - (p.Tackling ? tackleBonus : 0f);
            if (score < bestScore) { bestScore = score; best = p; }
        }

        if (best == null && parry != null) { Parry(parry); return; }
        if (best == Owner) return;

        // Жёсткое первое касание: полевой принимает быстрый свободный мяч — он отскакивает от ноги
        if (best != null && Owner == null && best.role == Role.Field)
        {
            float rel = Flat(Body.linearVelocity - best.Velocity).magnitude;
            float soft = softTouchSpeed + best.touchBonus;
            if (rel > soft) { HeavyTouch(best, rel - soft); return; }
        }

        if (Owner != null) Owner.OnLostBall();
        Owner = best;
        if (Owner != null)
        {
            lastTouch = Owner;
            mm.OnPossession(Owner);
        }
    }

    /// <summary>Ведение: мяч тянется к точке перед игроком (или за его корпусом при укрывании) и прижимается к земле.</summary>
    void ApplyControl(Player p)
    {
        Vector3 dir = p.Shielding ? p.ShieldDir : p.Facing;
        float d = p.Shielding ? shieldHoldDistance : p.IsSprinting ? sprintHoldDistance : holdDistance;
        Vector3 hold = p.Position + dir * d;
        Vector3 v = p.Velocity + Flat(hold - Body.position) * holdStiffness;
        v = Vector3.ClampMagnitude(v, p.Velocity.magnitude + 6f);
        Body.linearVelocity = new Vector3(v.x, Mathf.Min(Body.linearVelocity.y, 0f), v.z);
        Body.angularVelocity = Vector3.Cross(Vector3.up, v) / Radius;
    }

    void HeavyTouch(Player p, float excess)
    {
        Vector3 side = Vector3.Cross(Vector3.up, p.Facing) * Random.Range(-1f, 1f);
        Vector3 v = p.Velocity + (p.Facing + side * 0.7f).normalized * Mathf.Min(1.5f + excess * 0.45f, 6f);
        Body.linearVelocity = new Vector3(v.x, Body.linearVelocity.y * 0.3f, v.z);
        sideSpin = topSpin = 0f;
        lastTouch = p;
        p.OnHeavyTouch();
        MatchManager.I.OnLooseBall();
    }

    /// <summary>Вратарь отбивает сильный удар: мяч уходит в сторону от ворот (часто — на угловой).</summary>
    void Parry(Player k)
    {
        Vector3 v = Body.linearVelocity;
        Vector3 away = new Vector3(-Mathf.Sign(k.Position.x), 0f, 0f);                    // от своих ворот в поле
        Vector3 lateral = new Vector3(0f, 0f, Body.position.z >= k.Position.z ? 1f : -1f);
        Vector3 nv = (away * 0.5f + lateral).normalized * v.magnitude * 0.35f + Vector3.up * Random.Range(1f, 4f);
        Body.linearVelocity = nv;
        sideSpin = topSpin = 0f;
        lastTouch = k;
        k.OnParry();
        MatchManager.I.OnLooseBall();
        MatchManager.I.Shake(0.12f);
        MatchManager.I.Flash("СЕЙВ!");
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
            if (vn < 0f)
            {
                Body.linearVelocity -= n * vn * 1.4f;      // отражаем нормальную составляющую (упругость 0.4)
                sideSpin *= 0.3f;
                if (vn < -6f) mm.OnLooseBall();            // заметный рикошет — мяч ничей
            }
            Vector3 outPos = p.Position + n * minDist;
            Body.position = new Vector3(outPos.x, Body.position.y, outPos.z);
            lastTouch = p;
        }
    }

    /// <summary>Сопротивление воздуха и эффект Магнуса.</summary>
    void ApplyAerodynamics()
    {
        Vector3 v = Body.linearVelocity;
        float s = v.magnitude;
        float dt = Time.fixedDeltaTime;
        bool air = !Grounded;

        if (s > 0.5f)
        {
            Vector3 a = Vector3.zero;
            if (air) a -= dragK * s * v;                                  // −k·|v|·v
            Vector3 right = Vector3.Cross(Vector3.up, v).normalized;      // «вправо» от направления полёта
            a += right * (magnusK * sideSpin * s * s * (air ? 1f : 0.3f));
            if (air) a += Vector3.up * (magnusK * topSpin * s * s);       // обратное вращение — подъём, верхнее — вниз
            Body.linearVelocity = v + a * dt;
        }

        float decay = Mathf.Exp(-spinDecay * (air ? 1f : 4f) * dt);
        sideSpin *= decay;
        topSpin *= decay;
    }

    void ApplyRollingFriction()
    {
        Vector3 v = Body.linearVelocity;
        Vector3 flat = Flat(v);
        if (Grounded)
        {
            float s = Mathf.Max(0f, flat.magnitude - rollDecel * Time.fixedDeltaTime);
            flat = flat.normalized * s;
            Body.angularVelocity = Vector3.Cross(Vector3.up, flat) / Radius; // качение без проскальзывания
        }
        flat = Vector3.ClampMagnitude(flat, maxSpeed);
        Body.linearVelocity = new Vector3(flat.x, v.y, flat.z);
    }

    // ------------------------------------------------------------ команды и прогнозы

    /// <summary>Удар: направление, скорость (м/с), подъём (вертикальная скорость), вращение (−1…1).</summary>
    public void Kick(Vector3 dir, float power, float lift, Player by, float side = 0f, float top = 0f)
    {
        Body.isKinematic = false;
        if (Owner != null && Owner != by) Owner.OnLostBall();   // выбили подкатом — бывший владелец не подхватит сразу
        Owner = null;
        lastTouch = by;
        dir = Flat(dir).normalized;
        Body.linearVelocity = dir * power + Vector3.up * lift;
        Body.angularVelocity = Vector3.zero;
        sideSpin = side;
        topSpin = top;
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
        sideSpin = topSpin = 0f;
        pos.y = Radius;
        Body.position = pos;
        transform.position = pos;
    }

    /// <summary>Где мяч окажется: точка приземления (если в воздухе) или остановки (если катится).</summary>
    public Vector3 PredictRest()
    {
        Vector3 p = Body.position, v = Body.linearVelocity;
        Vector3 flat = Flat(v);
        if (!Grounded && v.y > -20f)
        {
            float g = 9.81f, h = Mathf.Max(p.y - Radius, 0f);
            float t = (v.y + Mathf.Sqrt(v.y * v.y + 2f * g * h)) / g;
            return Flat(p) + flat * t * 0.85f;                          // 0.85 — поправка на сопротивление воздуха
        }
        float s = flat.magnitude;
        return s < 0.1f ? Flat(p) : Flat(p) + flat / s * (s * s / (2f * rollDecel));
    }

    void OnTriggerEnter(Collider other)
    {
        if (MatchManager.I != null) MatchManager.I.OnBallTrigger(other);
    }

    // Удар в штангу/перекладину — встряска камеры
    void OnCollisionEnter(Collision c)
    {
        if (MatchManager.I != null && c.collider.name == "Post" && c.relativeVelocity.magnitude > 8f)
        {
            MatchManager.I.Shake(0.3f);
            MatchManager.I.Flash("ШТАНГА!");
            MatchManager.I.OnLooseBall();
        }
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
}
