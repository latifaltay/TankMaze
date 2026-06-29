using TankMaze.Server.Network;

namespace TankMaze.Server.Game;

public class GameLoopService : BackgroundService
{
    private readonly GameRoomManager _roomManager;
    private readonly SocketHandler _socketHandler;

    public GameLoopService(GameRoomManager roomManager, SocketHandler socketHandler)
    {
        _roomManager = roomManager;
        _socketHandler = socketHandler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickRate = TimeSpan.FromMilliseconds(16);
        using var timer = new PeriodicTimer(tickRate);

        while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested)
        {
            foreach (var room in _roomManager.GetAllRooms())
            {
                room.Simulation.Update();

                if (room.Simulation.GridDirty)
                {
                    await _socketHandler.BroadcastMapUpdate(room);
                    room.Simulation.ClearGridDirty();
                }

                await _socketHandler.BroadcastState(room);
                await _socketHandler.BroadcastLeaderboard(room);
            }
        }
    }
}
