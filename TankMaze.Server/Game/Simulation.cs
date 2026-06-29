using System.Collections.Concurrent;

namespace TankMaze.Server.Game;

public static class CellType
{
    public const byte Empty = 0;
    public const byte Wall = 1;
    public const byte Destructible = 2;
    public const byte Bush = 3;
}

public struct PlayerState
{
    public uint Id;
    public float X, Y;
    public byte Angle;
    public int Health;
    public int MaxHealth;
    public long LastFireMs;
}

public struct Bullet { public float X, Y; public float DirX, DirY; public int LifeTime; public uint OwnerId; }

public class Simulation
{
    public const float WorldWidth = 4000f;
    public const float WorldHeight = 4000f;
    public const float TankSize = 50f;
    public const float GridSize = 80f;
    public const float ViewRange = 1500f;

    public const int GridWidth = (int)(WorldWidth / GridSize);
    public const int GridHeight = (int)(WorldHeight / GridSize);

    public byte[,] Grid = new byte[GridWidth, GridHeight];
    public ConcurrentDictionary<uint, PlayerState> Players = new();
    public List<Bullet> Bullets = new();
    public List<Pickup> Pickups = new();

    private ConcurrentDictionary<uint, byte> _inputs = new();
    private int _initialDestructibleCount;
    private int _tickCounter;

    public bool GridDirty { get; private set; }
    public event Action<uint, uint>? PlayerEliminated;
    public Func<uint, int>? GetScore { get; set; }
    public Action<uint, int>? AddScore { get; set; }

    public Simulation()
    {
        GenerateMaze();
        _initialDestructibleCount = CountDestructibleWalls();
        SeedPickups();
    }

    private void GenerateMaze()
    {
        Random rnd = new Random();
        for (int x = 0; x < GridWidth; x++)
        {
            for (int y = 0; y < GridHeight; y++)
            {
                if (x == 0 || y == 0 || x == GridWidth - 1 || y == GridHeight - 1) Grid[x, y] = CellType.Wall;
                else
                {
                    int roll = rnd.Next(100);
                    if (roll < 55) Grid[x, y] = CellType.Empty;
                    else if (roll < 75) Grid[x, y] = CellType.Wall;
                    else if (roll < 85) Grid[x, y] = CellType.Destructible;
                    else Grid[x, y] = CellType.Bush;
                }
            }
        }
    }

    public void HandleInput(uint id, byte keys)
    {
        if (Players.ContainsKey(id)) _inputs[id] = keys;
    }

    public bool Fire(uint playerId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return false;

        long now = Environment.TickCount64;
        if (now - p.LastFireMs < GameBalance.FireCooldownMs) return false;

        float dx = 0, dy = 0;
        if (p.Angle == 0) dx = 1;
        else if (p.Angle == 1) dy = 1;
        else if (p.Angle == 2) dx = -1;
        else if (p.Angle == 3) dy = -1;

        p.LastFireMs = now;
        Players[playerId] = p;
        Bullets.Add(new Bullet { X = p.X + TankSize / 2, Y = p.Y + TankSize / 2, DirX = dx, DirY = dy, LifeTime = 60, OwnerId = playerId });
        return true;
    }

    public bool SpawnPlayer(uint playerId)
    {
        if (Players.ContainsKey(playerId)) return false;
        int score = GetScore?.Invoke(playerId) ?? 0;
        int maxHp = GameBalance.GetMaxHealth(score);
        var (x, y) = FindSpawnPosition();
        Players[playerId] = new PlayerState
        {
            Id = playerId,
            X = x,
            Y = y,
            Angle = 0,
            Health = maxHp,
            MaxHealth = maxHp,
            LastFireMs = 0
        };
        _inputs.TryRemove(playerId, out _);
        return true;
    }

    public void RemovePlayer(uint playerId)
    {
        Players.TryRemove(playerId, out _);
        _inputs.TryRemove(playerId, out _);
    }

    public void Update()
    {
        _tickCounter++;
        UpdateBullets();
        UpdateMovement();
        UpdatePickups();
        if (_tickCounter % GameBalance.PickupSpawnIntervalTicks == 0) TrySpawnPickups();
        if (_tickCounter % GameBalance.WallRegenIntervalTicks == 0) TryRegenerateWalls();
    }

    private void UpdateBullets()
    {
        for (int i = Bullets.Count - 1; i >= 0; i--)
        {
            var b = Bullets[i];
            b.X += b.DirX * 12f;
            b.Y += b.DirY * 12f;
            b.LifeTime--;

            if (b.LifeTime <= 0)
            {
                Bullets.RemoveAt(i);
                continue;
            }

            int gx = (int)(b.X / GridSize);
            int gy = (int)(b.Y / GridSize);

            if (gx >= 0 && gx < GridWidth && gy >= 0 && gy < GridHeight)
            {
                byte cell = Grid[gx, gy];
                if (cell == CellType.Wall || cell == CellType.Destructible)
                {
                    if (cell == CellType.Destructible) { Grid[gx, gy] = CellType.Empty; GridDirty = true; }
                    Bullets.RemoveAt(i);
                    continue;
                }
            }

            bool hitPlayer = false;
            int attackerScore = GetScore?.Invoke(b.OwnerId) ?? 0;
            int rawDamage = GameBalance.CalcOutgoingDamage(attackerScore);

            foreach (var pId in Players.Keys)
            {
                if (pId == b.OwnerId) continue;
                var p = Players[pId];
                float pcx = p.X + TankSize / 2f, pcy = p.Y + TankSize / 2f;
                if (Math.Abs(b.X - pcx) < TankSize / 2f && Math.Abs(b.Y - pcy) < TankSize / 2f)
                {
                    int defenderScore = GetScore?.Invoke(pId) ?? 0;
                    int damage = GameBalance.CalcDamageTaken(rawDamage, defenderScore);
                    p.Health -= damage;

                    if (p.Health <= 0)
                    {
                        Players.TryRemove(pId, out _);
                        _inputs.TryRemove(pId, out _);
                        PlayerEliminated?.Invoke(pId, b.OwnerId);
                    }
                    else Players[pId] = p;

                    hitPlayer = true;
                    break;
                }
            }

            if (hitPlayer) Bullets.RemoveAt(i);
            else Bullets[i] = b;
        }
    }

    private void UpdateMovement()
    {
        foreach (var id in Players.Keys.ToList())
        {
            if (!_inputs.TryGetValue(id, out byte keys)) continue;
            var p = Players[id];
            float newX = p.X, newY = p.Y;
            if ((keys & 0x01) != 0) { newY -= 5f; p.Angle = 3; }
            else if ((keys & 0x02) != 0) { newY += 5f; p.Angle = 1; }
            else if ((keys & 0x04) != 0) { newX -= 5f; p.Angle = 2; }
            else if ((keys & 0x08) != 0) { newX += 5f; p.Angle = 0; }
            if (!IsColliding(newX, newY)) { p.X = newX; p.Y = newY; }
            Players[id] = p;
        }
    }

    private void UpdatePickups()
    {
        if (Pickups.Count == 0 || Players.Count == 0) return;

        for (int i = Pickups.Count - 1; i >= 0; i--)
        {
            var pickup = Pickups[i];
            bool collected = false;

            foreach (var id in Players.Keys)
            {
                var p = Players[id];
                float pcx = p.X + TankSize / 2f, pcy = p.Y + TankSize / 2f;
                if (Math.Abs(pickup.X - pcx) > GridSize * 0.45f || Math.Abs(pickup.Y - pcy) > GridSize * 0.45f) continue;

                if (pickup.Type == PickupType.Score)
                {
                    AddScore?.Invoke(id, GameBalance.ScorePickupValue);
                    int score = GetScore?.Invoke(id) ?? 0;
                    p.MaxHealth = GameBalance.GetMaxHealth(score);
                    if (p.Health > p.MaxHealth) p.Health = p.MaxHealth;
                }
                else if (pickup.Type == PickupType.Health)
                {
                    p.Health = Math.Min(p.MaxHealth, p.Health + GameBalance.HealthPickupValue);
                }

                Players[id] = p;
                collected = true;
                break;
            }

            if (collected) Pickups.RemoveAt(i);
        }
    }

    private void SeedPickups()
    {
        for (int i = 0; i < 20; i++) TryAddPickup(PickupType.Score);
        for (int i = 0; i < 3; i++) TryAddPickup(PickupType.Health);
    }

    private void TrySpawnPickups()
    {
        int scoreCount = Pickups.Count(p => p.Type == PickupType.Score);
        int healthCount = Pickups.Count(p => p.Type == PickupType.Health);

        if (scoreCount < GameBalance.MaxScorePickups && Random.Shared.NextDouble() < 0.85)
            TryAddPickup(PickupType.Score);

        if (healthCount < GameBalance.MaxHealthPickups && Random.Shared.NextDouble() < GameBalance.HealthPickupSpawnChance)
            TryAddPickup(PickupType.Health);
    }

    private void TryAddPickup(byte type)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (!TryRandomWalkableCell(out int gx, out int gy)) return;
            float x = gx * GridSize + GridSize / 2f;
            float y = gy * GridSize + GridSize / 2f;
            if (Pickups.Any(p => Math.Abs(p.X - x) < GridSize && Math.Abs(p.Y - y) < GridSize)) continue;
            Pickups.Add(new Pickup { X = x, Y = y, Type = type });
            return;
        }
    }

    private void TryRegenerateWalls()
    {
        if (_initialDestructibleCount <= 0) return;
        int current = CountDestructibleWalls();
        if (current >= _initialDestructibleCount * GameBalance.WallRegenThreshold) return;

        int toAdd = Random.Shared.Next(GameBalance.WallRegenBatchMin, GameBalance.WallRegenBatchMax + 1);
        for (int i = 0; i < toAdd; i++)
        {
            if (!TryRandomWalkableCell(out int gx, out int gy)) break;
            if (Grid[gx, gy] != CellType.Empty) continue;
            Grid[gx, gy] = CellType.Destructible;
            GridDirty = true;
        }
    }

    private bool TryRandomWalkableCell(out int gx, out int gy)
    {
        gx = gy = 0;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            gx = Random.Shared.Next(1, GridWidth - 1);
            gy = Random.Shared.Next(1, GridHeight - 1);
            byte cell = Grid[gx, gy];
            if (cell == CellType.Empty || cell == CellType.Bush) return true;
        }
        return false;
    }

    private int CountDestructibleWalls()
    {
        int count = 0;
        for (int x = 0; x < GridWidth; x++)
            for (int y = 0; y < GridHeight; y++)
                if (Grid[x, y] == CellType.Destructible) count++;
        return count;
    }

    public (float X, float Y) FindSpawnPosition()
    {
        int cx = GridWidth / 2, cy = GridHeight / 2;
        for (int dist = 0; dist < GridWidth + GridHeight; dist++)
        {
            for (int gx = 1; gx < GridWidth - 1; gx++)
            {
                for (int gy = 1; gy < GridHeight - 1; gy++)
                {
                    if (Math.Abs(gx - cx) + Math.Abs(gy - cy) != dist) continue;
                    float x = gx * GridSize + (GridSize - TankSize) / 2f;
                    float y = gy * GridSize + (GridSize - TankSize) / 2f;
                    if (!IsColliding(x, y)) return (x, y);
                }
            }
        }
        return (GridSize, GridSize);
    }

    public bool IsColliding(float newX, float newY)
    {
        float margin = 5f;
        int left = (int)((newX + margin) / GridSize);
        int right = (int)((newX + TankSize - margin) / GridSize);
        int top = (int)((newY + margin) / GridSize);
        int bottom = (int)((newY + TankSize - margin) / GridSize);
        return IsWall(left, top) || IsWall(right, top) || IsWall(left, bottom) || IsWall(right, bottom);
    }

    private bool IsWall(int x, int y) =>
        x < 0 || x >= GridWidth || y < 0 || y >= GridHeight ||
        Grid[x, y] == CellType.Wall || Grid[x, y] == CellType.Destructible;

    public bool IsInRange(PlayerState p1, PlayerState p2) =>
        Math.Abs(p1.X - p2.X) < ViewRange && Math.Abs(p1.Y - p2.Y) < ViewRange;

    public void ClearGridDirty() => GridDirty = false;
}
