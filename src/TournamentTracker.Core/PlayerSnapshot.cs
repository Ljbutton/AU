namespace TournamentTracker
{
    /// <summary>What the plugin reads off one player at a point in time.</summary>
    public sealed class PlayerSnapshot
    {
        public byte PlayerId { get; set; }

        /// <summary>Stable identity across games: the friend code, or the name when there is none (local games).</summary>
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public int ColorId { get; set; }
        public string Role { get; set; } = "Crewmate";
        public bool IsImpostor { get; set; }
        public bool IsDead { get; set; }
        public bool Disconnected { get; set; }
        public int TasksCompleted { get; set; }
        public int TasksTotal { get; set; }
        public int LongTasksCompleted { get; set; }
        public int LongTasksTotal { get; set; }

        /// <summary>This is the host (the player running the mod).</summary>
        public bool IsHost { get; set; }

        public bool IsAlive => !IsDead && !Disconnected;

        public static string MakeKey(string? friendCode, string name) =>
            string.IsNullOrWhiteSpace(friendCode) ? "name:" + name.Trim().ToLowerInvariant() : friendCode!.Trim().ToLowerInvariant();

        public override string ToString() => $"{Colors.Name(ColorId)} ({Name})";
    }

    public static class Colors
    {
        private static readonly string[] Names =
        {
            "Red", "Blue", "Green", "Pink", "Orange", "Yellow", "Black", "White", "Purple",
            "Brown", "Cyan", "Lime", "Maroon", "Rose", "Banana", "Gray", "Tan", "Coral",
        };

        public static string Name(int colorId) =>
            colorId >= 0 && colorId < Names.Length ? Names[colorId] : "Color" + colorId;

        public static int? Parse(string text)
        {
            for (int i = 0; i < Names.Length; i++)
                if (string.Equals(Names[i], text, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            if (string.Equals(text, "grey", System.StringComparison.OrdinalIgnoreCase)) return 15;
            return null;
        }
    }

    public static class Maps
    {
        public static string Name(int mapId) => mapId switch
        {
            0 => "The Skeld",
            1 => "MIRA HQ",
            2 => "Polus",
            3 => "dlekS ehT",
            4 => "Airship",
            5 => "The Fungle",
            _ => "Map " + mapId,
        };
    }
}
