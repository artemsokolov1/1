using UnityEngine;

/// <summary>
/// Мяч с аркадной физикой: отскоки (физматериал), трение качения, ограничение скорости,
/// удар с силой и направлением. Также сообщает MatchManager о попадании в триггер ворот.
/// Вешается на Sphere "Ball" (Rigidbody добавится автоматически).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class Ball : MonoBehaviour
{
    [Header("Физика мяча")]
    public float rollDecel = 4f;                 // торможение катящегося мяча, м/с² (трение о траву)
    public float maxSpeed = 30f;                 // потолок горизонтальной скорости
    [Range(0f, 1f)] public float bounciness = 0.6f;

    public Rigidbody Body { get; private set; }
    public float Radius { get; private set; }
    [HideInInspector] public Player lastTouch;   // кто последним коснулся (на будущее: автоголы, статистика)

    void Awake()
    {
        Body = GetComponent<Rigidbody>();
        Body.mass = 0.45f;
        Body.linearDamping = 0.1f;               // лёгкое сопротивление воздуха
        Body.angularDamping = 1f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // быстрый удар не пролетит сквозь штангу

        var col = GetComponent<SphereCollider>();
        Radius = col.radius * transform.lossyScale.x;

        // Maximum — чтобы мяч прыгал от любых поверхностей (бортов, штанг, игроков) одинаково
        col.material = new PhysicsMaterial("Ball")
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
        Vector3 v = Body.linearVelocity;
        Vector3 flat = new Vector3(v.x, 0f, v.z);

        bool grounded = transform.position.y <= Radius + 0.05f;
        if (grounded)
        {
            // Трение качения: равномерно гасим горизонтальную скорость
            float s = Mathf.Max(0f, flat.magnitude - rollDecel * Time.fixedDeltaTime);
            flat = flat.normalized * s;
            // Вращение = качение без проскальзывания (иначе остаточный спин снова разгоняет мяч)
            Body.angularVelocity = Vector3.Cross(Vector3.up, flat) / Radius;
        }

        flat = Vector3.ClampMagnitude(flat, maxSpeed);
        Body.linearVelocity = new Vector3(flat.x, v.y, flat.z);
    }

    /// <summary>Удар: направление (по земле), сила (м/с) и подъём (вертикальная скорость).</summary>
    public void Kick(Vector3 dir, float power, float lift, Player by)
    {
        dir.y = 0f;
        dir.Normalize();
        Body.linearVelocity = dir * power + Vector3.up * lift;
        Body.angularVelocity = Vector3.zero;
        lastTouch = by;
    }

    /// <summary>Поставить мяч в точку и остановить (разводка / вылет за поле).</summary>
    public void ResetTo(Vector3 pos)
    {
        Body.position = pos;
        transform.position = pos;
        Body.linearVelocity = Vector3.zero;
        Body.angularVelocity = Vector3.zero;
        lastTouch = null;
    }

    // Триггеры ворот создаёт MatchManager; он же решает, чей это гол
    void OnTriggerEnter(Collider other)
    {
        if (MatchManager.I != null) MatchManager.I.OnBallTrigger(other);
    }
}
