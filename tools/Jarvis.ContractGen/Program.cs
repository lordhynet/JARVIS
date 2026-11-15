using System.Reflection;
using System.Text;
using Jarvis.Contracts;

if (args.Length != 1) throw new ArgumentException("Pass the generated Kotlin output path.");
var roots = new[] { typeof(RegisterDeviceRequest), typeof(DeviceView), typeof(PrivacyStatus), typeof(AiCapabilityStatus), typeof(CompanionVoiceRequest), typeof(CompanionVoiceSession), typeof(VoiceStartRequest), typeof(VoiceTicket) };
var types = new HashSet<Type>();
void Visit(Type type)
{
    type = Nullable.GetUnderlyingType(type) ?? type;
    if (type.IsArray) { Visit(type.GetElementType()!); return; }
    if (type.IsGenericType) { foreach (var a in type.GenericTypeArguments) Visit(a); return; }
    if (type.Assembly != typeof(Story).Assembly || !types.Add(type)) return;
    foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) Visit(p.PropertyType);
}
foreach (var root in roots) Visit(root);
var nulls = new NullabilityInfoContext();
string Kotlin(Type type, NullabilityInfo? info = null)
{
    var inner = Nullable.GetUnderlyingType(type);
    if (inner is not null) return Kotlin(inner) + "?";
    var name = type == typeof(string) || type == typeof(Guid) || type == typeof(DateTimeOffset) ? "String" :
        type == typeof(int) ? "Int" : type == typeof(long) ? "Long" : type == typeof(bool) ? "Boolean" :
        type == typeof(double) || type == typeof(decimal) ? "Double" :
        type.IsArray ? $"List<{Kotlin(type.GetElementType()!, info?.ElementType)}>" :
        type.IsGenericType ? $"List<{Kotlin(type.GenericTypeArguments[0], info?.GenericTypeArguments[0])}>" : type.Name;
    return name + (!type.IsValueType && info?.ReadState == NullabilityState.Nullable ? "?" : "");
}
string Default(Type type, string mapped) => mapped.EndsWith('?') ? "null" : mapped == "String" ? "\"\"" :
    mapped == "Boolean" ? "false" : mapped == "Int" ? "0" : mapped == "Long" ? "0L" : mapped == "Double" ? "0.0" :
    mapped.StartsWith("List<") ? "emptyList()" : type.IsEnum ? type.Name + "." + Enum.GetNames(type)[0] : type.Name + "()";
var output = new StringBuilder("// Generated from Jarvis.Contracts by Jarvis.ContractGen. Do not edit.\npackage uk.jarvis.mobile.contracts\n\n");
foreach (var type in types.OrderBy(t => t.Name, StringComparer.Ordinal))
{
    if (type.IsEnum) { output.AppendLine($"enum class {type.Name} {{ {string.Join(", ", Enum.GetNames(type))} }}\n"); continue; }
    output.AppendLine($"data class {type.Name}(");
    var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
    foreach (var p in props)
    {
        var name = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
        var mapped = Kotlin(p.PropertyType, nulls.Create(p));
        output.AppendLine($"    val {name}: {mapped} = {Default(p.PropertyType, mapped)},");
    }
    output.AppendLine(")\n");
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllText(args[0], output.ToString().Replace("\r\n", "\n"));
Console.WriteLine($"Generated {types.Count} Kotlin contracts.");

