using System.Reflection;
using System.Reflection.Emit;

namespace PouryaKit.ArchitectureTests;

/// <summary>
/// What the code of an assembly calls and which string literals it loads, read from the IL of every method body, those
/// the compiler generates for async methods, iterators, lambdas and local functions included. Each finding is
/// attributed to the outermost type declared in source. Only what the IL names statically is seen: a call made through
/// reflection, <c>dynamic</c> or a delegate built elsewhere, and text assembled at run time, are not.
/// </summary>
internal static class Il
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static Il()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var opCode = (OpCode)field.GetValue(null)!;
            var value = (ushort)opCode.Value;
            if (value < 0x100)
            {
                OneByte[value] = opCode;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                TwoByte[value & 0xff] = opCode;
            }
        }
    }

    public sealed record Call(Type Owner, MethodBase Caller, MethodBase Callee);

    public sealed record Literal(Type Owner, MethodBase Method, string Text);

    /// <summary>The calls in <paramref name="assembly"/>; a call whose target cannot be resolved throws.</summary>
    public static IEnumerable<Call> CallsIn(Assembly assembly) =>
        from method in MethodsOf(assembly)
        from token in Tokens(method, OperandType.InlineMethod)
        select new Call(OwnerOf(method.DeclaringType!), method, Resolve(method, token));

    public static IEnumerable<Literal> LiteralsIn(Assembly assembly) =>
        from method in MethodsOf(assembly)
        from token in Tokens(method, OperandType.InlineString)
        select new Literal(OwnerOf(method.DeclaringType!), method, method.Module.ResolveString(token));

    /// <summary>The type declared in source that holds <paramref name="type"/>, which may be one the compiler made.</summary>
    public static Type OwnerOf(Type type)
    {
        while (type.DeclaringType is { } declaring)
        {
            type = declaring;
        }

        return type;
    }

    private static IEnumerable<MethodBase> MethodsOf(Assembly assembly)
    {
        const BindingFlags all =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            .Where(method => method.GetMethodBody() is not null);
    }

    private static MethodBase Resolve(MethodBase method, int token)
    {
        var typeArguments = method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        return method.Module.ResolveMethod(token, typeArguments, methodArguments)
            ?? throw new InvalidOperationException($"A call in {method.DeclaringType.FullName}.{method.Name} resolved to nothing.");
    }

    private static IEnumerable<int> Tokens(MethodBase method, OperandType operandType)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var at = 0;
        while (at < il.Length)
        {
            var opCode = il[at] == 0xfe ? TwoByte[il[++at]] : OneByte[il[at]];
            at++;
            var size = opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
                _ => 4,
            };
            if (opCode.OperandType == operandType)
            {
                yield return BitConverter.ToInt32(il, at);
            }

            at += size;
        }
    }
}
