namespace TankMaze.Server.Network;

public static class PacketType
{
    public const byte PlayerInput = 0x01;
    public const byte StateUpdate = 0x02;
    public const byte Welcome = 0x03;
    public const byte MapUpdate = 0x04;
    public const byte Fire = 0x05; // Ateş etme paketi
}