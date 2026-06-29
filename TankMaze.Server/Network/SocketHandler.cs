using System.Net.WebSockets;
using TankMaze.Server.Game;

namespace TankMaze.Server.Network;

public class SocketHandler
{
    private readonly Dictionary<WebSocket, ushort> _clients = new();
    private readonly object _lock = new();
    private readonly Simulation _simulation;
    private ushort _idCounter = 1;

    public SocketHandler(Simulation simulation) => _simulation = simulation;

    public async Task HandleConnection(WebSocket socket)
    {
        ushort playerId;
        lock (_lock) playerId = _idCounter++;

        var (spawnX, spawnY) = _simulation.FindSpawnPosition();
        _simulation.Players.TryAdd((uint)playerId, new PlayerState { Id = playerId, X = spawnX, Y = spawnY, Angle = 0 });

        lock (_lock) _clients.Add(socket, playerId);

        try
        {
            var welcome = new byte[3];
            welcome[0] = PacketType.Welcome;
            BitConverter.TryWriteBytes(welcome.AsSpan(1, 2), playerId);
            await socket.SendAsync(new ArraySegment<byte>(welcome), WebSocketMessageType.Binary, true, CancellationToken.None);
            
            var mapPacket = new byte[1 + (Simulation.GridWidth * Simulation.GridHeight)];
            mapPacket[0] = PacketType.MapUpdate;
            int index = 1;
            for (int x = 0; x < Simulation.GridWidth; x++)
                for (int y = 0; y < Simulation.GridHeight; y++)
                    mapPacket[index++] = _simulation.Grid[x, y];
            await socket.SendAsync(new ArraySegment<byte>(mapPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch { }

        var buffer = new byte[1024];
        try {
            while (socket.State == WebSocketState.Open) {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Binary && result.Count >= 1)
                {
                    if (buffer[0] == PacketType.PlayerInput) _simulation.HandleInput(playerId, buffer[1]);
                    else if (buffer[0] == PacketType.Fire) _simulation.Fire(playerId);
                }
            }
        } finally {
            lock (_lock) _clients.Remove(socket);
            _simulation.Players.TryRemove((uint)playerId, out _);
        }
    }

    public async Task BroadcastMapUpdate()
    {
        var mapPacket = new byte[1 + (Simulation.GridWidth * Simulation.GridHeight)];
        mapPacket[0] = PacketType.MapUpdate;
        int index = 1;
        for (int x = 0; x < Simulation.GridWidth; x++)
            for (int y = 0; y < Simulation.GridHeight; y++)
                mapPacket[index++] = _simulation.Grid[x, y];

        List<WebSocket> clientSnapshots;
        lock (_lock) clientSnapshots = _clients.Keys.ToList();

        foreach (var client in clientSnapshots)
        {
            if (client.State != WebSocketState.Open) continue;
            try { await client.SendAsync(new ArraySegment<byte>(mapPacket), WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
        }
    }

    public async Task BroadcastState(IEnumerable<PlayerState> allPlayers)
    {
        var playerList = allPlayers.ToList();
        List<KeyValuePair<WebSocket, ushort>> clientSnapshots;
        lock (_lock) clientSnapshots = _clients.ToList();

        foreach (var entry in clientSnapshots)
        {
            var client = entry.Key;
            if (client.State != WebSocketState.Open || !_simulation.Players.TryGetValue((uint)entry.Value, out var clientPlayer)) continue;

            var nearby = playerList.Where(p => _simulation.IsInRange(clientPlayer, p)).ToList();
            var packet = new byte[5 + (nearby.Count * 7) + 2 + (_simulation.Bullets.Count * 8)];
            
            packet[0] = PacketType.StateUpdate;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 2), entry.Value);
            BitConverter.TryWriteBytes(packet.AsSpan(3, 2), (ushort)nearby.Count);

            int offset = 5;
            foreach (var p in nearby) {
                BitConverter.TryWriteBytes(packet.AsSpan(offset, 2), (ushort)p.Id);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 2, 2), (ushort)p.X);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 4, 2), (ushort)p.Y);
                packet[offset + 6] = p.Angle;
                offset += 7;
            }

            BitConverter.TryWriteBytes(packet.AsSpan(offset, 2), (ushort)_simulation.Bullets.Count);
            offset += 2;
            foreach (var b in _simulation.Bullets) {
                BitConverter.TryWriteBytes(packet.AsSpan(offset, 4), b.X);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 4, 4), b.Y);
                offset += 8;
            }
            try { await client.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
        }
    }
}