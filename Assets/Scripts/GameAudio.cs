using UnityEngine;

/// <summary>
/// Звук без аудиофайлов: все звуки синтезируются кодом при запуске.
///  - удар по мячу (глухой «тум» + щелчок кожи; громкость и тон — от силы удара), отскок, толчок/падение;
///  - свисток судьи (короткий — фол и начало, длинный — пенальти, три — конец матча);
///  - штанга («звон» из негармоничных обертонов), сетка;
///  - трибуны: постоянный гул (громче, когда мяч у ворот), «О-о-о!» на сейве и промахе рядом, рёв на гол;
///  - щелчок кнопок меню.
/// Громкость — в «Настройки → Звук» (Profile.soundVolume, 0…4).
/// </summary>
public class GameAudio : MonoBehaviour
{
    public static GameAudio I { get; private set; }

    const int Rate = 44100;
    AudioClip kick, whistleShort, whistleLong, whistleEnd, post, net, thud, click, crowdLoop, roar, oh;
    AudioSource[] pool;
    int next;
    AudioSource crowd, reaction;
    float excitement;                 // 0…1: мяч у ворот — трибуны шумят сильнее
    static System.Random rng = new System.Random(7);

    // ------------------------------------------------------------ вызовы из игры

    /// <summary>Удар по мячу. power — скорость мяча, м/с.</summary>
    public static void Kick(float power)
    {
        if (I == null) return;
        float v = Mathf.Clamp01(0.25f + power / 32f);
        I.Play(I.kick, v, Mathf.Lerp(1.15f, 0.85f, Mathf.Clamp01(power / 30f)) * Random.Range(0.95f, 1.05f));
    }

    /// <summary>Отскок мяча от газона/щита. speed — скорость удара, м/с.</summary>
    public static void Bounce(float speed)
    {
        if (I == null || speed < 3f) return;
        I.Play(I.kick, Mathf.Clamp01(speed / 25f) * 0.35f, Random.Range(0.7f, 0.8f));
    }

    public static void Thud(bool heavy)
    {
        if (I == null) return;
        I.Play(I.thud, heavy ? 0.9f : 0.5f, heavy ? 0.8f : Random.Range(1f, 1.15f));
    }

    public static void Post() { if (I != null) I.Play(I.post, 0.9f, Random.Range(0.97f, 1.03f)); }
    public static void Net() { if (I != null) I.Play(I.net, 0.7f, Random.Range(0.9f, 1.1f)); }
    public static void Click() { if (I != null) I.Play(I.click, 0.5f, 1f); }

    /// <summary>Свисток: 0 — короткий, 1 — длинный, 2 — конец матча (три).</summary>
    public static void Whistle(int kind)
    {
        if (I == null) return;
        I.Play(kind == 0 ? I.whistleShort : kind == 1 ? I.whistleLong : I.whistleEnd, 0.55f, 1f);
    }

    /// <summary>Трибуны ахают (сейв, штанга, удар рядом).</summary>
    public static void CrowdOh()
    {
        if (I == null || (I.reaction.isPlaying && I.reaction.clip == I.roar)) return;   // рёв после гола не перебиваем
        I.reaction.clip = I.oh;
        I.reaction.volume = 0.8f;
        I.reaction.pitch = Random.Range(0.95f, 1.05f);
        I.reaction.Play();
    }

    /// <summary>Рёв трибун после гола. forHome — забила твоя команда (громче).</summary>
    public static void CrowdRoar(bool forHome)
    {
        if (I == null) return;
        I.reaction.clip = I.roar;
        I.reaction.volume = forHome ? 1f : 0.55f;
        I.reaction.pitch = 1f;
        I.reaction.Play();
    }

    // ------------------------------------------------------------ жизненный цикл

    void Awake()
    {
        I = this;
        if (FindAnyObjectByType<AudioListener>() == null)
        {
            Camera c = Camera.main;
            (c != null ? c.gameObject : gameObject).AddComponent<AudioListener>();
        }

        pool = new AudioSource[8];
        for (int i = 0; i < pool.Length; i++) pool[i] = NewSource(false);
        crowd = NewSource(true);
        reaction = NewSource(false);

        kick = Make(0.22f, 0.9f, KickSample);
        thud = Make(0.25f, 0.8f, ThudSample);
        whistleShort = Whistle(0.28f, 1);
        whistleLong = Whistle(0.9f, 1);
        whistleEnd = Whistle(0.35f, 3, 0.5f, 1.1f);
        post = Make(1.2f, 0.6f, PostSample);
        net = Noise(0.35f, 0.01f, 0.12f, 400f, 3000f, 0.5f);
        click = Make(0.04f, 0.4f, t => Mathf.Sin(2f * Mathf.PI * 1400f * t) * Mathf.Exp(-t / 0.008f) * 0.6f);
        crowdLoop = CrowdLoop(6f);
        roar = Noise(4f, 0.25f, 2.2f, 250f, 2200f, 0.9f, 0.9f);
        oh = Noise(1.6f, 0.2f, 0.9f, 200f, 900f, 0.8f, 0.6f);

        crowd.clip = crowdLoop;
        crowd.volume = 0f;
        crowd.Play();
    }

    void Update()
    {
        AudioListener.volume = Profile.Current.soundVolume / 4f;

        var mm = MatchManager.I;
        float target = 0.12f;
        if (mm != null && mm.InMatch && mm.ball != null)
        {
            // Мяч у чужих или своих ворот — гул нарастает
            float dGoal = mm.length * 0.5f - Mathf.Abs(mm.ball.transform.position.x);
            target = 0.22f + 0.28f * Mathf.Clamp01(1f - dGoal / 10f);
        }
        excitement = Mathf.MoveTowards(excitement, target, Time.unscaledDeltaTime * 0.25f);
        crowd.volume = excitement;
    }

    AudioSource NewSource(bool loop)
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f;
        return s;
    }

    void Play(AudioClip clip, float volume, float pitch)
    {
        AudioSource s = pool[next];
        next = (next + 1) % pool.Length;
        s.pitch = pitch;
        s.PlayOneShot(clip, volume);
    }

    // ------------------------------------------------------------ синтез

    static float Rand() => (float)(rng.NextDouble() * 2.0 - 1.0);

    /// <summary>Клип из сэмплов; громкость нормируется так, чтобы пик был равен peak (без перегруза).</summary>
    static AudioClip Clip(string name, float[] data, float peak)
    {
        float max = 0f;
        foreach (float x in data) max = Mathf.Max(max, Mathf.Abs(x));
        if (max > 1e-5f) for (int i = 0; i < data.Length; i++) data[i] *= peak / max;
        var c = AudioClip.Create(name, data.Length, 1, Rate, false);
        c.SetData(data, 0);
        return c;
    }

    static AudioClip Make(float seconds, float peak, System.Func<float, float> f)
    {
        var d = new float[(int)(seconds * Rate)];
        for (int i = 0; i < d.Length; i++) d[i] = f((float)i / Rate);
        return Clip("sfx", d, peak);
    }

    // Удар: низкий «тум», тон падает 195 → 55 Гц, + короткий щелчок кожи
    static float KickSample(float t)
    {
        float phase = 2f * Mathf.PI * (55f * t + 140f * 0.02f * (1f - Mathf.Exp(-t / 0.02f)));
        float body = Mathf.Sin(phase) * Mathf.Exp(-t / 0.045f);
        float slap = Rand() * Mathf.Exp(-t / 0.004f) * 0.6f;
        return (body * 0.9f + slap) * 0.9f;
    }

    // Толчок/падение: глухой низкий шум
    static float thudLp;
    static float ThudSample(float t)
    {
        thudLp += (Rand() - thudLp) * 0.08f;
        return (thudLp * 3f + Mathf.Sin(2f * Mathf.PI * 70f * t) * 0.6f) * Mathf.Exp(-t / 0.06f);
    }

    // Штанга: негармоничные обертоны металлической трубы
    static float PostSample(float t)
    {
        float[] f = { 523f, 1443f, 2826f, 4670f };
        float[] a = { 0.5f, 0.3f, 0.18f, 0.1f };
        float[] tau = { 0.5f, 0.3f, 0.18f, 0.1f };
        float s = 0f;
        for (int i = 0; i < f.Length; i++) s += Mathf.Sin(2f * Mathf.PI * f[i] * t) * a[i] * Mathf.Exp(-t / tau[i]);
        return s + Rand() * Mathf.Exp(-t / 0.003f) * 0.4f;
    }

    // Свисток с горошиной: тон ~2.9 кГц, горошина даёт трель (частота и громкость «дрожат» ~35 Гц)
    static AudioClip Whistle(float blast, int count, float gap = 0f, float lastBlast = 0f)
    {
        float total = 0f;
        for (int k = 0; k < count; k++) total += (k == count - 1 && lastBlast > 0f ? lastBlast : blast) + (k < count - 1 ? gap : 0f);
        var d = new float[(int)((total + 0.05f) * Rate)];
        float start = 0f, phase = 0f;
        for (int k = 0; k < count; k++)
        {
            float len = k == count - 1 && lastBlast > 0f ? lastBlast : blast;
            int i0 = (int)(start * Rate), n = (int)(len * Rate);
            for (int i = 0; i < n && i0 + i < d.Length; i++)
            {
                float t = (float)i / Rate;
                float env = Mathf.Clamp01(t / 0.015f) * Mathf.Clamp01((len - t) / 0.04f);
                float trill = Mathf.Sin(2f * Mathf.PI * 35f * t);
                phase += 2f * Mathf.PI * (2900f + 120f * trill) / Rate;
                d[i0 + i] = (Mathf.Sin(phase) * (0.75f + 0.25f * trill) + Rand() * 0.06f) * env * 0.6f;
            }
            start += len + gap;
        }
        return Clip("whistle", d, 0.5f);
    }

    /// <summary>Отфильтрованный шум (полоса lo…hi Гц) с огибающей: подъём attack, спад decay.</summary>
    static AudioClip Noise(float seconds, float attack, float decay, float lo, float hi, float peak, float swell = 0f)
    {
        var d = new float[(int)(seconds * Rate)];
        float aLo = 1f - Mathf.Exp(-2f * Mathf.PI * lo / Rate), aHi = 1f - Mathf.Exp(-2f * Mathf.PI * hi / Rate);
        float lpHi = 0f, lpLo = 0f;
        for (int i = 0; i < d.Length; i++)
        {
            float t = (float)i / Rate;
            lpHi += (Rand() - lpHi) * aHi;
            lpLo += (lpHi - lpLo) * aLo;
            float band = lpHi - lpLo;
            float env = Mathf.Clamp01(t / attack) * Mathf.Exp(-Mathf.Max(0f, t - attack) / decay);
            float wobble = 1f + swell * 0.25f * Mathf.Sin(2f * Mathf.PI * 1.3f * t) * Mathf.Sin(2f * Mathf.PI * 0.4f * t);
            d[i] = band * env * wobble;
        }
        return Clip("noise", d, peak);
    }

    /// <summary>Гул трибун: шум в «голосовой» полосе с медленными волнами громкости, склеенный в бесшовную петлю.</summary>
    static AudioClip CrowdLoop(float seconds)
    {
        int n = (int)(seconds * Rate), fade = Rate / 2;
        var raw = new float[n + fade];
        float aLo = 1f - Mathf.Exp(-2f * Mathf.PI * 180f / Rate), aHi = 1f - Mathf.Exp(-2f * Mathf.PI * 1500f / Rate);
        float lpHi = 0f, lpLo = 0f;
        for (int i = 0; i < raw.Length; i++)
        {
            float t = (float)i / Rate;
            lpHi += (Rand() - lpHi) * aHi;
            lpLo += (lpHi - lpLo) * aLo;
            float waves = 0.8f + 0.12f * Mathf.Sin(2f * Mathf.PI * 0.37f * t) + 0.08f * Mathf.Sin(2f * Mathf.PI * 0.91f * t + 1f);
            raw[i] = (lpHi - lpLo) * waves;
        }
        var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = raw[i];
        for (int i = 0; i < fade; i++)                    // хвост плавно перетекает в начало — щелчка на стыке нет
        {
            float k = (float)i / fade;
            d[i] = raw[i] * k + raw[n + i] * (1f - k);
        }
        return Clip("crowd", d, 0.6f);
    }
}
