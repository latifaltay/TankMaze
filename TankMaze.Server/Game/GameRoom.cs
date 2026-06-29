namespace TankMaze.Server.Game;

public class GameRoom
{
    public const int MaxPlayers = 100;

    public int Id { get; }
    public Simulation Simulation { get; }

    public GameRoom(int id)
    {
        Id = id;
        Simulation = new Simulation();
    }

    public bool HasSpace => Simulation.Players.Count < MaxPlayers;
    public int PlayerCount => Simulation.Players.Count;
}
