using System.Reflection;
using System.Reflection.Emit;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ProtectedRecordArchitectureTests
{
    // Add future approved Forge command/query handlers individually after independent review.
    // No namespace-wide exemption for agents, briefing, snapshots, workers or devices.
    private static readonly HashSet<Type> Allowed = [typeof(IProtectedRecordProtector), typeof(ProtectedRecordProtector),
        typeof(ForgeProtectedJson), typeof(PostgresPrivacyStore), typeof(InMemoryPrivacyStore), typeof(Program)];
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(x => x.Value);

    [Fact]
    public void Protected_key_consumers_in_production_are_explicitly_allowlisted()
    {
        var assemblies = new[] { typeof(IProtectedRecordProtector).Assembly, typeof(ProtectedRecordProtector).Assembly, typeof(Program).Assembly };
        var forbidden = assemblies.SelectMany(a => a.GetTypes()).Where(t => !Allowed.Contains(Root(t)))
            .SelectMany(t => Dependencies(t).Select(member => $"{t.FullName}: {member}")).ToArray();
        Assert.True(forbidden.Length == 0, "Protected key access requires independent architecture review: " + string.Join("; ", forbidden));
    }

    [Fact]
    public void Protected_key_guard_detects_constructor_injection_and_hidden_service_resolution()
    {
        Assert.Contains(Dependencies(typeof(InjectedConsumer)), x => x.Contains("signature", StringComparison.Ordinal));
        Assert.Contains(Dependencies(typeof(ServiceLocatorConsumer)), x => x.Contains("IL", StringComparison.Ordinal));
        Assert.Contains(Dependencies(typeof(TypeLocatorConsumer)), x => x.Contains("IL", StringComparison.Ordinal));
    }

    private static Type Root(Type type) { while (type.DeclaringType is { } parent) type = parent; return type; }
    private static bool Sensitive(Type? type) => type is not null && (type == typeof(IProtectedRecordProtector) || type == typeof(ProtectedRecordProtector) ||
        type.HasElementType && Sensitive(type.GetElementType()) || type.IsGenericType && type.GetGenericArguments().Any(Sensitive));
    private static bool Sensitive(MemberInfo? member) => member switch {
        Type type => Sensitive(type),
        FieldInfo field => Sensitive(field.DeclaringType) || Sensitive(field.FieldType),
        MethodBase method => Signature(method),
        _ => false
    };
    private static bool Signature(MethodBase method) => Sensitive(method.DeclaringType) ||
        method.GetParameters().Any(p => Sensitive(p.ParameterType)) || method is MethodInfo info &&
        (Sensitive(info.ReturnType) || info.GetGenericArguments().Any(Sensitive) ||
         info.GetGenericArguments().Where(x => x.IsGenericParameter).SelectMany(x => x.GetGenericParameterConstraints()).Any(Sensitive));

    private static IEnumerable<string> Dependencies(Type type)
    {
        if (Sensitive(type.BaseType) || type.GetInterfaces().Any(Sensitive)) yield return "type signature";
        foreach (var field in type.GetFields(Declared).Where(f => Sensitive(f.FieldType))) yield return field.Name + " field signature";
        foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared))) {
            if (Signature(method)) yield return method.Name + " signature";
            if (method.GetMethodBody() is not { } body) continue;
            if (body.LocalVariables.Any(x => Sensitive(x.LocalType))) yield return method.Name + " local signature";
            foreach (var token in MemberTokens(body.GetILAsByteArray()!)) {
                var member = method.Module.ResolveMember(token, type.GetGenericArguments(), method is MethodInfo m ? m.GetGenericArguments() : Type.EmptyTypes);
                if (Sensitive(member)) yield return method.Name + " IL reference";
            }
        }
    }

    private static IEnumerable<int> MemberTokens(byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length;) {
            short code = bytes[offset++];
            if (code == 0xfe) code = unchecked((short)(0xfe00 | bytes[offset++]));
            var operand = Opcodes[code].OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
                yield return BitConverter.ToInt32(bytes, offset);
            offset += operand switch {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                    OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                _ => throw new InvalidOperationException("Unsupported IL operand in protected dependency guard.")
            };
        }
    }

    private sealed class InjectedConsumer(IProtectedRecordProtector protector) { public bool Check() => protector.Ready; }
    private static class ServiceLocatorConsumer { public static object Resolve(IServiceProvider services) => services.GetRequiredService<IProtectedRecordProtector>(); }
    private static class TypeLocatorConsumer { public static object? Resolve(IServiceProvider services) => services.GetService(typeof(IProtectedRecordProtector)); }
}
