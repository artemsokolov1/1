using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Сохранения игрока (PlayerPrefs + JSON): валюта, статистика, прокачка, покупки, настройки, испытания.
/// Profile.Current — единственный экземпляр, меню и матч читают/пишут его и вызывают Save().
/// </summary>
[Serializable]
public class Profile
{
    const string Key = "MiniFootball.Profile.v1";

    public string nickname = "Игрок";
    public string clubName = "ФК Красная Звезда";
    public int coins = 1000;
    public int tokens = 0;

    // статистика
    public int matches, wins, draws, losses, goalsFor, goalsAgainst;

    // клуб и магазин
    public int kit = 0;                                   // индекс в Catalog.Kits
    public int ballSkin = 0;                              // индекс в Catalog.Balls
    public List<int> ownedKits = new List<int> { 0 };
    public List<int> ownedBalls = new List<int> { 0 };
    public string[] playerNames = { "Смирнов", "Кузнецов", "Попов", "Васильев", "Козлов" }; // вратарь, 2 защ., 2 нап.

    // прокачка (0..Catalog.MaxUpgrade)
    public int speedLvl, shotLvl, controlLvl;

    // настройки
    public int matchLength = 0;                           // индекс в Catalog.MatchLengths
    public int difficulty = 1;                            // 0 — легко, 1 — нормально, 2 — сложно
    public int cameraMode = 0;                            // 0 — умная трансляция, 1 — широкая, 2 — изометрия
    public int autoSwitch = 0;                            // 0 — авто, 1 — мячи в воздухе и ничьи, 2 — вручную
    public bool showHints = true;
    public int soundVolume = 3;                           // 0 — выкл … 4 — 100%

    // испытания и тренировки
    public int[] challengeProgress = new int[Catalog.Challenges.Length];
    public bool[] challengeClaimed = new bool[Catalog.Challenges.Length];
    public bool[] practiceDone = new bool[Catalog.Practices.Length];

    static Profile current;
    public static Profile Current => current ?? (current = Load());

    static Profile Load()
    {
        Profile p = null;
        try
        {
            string json = PlayerPrefs.GetString(Key, "");
            if (!string.IsNullOrEmpty(json)) p = JsonUtility.FromJson<Profile>(json);
        }
        catch (Exception e) { Debug.LogWarning("Профиль повреждён, создаю новый: " + e.Message); }
        p = p ?? new Profile();
        p.Repair();
        return p;
    }

    public void Save()
    {
        PlayerPrefs.SetString(Key, JsonUtility.ToJson(this));
        PlayerPrefs.Save();
    }

    public static void ResetAll()
    {
        current = new Profile();
        current.Save();
    }

    /// <summary>Защита от старых/битых сохранений: длины массивов и индексы в допустимых пределах.</summary>
    void Repair()
    {
        Array.Resize(ref challengeProgress, Catalog.Challenges.Length);
        Array.Resize(ref challengeClaimed, Catalog.Challenges.Length);
        Array.Resize(ref practiceDone, Catalog.Practices.Length);
        if (playerNames == null || playerNames.Length != 5) playerNames = new Profile().playerNames;
        if (ownedKits == null || ownedKits.Count == 0) ownedKits = new List<int> { 0 };
        if (ownedBalls == null || ownedBalls.Count == 0) ownedBalls = new List<int> { 0 };
        if (!ownedKits.Contains(kit)) kit = 0;
        if (!ownedBalls.Contains(ballSkin)) ballSkin = 0;
        matchLength = Mathf.Clamp(matchLength, 0, Catalog.MatchLengths.Length - 1);
        difficulty = Mathf.Clamp(difficulty, 0, 2);
        cameraMode = Mathf.Clamp(cameraMode, 0, Catalog.CameraModes.Length - 1);
        autoSwitch = Mathf.Clamp(autoSwitch, 0, Catalog.AutoSwitchModes.Length - 1);
        soundVolume = Mathf.Clamp(soundVolume, 0, 4);
    }

    public int ChallengesReady()
    {
        int n = 0;
        for (int i = 0; i < Catalog.Challenges.Length; i++)
            if (!challengeClaimed[i] && challengeProgress[i] >= Catalog.Challenges[i].target) n++;
        return n;
    }

    public int PracticeDoneCount()
    {
        int n = 0;
        foreach (bool b in practiceDone) if (b) n++;
        return n;
    }

    public void AddChallenge(ChallengeKind kind, int amount)
    {
        for (int i = 0; i < Catalog.Challenges.Length; i++)
            if (Catalog.Challenges[i].kind == kind)
                challengeProgress[i] = Mathf.Min(challengeProgress[i] + amount, Catalog.Challenges[i].target);
    }
}

public enum ChallengeKind { PlayMatches, ScoreGoals, WinMatches, CleanSheet, PracticeDone }
public enum MatchMode { Normal, PracticeFree, PracticeKeeper, PracticeDefense }

/// <summary>Статические справочники: формы, мячи, прокачка, испытания, тренировки.</summary>
public static class Catalog
{
    public struct Item
    {
        public string name; public Color color; public int price; public bool forTokens;
        public Item(string n, Color c, int p, bool t = false) { name = n; color = c; price = p; forTokens = t; }
    }

    public struct Challenge
    {
        public string title; public ChallengeKind kind; public int target; public int reward; public bool rewardTokens;
        public Challenge(string t, ChallengeKind k, int tg, int r, bool tok = false) { title = t; kind = k; target = tg; reward = r; rewardTokens = tok; }
    }

    public struct Practice
    {
        public string title, description; public MatchMode mode; public int goalsNeeded; public float time; public int reward;
        public Practice(string t, string d, MatchMode m, int g, float tm, int r) { title = t; description = d; mode = m; goalsNeeded = g; time = tm; reward = r; }
    }

    public static readonly Item[] Kits =
    {
        new Item("Красная классика", new Color(0.88f, 0.18f, 0.2f), 0),
        new Item("Изумрудная",       new Color(0.1f, 0.65f, 0.35f), 800),
        new Item("Оранжевая",        new Color(1f, 0.5f, 0.1f), 800),
        new Item("Белая",            new Color(0.93f, 0.93f, 0.93f), 1200),
        new Item("Чёрно-золотая",    new Color(0.12f, 0.12f, 0.12f), 2500),
        new Item("Фиолетовая",       new Color(0.55f, 0.25f, 0.85f), 3, true),
    };

    public static readonly Item[] Balls =
    {
        new Item("Классический", Color.white, 0),
        new Item("Жёлтый",       new Color(1f, 0.9f, 0.2f), 500),
        new Item("Оранжевый",    new Color(1f, 0.55f, 0.15f), 500),
        new Item("Салатовый",    new Color(0.7f, 0.95f, 0.15f), 900),
        new Item("Золотой",      new Color(1f, 0.78f, 0.25f), 2, true),
    };

    public static readonly Color OpponentBlue = new Color(0.2f, 0.4f, 0.95f);
    public static readonly Color OpponentAlt = new Color(0.15f, 0.15f, 0.2f);
    public static readonly string[] OpponentNames = { "Орлов", "Волков", "Зайцев", "Лебедев", "Соколов" };
    public static readonly string OpponentClub = "ФК Синие Молнии";

    public static readonly int[] MatchLengths = { 60, 120, 180, 300 };
    public static readonly string[] Difficulties = { "Легко", "Нормально", "Сложно" };
    public static readonly float[] DifficultySpeed = { 0.85f, 1f, 1.12f };
    public static readonly string[] CameraModes = { "Умная трансляция", "Широкая трансляция", "Изометрия" };
    public static readonly string[] AutoSwitchModes = { "Авто", "Мячи в воздухе и ничьи", "Вручную" };

    public const int MaxUpgrade = 5;
    public static int UpgradePrice(int lvl) => 400 + lvl * 350;
    public const int TokenBuyPrice = 2000;    // монет за 1 жетон
    public const int TokenSellPrice = 1500;   // монет за продажу 1 жетона

    public static readonly Challenge[] Challenges =
    {
        new Challenge("Сыграй 5 матчей",           ChallengeKind.PlayMatches, 5, 1000),
        new Challenge("Забей 10 голов",            ChallengeKind.ScoreGoals, 10, 1500),
        new Challenge("Выиграй 3 матча",           ChallengeKind.WinMatches, 3, 2000),
        new Challenge("Матч без пропущенных",      ChallengeKind.CleanSheet, 1, 1, true),
        new Challenge("Пройди 2 тренировки",       ChallengeKind.PracticeDone, 2, 800),
    };

    public static readonly Practice[] Practices =
    {
        new Practice("Свободная тренировка", "Без соперников. Отработай пасы, удары и навесы. Забей 5 голов.",
                     MatchMode.PracticeFree, 5, 120f, 200),
        new Practice("Один на один с вратарём", "Против тебя только вратарь. Забей 3 гола за 90 секунд.",
                     MatchMode.PracticeKeeper, 3, 90f, 400),
        new Practice("Взлом обороны", "Вратарь и два защитника. Забей 3 гола за 2 минуты.",
                     MatchMode.PracticeDefense, 3, 120f, 700),
    };
}
