using System.Text.Json;

namespace TournamentTracker.App.Broadcast
{
    /// <summary>
    /// Reading the lobbies' messages and OBS's answers: a property's value when it's there and of the
    /// right kind, else null (or false). Like TryGetProperty, they throw on an element that isn't an object.
    /// </summary>
    internal static class JsonRead
    {
        public static string? Str(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public static int? Int(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        public static long? Long(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        public static double? Num(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        public static bool? Bool(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        /// <summary>True only when the property is JSON true.</summary>
        public static bool Flag(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
