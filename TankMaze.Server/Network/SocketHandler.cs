using System.Net.WebSockets;
using System.Text;
using TankMaze.Server.Game;

namespace TankMaze.Server.Network;

public class SocketHandler
{
    private readonly Dictionary<WebSocket, ushort> _clients = new();
    private readonly Dictionary<ushort, ClientSession> _sessions = new();
    private readonly object _lock = new();
    private readonly GameRoomManager _roomManager;
    private ushort _idCounter = 1;

    public SocketHandler(GameRoomManager roomManager)
    {
        _roomManager = roomManager;
        _roomManager.GetScore = GetSessionScore;
        _roomManager.AddScore = AddSessionScore;
        _roomManager.PlayerEliminated = OnPlayerEliminated;
    }

    private int GetSessionScore(uint playerId)
    {
        lock (_lock)
            return _sessions.TryGetValue((ushort)playerId, out var s) ? s.Score : 0;
    }

    private void AddSessionScore(uint playerId, int amount)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue((ushort)playerId, out var s))
                s.Score += amount;
        }
    }

    public async Task HandleConnection(WebSocket socket)
    {
        ushort playerId;
        ClientSession session;
        lock (_lock)
        {
            playerId = _idCounter++;
            session = new ClientSession { Id = playerId, Nickname = ClientSession.GenerateRandomNickname() };
            _sessions[playerId] = session;
        }

        lock (_lock) _clients.Add(socket, playerId);

        try
        {
            await SendWelcome(socket, playerId, session.Nickname);
        }
        catch { }

        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType != WebSocketMessageType.Binary || result.Count < 1) continue;

                if (buffer[0] == PacketType.PlayerInput && result.Count >= 2)
                    RouteInput(playerId, buffer[1]);
                else if (buffer[0] == PacketType.Fire)
                    RouteFire(playerId);
                else if (buffer[0] == PacketType.SetNickname)
                    HandleSetNickname(playerId, buffer, result.Count);
                else if (buffer[0] == PacketType.Play)
                    await HandlePlayAsync(socket, playerId);
            }
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(socket);
                _sessions.Remove(playerId);
            }
            _roomManager.FindRoomByPlayer(playerId)?.Simulation.RemovePlayer(playerId);
        }
    }

    private void RouteInput(ushort playerId, byte keys)
    {
        var room = _roomManager.FindRoomByPlayer(playerId);
        room?.Simulation.HandleInput(playerId, keys);
    }

    private void RouteFire(ushort playerId)
    {
        var room = _roomManager.FindRoomByPlayer(playerId);
        room?.Simulation.Fire(playerId);
    }

    private void HandleSetNickname(ushort playerId, byte[] buffer, int count)
    {
        if (count < 2) return;
        int len = Math.Min((int)buffer[1], PacketType.MaxNicknameLength);
        if (count < 2 + len) return;
        string nick = Encoding.UTF8.GetString(buffer, 2, len).Trim();
        if (string.IsNullOrWhiteSpace(nick)) nick = ClientSession.GenerateRandomNickname();
        nick = nick.Length > PacketType.MaxNicknameLength ? nick[..PacketType.MaxNicknameLength] : nick;

        lock (_lock)
        {
            if (_sessions.TryGetValue(playerId, out var session))
                session.Nickname = nick;
        }
    }

    private async Task HandlePlayAsync(WebSocket socket, ushort playerId)
    {
        GameRoom room;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(playerId, out var session) || session.IsPlaying) return;
            room = _roomManager.AssignRoom();
            if (!room.Simulation.SpawnPlayer(playerId)) return;
            session.IsPlaying = true;
            session.RoomId = room.Id;
        }

        try
        {
            await SendRoomJoined(socket, room);
            var mapPacket = BuildMapPacket(room);
            await socket.SendAsync(new ArraySegment<byte>(mapPacket), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch { }
    }

    private void OnPlayerEliminated(int roomId, uint victimId, uint killerId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue((ushort)victimId, out var victim))
            {
                victim.IsPlaying = false;
                victim.RoomId = null;
            }
            if (_sessions.TryGetValue((ushort)killerId, out var killer))
                killer.Score += GameBalance.KillScore;
        }

        List<KeyValuePair<WebSocket, ushort>> targets;
        lock (_lock) targets = _clients.Where(c => c.Value == victimId).ToList();
        var diedPacket = new byte[] { PacketType.YouDied };
        foreach (var (ws, _) in targets)
        {
            if (ws.State != WebSocketState.Open) continue;
            try { ws.SendAsync(new ArraySegment<byte>(diedPacket), WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
        }
    }

    public async Task BroadcastMapUpdate(GameRoom room)
    {
        var mapPacket = BuildMapPacket(room);
        var recipients = GetPlayingClientsInRoom(room.Id);

        foreach (var client in recipients)
        {
            if (client.State != WebSocketState.Open) continue;
            try { await client.SendAsync(new ArraySegment<byte>(mapPacket), WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
        }
    }

    public async Task BroadcastState(GameRoom room)
    {
        var playerList = room.Simulation.Players.Values.ToList();
        var recipients = GetPlayingClientEntriesInRoom(room.Id);

        foreach (var (client, playerId) in recipients)
        {
            if (client.State != WebSocketState.Open) continue;
            if (!room.Simulation.Players.TryGetValue(playerId, out var clientPlayer)) continue;

            var nearby = playerList.Where(p => room.Simulation.IsInRange(clientPlayer, p)).ToList();
            var nearbyPickups = room.Simulation.Pickups
                .Where(pk => Math.Abs(clientPlayer.X - pk.X) < Simulation.ViewRange && Math.Abs(clientPlayer.Y - pk.Y) < Simulation.ViewRange)
                .ToList();

            const int entrySize = 11 + PacketType.MaxNicknameLength;
            const int pickupSize = 9;
            var packet = new byte[5 + (nearby.Count * entrySize) + 2 + (room.Simulation.Bullets.Count * 8) + 2 + (nearbyPickups.Count * pickupSize)];

            packet[0] = PacketType.StateUpdate;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 2), (ushort)playerId);
            BitConverter.TryWriteBytes(packet.AsSpan(3, 2), (ushort)nearby.Count);

            int offset = 5;
            foreach (var p in nearby)
            {
                BitConverter.TryWriteBytes(packet.AsSpan(offset, 2), (ushort)p.Id);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 2, 2), (ushort)p.X);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 4, 2), (ushort)p.Y);
                packet[offset + 6] = p.Angle;
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 7, 2), (short)p.Health);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 9, 2), (short)p.MaxHealth);
                WriteNickname(packet, offset + 11, GetNickname((ushort)p.Id));
                offset += entrySize;
            }

            BitConverter.TryWriteBytes(packet.AsSpan(offset,  2), (ushort)room.Simulation.Bullets.Count);
            offset += 2;
            foreach (var b in room.Simulation.Bullets)
            {
                BitConverter.TryWriteBytes(packet.AsSpan(offset, 4), b.X);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 4, 4), b.Y);
                offset += 8;
            }

            BitConverter.TryWriteBytes(packet.AsSpan(offset, 2), (ushort)nearbyPickups.Count);
            offset += 2;
            foreach (var pk in nearbyPickups)
            {
                BitConverter.TryWriteBytes(packet.AsSpan(offset, 4), pk.X);
                BitConverter.TryWriteBytes(packet.AsSpan(offset + 4, 4), pk.Y);
                packet[offset + 8] = pk.Type;
                offset += pickupSize;
            }

            try { await client.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
        }
    }

    public async Task BroadcastLeaderboard(GameRoom room)
    {
        List<(ushort id, string nick, int score)> rows;
        lock (_lock)
            rows = _sessions.Values
                .Where(s => s.IsPlaying && s.RoomId == room.Id)
                .Select(s => (s.Id, s.Nickname, s.Score))
                .OrderByDescending(r => r.Score)
                .ToList();

        int rowSize = 2 + 4 + PacketType.MaxNicknameLength;
        var packet = new byte[1 + 2 + (rows.Count * rowSize)];
        packet[0] = PacketType.Leaderboard;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 2), (ushort)rows.Count);

        int offset = 3;
        foreach (var (id, nick, score) in rows)
        {
            BitConverter.TryWriteBytes(packet.AsSpan(offset, 2), id);
            BitConverter.TryWriteBytes(packet.AsSpan(offset + 2, 4), score);
            WriteNickname(packet, offset + 6, nick);
            offset += rowSize;
        }

        var recipients = GetPlayingClientsInRoom(room.Id);
        foreach (var client in recipients)
        {
            if (client.State != WebSocketState.Open) continue;
            try { await client.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
        }
    }

    private List<WebSocket> GetPlayingClientsInRoom(int roomId)
    {
        lock (_lock)
            return _clients
                .Where(c => _sessions.TryGetValue(c.Value, out var s) && s.IsPlaying && s.RoomId == roomId)
                .Select(c => c.Key)
                .ToList();
    }

    private List<(WebSocket client, uint playerId)> GetPlayingClientEntriesInRoom(int roomId)
    {
        lock (_lock)
            return _clients
                .Where(c => _sessions.TryGetValue(c.Value, out var s) && s.IsPlaying && s.RoomId == roomId)
                .Select(c => (c.Key, (uint)c.Value))
                .ToList();
    }

    private async Task SendWelcome(WebSocket socket, ushort playerId, string nickname)
    {
        var nickBytes = Encoding.UTF8.GetBytes(nickname.PadRight(PacketType.MaxNicknameLength, '\0')[..PacketType.MaxNicknameLength]);
        var welcome = new byte[3 + PacketType.MaxNicknameLength];
        welcome[0] = PacketType.Welcome;
        BitConverter.TryWriteBytes(welcome.AsSpan(1, 2), playerId);
        nickBytes.CopyTo(welcome.AsSpan(3));
        await socket.SendAsync(new ArraySegment<byte>(welcome), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private static async Task SendRoomJoined(WebSocket socket, GameRoom room)
    {
        var packet = new byte[7];
        packet[0] = PacketType.RoomJoined;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 2), (ushort)room.Id);
        BitConverter.TryWriteBytes(packet.AsSpan(3, 2), (ushort)room.PlayerCount);
        BitConverter.TryWriteBytes(packet.AsSpan(5, 2), (ushort)GameRoom.MaxPlayers);
        await socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    private static byte[] BuildMapPacket(GameRoom room)
    {
        var mapPacket = new byte[1 + (Simulation.GridWidth * Simulation.GridHeight)];
        mapPacket[0] = PacketType.MapUpdate;
        int index = 1;
        for (int x = 0; x < Simulation.GridWidth; x++)
            for (int y = 0; y < Simulation.GridHeight; y++)
                mapPacket[index++] = room.Simulation.Grid[x, y];
        return mapPacket;
    }

    private string GetNickname(ushort id)
    {
        lock (_lock)
            return _sessions.TryGetValue(id, out var s) ? s.Nickname : $"Tank{id}";
    }

    private static void WriteNickname(byte[] packet, int offset, string nickname)
    {
        var bytes = Encoding.UTF8.GetBytes(nickname);
        Array.Clear(packet, offset, PacketType.MaxNicknameLength);
        Array.Copy(bytes, 0, packet, offset, Math.Min(bytes.Length, PacketType.MaxNicknameLength));
    }
}
