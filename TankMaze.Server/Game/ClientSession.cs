namespace TankMaze.Server.Game;

public class ClientSession
{
    public ushort Id { get; init; }
    public string Nickname { get; set; } = "";
    public int Score { get; set; }
    public bool IsPlaying { get; set; }
    public int? RoomId { get; set; }

    public static string GenerateRandomNickname()
    {
        string[] adjectives = ["Swift", "Iron", "Shadow", "Blaze", "Storm", "Ghost", "Turbo", "Neon", "Crimson", "Silent"];
        string[] nouns = ["Tank", "Panzer", "Crawler", "Striker", "Raider", "Hunter", "Viper", "Rhino", "Bolt", "Fang"];
        var rnd = Random.Shared;
        return $"{adjectives[rnd.Next(adjectives.Length)]}{nouns[rnd.Next(nouns.Length)]}{rnd.Next(10, 99)}";
    }
}
