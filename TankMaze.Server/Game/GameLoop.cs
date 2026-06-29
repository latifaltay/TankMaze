using TankMaze.Server.Network;

namespace TankMaze.Server.Game;

public class GameLoopService : BackgroundService
{
    private readonly Simulation _simulation;
    private readonly SocketHandler _socketHandler;

    public GameLoopService(Simulation simulation, SocketHandler socketHandler)
    {
        _simulation = simulation;
        _socketHandler = socketHandler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickRate = TimeSpan.FromMilliseconds(16); // ~60 FPS
        using var timer = new PeriodicTimer(tickRate);

        while (await timer.WaitForNextTickAsync(stoppingToken) && !stoppingToken.IsCancellationRequested)
        {
            _simulation.Update();

            if (_simulation.GridDirty)
            {
                await _socketHandler.BroadcastMapUpdate();
                _simulation.ClearGridDirty();
            }

            await _socketHandler.BroadcastState(_simulation.Players.Values);
        }
    }
}