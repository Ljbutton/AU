namespace TournamentTracker.Tests;

public static class TextExtensions
{
    /// <summary>A caster text with its name tags ("[[6|Jake]]") as plain names.</summary>
    public static string Plain(this string s) => TournamentTracker.App.Broadcast.NameTag.Plain(s);
}

