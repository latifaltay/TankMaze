namespace TankMaze.Server.Network;

public static class PacketType
{
    public const byte PlayerInput = 0x01;
    public const byte StateUpdate = 0x02;
    public const byte Welcome = 0x03;
    public const byte MapUpdate = 0x04;
    public const byte Fire = 0x05;
    public const byte SetNickname = 0x06;
    public const byte Play = 0x07;
    public const byte Leaderboard = 0x08;
    public const byte YouDied = 0x09;
    public const byte RoomJoined = 0x0A;

    public const int MaxNicknameLength = 12;
}