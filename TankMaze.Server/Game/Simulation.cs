using System.Collections.Concurrent;

namespace TankMaze.Server.Game;

public static class CellType
{
    public const byte Empty = 0;
    public const byte Wall = 1;           // Gri — yıkılmaz
    public const byte Destructible = 2;   // Kahverengi — mermi ile kırılır
    public const byte Bush = 3;           // Yeşil çalı — geçilebilir, tank altında saklanır
}

public struct PlayerState { public uint Id; public float X, Y; public byte Angle; }
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
    private ConcurrentDictionary<uint, byte> _inputs = new();
    public bool GridDirty { get; private set; }

    public Simulation() { GenerateMaze(); }

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

    public void HandleInput(uint id, byte keys) => _inputs[id] = keys;

    public void Fire(uint playerId)
    {
        if (!Players.TryGetValue(playerId, out var p)) return;
        float dx = 0, dy = 0;
        if (p.Angle == 0) dx = 1; else if (p.Angle == 1) dy = 1; else if (p.Angle == 2) dx = -1; else if (p.Angle == 3) dy = -1;
        Bullets.Add(new Bullet { X = p.X + TankSize/2, Y = p.Y + TankSize/2, DirX = dx, DirY = dy, LifeTime = 60, OwnerId = playerId });
    }

    public void Update()
    {
        for (int i = Bullets.Count - 1; i >= 0; i--)
        {
            var b = Bullets[i];
            b.X += b.DirX * 12f; b.Y += b.DirY * 12f; b.LifeTime--;
            
            // Çarpışma kontrolü ve duvar kırma
            int gx = (int)(b.X / GridSize);
            int gy = (int)(b.Y / GridSize);

            if (b.LifeTime <= 0)
            {
                Bullets.RemoveAt(i);
                continue;
            }

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

            Bullets[i] = b;
        }

        foreach (var id in Players.Keys)
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
        int left = (int)((newX + margin) / GridSize); int right = (int)((newX + TankSize - margin) / GridSize);
        int top = (int)((newY + margin) / GridSize); int bottom = (int)((newY + TankSize - margin) / GridSize);
        return IsWall(left, top) || IsWall(right, top) || IsWall(left, bottom) || IsWall(right, bottom);
    }
    private bool IsWall(int x, int y) => (x < 0 || x >= GridWidth || y < 0 || y >= GridHeight) || Grid[x, y] == CellType.Wall || Grid[x, y] == CellType.Destructible;
    public bool IsInRange(PlayerState p1, PlayerState p2) => Math.Abs(p1.X - p2.X) < ViewRange && Math.Abs(p1.Y - p2.Y) < ViewRange;
    public void ClearGridDirty() => GridDirty = false;
}