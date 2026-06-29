namespace TankMaze.Server.Game;

public class GameRoomManager
{
    private readonly List<GameRoom> _rooms = new();
    private readonly object _lock = new();
    private int _nextRoomId = 1;

    public Func<uint, int>? GetScore { get; set; }
    public Action<uint, int>? AddScore { get; set; }
    public Action<int, uint, uint>? PlayerEliminated { get; set; }

    public GameRoom AssignRoom()
    {
        lock (_lock)
        {
            foreach (var room in _rooms)
            {
                if (room.HasSpace) return room;
            }

            var created = CreateRoomLocked();
            return created;
        }
    }

    private GameRoom CreateRoomLocked()
    {
        var room = new GameRoom(_nextRoomId++);
        WireSimulation(room);
        _rooms.Add(room);
        return room;
    }

    private void WireSimulation(GameRoom room)
    {
        room.Simulation.GetScore = GetScore;
        room.Simulation.AddScore = AddScore;
        room.Simulation.PlayerEliminated += (victim, killer) =>
            PlayerEliminated?.Invoke(room.Id, victim, killer);
    }

    public IReadOnlyList<GameRoom> GetAllRooms()
    {
        lock (_lock) return _rooms.ToList();
    }

    public GameRoom? FindRoom(int roomId)
    {
        lock (_lock) return _rooms.FirstOrDefault(r => r.Id == roomId);
    }

    public GameRoom? FindRoomByPlayer(uint playerId)
    {
        lock (_lock) return _rooms.FirstOrDefault(r => r.Simulation.Players.ContainsKey(playerId));
    }
}
