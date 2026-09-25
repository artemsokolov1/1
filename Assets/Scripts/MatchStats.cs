using System.Collections.Generic;

/// <summary>Гол: кто забил, кто отдал голевой пас, на какой секунде матча.</summary>
public class GoalEvent
{
    public Team team;          // команда, которой засчитан гол
    public string scorer, assist;
    public bool own;           // автогол
    public float time;         // секунд с начала матча
}

/// <summary>Статистика матча по командам (индекс — (int)Team). Собирает MatchManager, показывают пауза и итог матча.</summary>
public class MatchStats
{
    public float[] possession = new float[2];   // секунд владения
    public int[] shots = new int[2], onTarget = new int[2];
    public int[] passes = new int[2], passesDone = new int[2];
    public int[] saves = new int[2], corners = new int[2], fouls = new int[2];
    public List<GoalEvent> goals = new List<GoalEvent>();

    /// <summary>Доля владения команды, %. Пока никто не владел — 50/50.</summary>
    public int PossessionPct(Team t)
    {
        float total = possession[0] + possession[1];
        if (total < 0.01f) return 50;
        int red = (int)(possession[0] / total * 100f + 0.5f);
        return t == Team.Red ? red : 100 - red;
    }

    public int PassPct(Team t)
    {
        int i = (int)t;
        return passes[i] == 0 ? 0 : (int)(passesDone[i] * 100f / passes[i] + 0.5f);
    }

    public static string Clock(float seconds)
    {
        int s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }
}
