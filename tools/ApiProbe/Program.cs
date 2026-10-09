using System.Reflection;
using System.Text.RegularExpressions;

string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var dlls = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
    .Concat(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
    .GroupBy(Path.GetFileName).Select(g => g.OrderByDescending(f => f).First()).ToList();
using var ctx = new MetadataLoadContext(new PathAssemblyResolver(dlls));
const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
string Sig(MethodInfo m) { try { return $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})"; } catch (Exception) { return m.Name + "(?)"; } }
void Dump(string file, Regex typeRx, Regex memRx, Regex? memType)
{
    var path = dlls.FirstOrDefault(f => Path.GetFileName(f) == file);
    Console.WriteLine("ASM " + file + " " + path);
    if (path == null) return;
    var asm = ctx.LoadFromAssemblyPath(path);
    Type[] types;
    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
    foreach (var t in types.OrderBy(t => t.FullName))
    {
        bool typeHit = typeRx.IsMatch(t.Name);
        bool memHit = memType != null && memType.IsMatch(t.Name);
        if (!typeHit && !memHit) continue;
        var lines = new List<string>();
        try
        {
            foreach (var f in t.GetFields(all)) if (typeHit || memRx.IsMatch(f.Name)) { try { lines.Add($"  F {f.FieldType.Name} {f.Name}"); } catch { lines.Add("  F ? " + f.Name); } }
            foreach (var p in t.GetProperties(all)) if (typeHit || memRx.IsMatch(p.Name)) { try { lines.Add($"  P {p.PropertyType.Name} {p.Name}"); } catch { lines.Add("  P ? " + p.Name); } }
            foreach (var m in t.GetMethods(all)) if (!m.IsSpecialName && (typeHit || memRx.IsMatch(m.Name))) lines.Add("  M " + (m.IsStatic ? "static " : "") + Sig(m));
        }
        catch (Exception e) { lines.Add("  ! " + e.Message); }
        if (lines.Count == 0) continue;
        string bas = ""; try { bas = t.BaseType?.Name ?? ""; } catch { }
        Console.WriteLine($"T {t.FullName} : {bas}");
        foreach (var l in lines.Take(300)) Console.WriteLine(l);
    }
}
Dump("Assembly-CSharp.dll",
    new Regex("^(ChatController|ChatButton|HudManager|PlayerVoteArea|LogicOptions|LogicOptionsNormal|ServerManager|IRegionInfo|StaticHttpRegionInfo|DnsRegionInfo|RegionInfo|ChatBubble|FreeChatInputField|ChatNotification|MedScanMinigame|GameStartManager|LobbyBehaviour|KeyboardJoystick|HudOverrideTask)$"),
    new Regex("Anim|Scan|Visual|Outfit|Cosmetic|Region|Server|Chat|Visible", RegexOptions.IgnoreCase),
    new Regex("^(PlayerControl|AmongUsClient|InnerNetClient|CosmeticsLayer|SkinLayer|PlayerPhysics|ShipStatus|GameManager|NormalGameManager|PlayerAnimations)$"));
Dump("UnityEngine.CoreModule.dll", new Regex("^(AsyncGPUReadbackRequest|AsyncGPUReadback)$"), new Regex("Async", RegexOptions.IgnoreCase), new Regex("^SystemInfo$"));
