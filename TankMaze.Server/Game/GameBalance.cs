namespace TankMaze.Server.Game;

public static class GameBalance
{
    public const int BaseHealth = 100;
    public const int BaseDamage = 20;
    public const int MinDamageTaken = 12;
    public const int KillScore = 100;

    public const int FireCooldownMs = 750;

    public const int ScorePickupValue = 25;
    public const int HealthPickupValue = 20;
    public const int MaxScorePickups = 35;
    public const int MaxHealthPickups = 6;
    public const int PickupSpawnIntervalTicks = 45;
    public const float HealthPickupSpawnChance = 0.12f;

    public const int WallRegenIntervalTicks = 300;
    public const float WallRegenThreshold = 0.72f;
    public const int WallRegenBatchMin = 2;
    public const int WallRegenBatchMax = 5;

    public static int GetPowerLevel(int score) => Math.Min(score / 150, 12);

    public static int GetMaxHealth(int score) => BaseHealth + GetPowerLevel(score) * 3;

    public static int CalcOutgoingDamage(int attackerScore)
    {
        return BaseDamage + GetPowerLevel(attackerScore);
    }

    public static int CalcDamageTaken(int rawDamage, int defenderScore)
    {
        int reduction = GetPowerLevel(defenderScore) / 2;
        return Math.Max(MinDamageTaken, rawDamage - reduction);
    }
}
