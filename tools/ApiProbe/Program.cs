using System.Reflection;
using System.Text.RegularExpressions;

string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
var dlls = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories)
    .Concat(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
    .GroupBy(Path.GetFileName).Select(g => g.OrderByDescending(f => f).First()).ToList();
using var ctx = new MetadataLoadContext(new PathAssemblyResolver(dlls));
const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
string Sig(MethodInfo m) { try { return $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})"; } catch (Exception) { return m.Name + "(?)"; } }
var rx = new Regex("^(RoleTypes|FloatOptionNames|Int32OptionNames|BoolOptionNames|ByteOptionNames|RoleOptionsCollectionV\\d+|IRoleOptionsCollection|RoleOptionsCollection|IGameOptions|NormalGameOptionsV\\d+|RoleRate|IRoleOptions|.*RoleOptionsV\\d+|TaskBarMode|TaskTypes|MapNames|RulesPresets|GameOptionsManager|NumberOption|StringOption|ToggleOption|RoleOptionSetting|GameSettingMenu|RolesSettingsMenu)$");
foreach (var file in new[] { "Assembly-CSharp.dll", "Hazel.dll" })
{
    var path = dlls.FirstOrDefault(f => Path.GetFileName(f) == file);
    Console.WriteLine("ASM " + file + " " + path);
    if (path == null) continue;
    var asm = ctx.LoadFromAssemblyPath(path);
    Type[] types;
    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
    foreach (var t in types.OrderBy(t => t.FullName))
    {
        if (!rx.IsMatch(t.Name)) continue;
        string bas = ""; try { bas = t.BaseType?.Name ?? ""; } catch { }
        Console.WriteLine($"T {t.FullName} : {bas}");
        try
        {
            if (t.IsEnum) { foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static)) Console.WriteLine($"  E {f.Name} = {f.GetRawConstantValue()}"); continue; }
            foreach (var f in t.GetFields(all)) { try { Console.WriteLine($"  F {f.FieldType.Name} {f.Name}"); } catch { Console.WriteLine("  F ? " + f.Name); } }
            foreach (var p in t.GetProperties(all)) { try { Console.WriteLine($"  P {p.PropertyType.Name} {p.Name}"); } catch { Console.WriteLine("  P ? " + p.Name); } }
            foreach (var m in t.GetMethods(all).Take(120)) if (!m.IsSpecialName) Console.WriteLine("  M " + (m.IsStatic ? "static " : "") + Sig(m));
        }
        catch (Exception e) { Console.WriteLine("  ! " + e.Message); }
    }
}
