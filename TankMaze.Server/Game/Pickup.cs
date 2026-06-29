namespace TankMaze.Server.Game;

public static class PickupType
{
    public const byte Score = 1;
    public const byte Health = 2;
}

public struct Pickup
{
    public float X;
    public float Y;
    public byte Type;
}
