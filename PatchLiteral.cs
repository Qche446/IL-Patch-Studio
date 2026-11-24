using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mono.Cecil.Cil;

namespace ILPatchStudio;

internal enum LiteralKind
{
    Int, UInt, Long, ULong, Float, Double, String, Bool, Char
}

internal readonly record struct CilValue(Code Opcode, long Bits, string? String = null)
{
    public string CSharpOperand => Opcode switch
    {
        Code.Ldc_I4 => ((int)Bits).ToString(CultureInfo.InvariantCulture),
        Code.Ldc_I8 => Bits.ToString(CultureInfo.InvariantCulture) + "L",
        Code.Ldc_R4 => FormatFloat(BitConverter.Int32BitsToSingle((int)Bits)),
        Code.Ldc_R8 => FormatDouble(BitConverter.Int64BitsToDouble(Bits)),
        Code.Ldstr => CodeText.Quote(String!),
        _ => throw new InvalidOperationException()
    };

    public string Predicate => Opcode switch
    {
        Code.Ldc_I4 => $"i.MatchLdcI4({CSharpOperand})",
        Code.Ldc_I8 => $"i.MatchLdcI8({CSharpOperand})",
        Code.Ldc_R4 when Bits == 0 || Bits == int.MinValue || !float.IsFinite(BitConverter.Int32BitsToSingle((int)Bits))
            => $"i.MatchLdcR4(out float value) && BitConverter.SingleToInt32Bits(value) == {Bits}",
        Code.Ldc_R4 => $"i.MatchLdcR4({CSharpOperand})",
        Code.Ldc_R8 when Bits == 0 || Bits == long.MinValue || !double.IsFinite(BitConverter.Int64BitsToDouble(Bits))
            => $"i.MatchLdcR8(out double value) && BitConverter.DoubleToInt64Bits(value) == {Bits}L",
        Code.Ldc_R8 => $"i.MatchLdcR8({CSharpOperand})",
        Code.Ldstr => $"i.MatchLdstr({CSharpOperand})",
        _ => throw new InvalidOperationException()
    };

    public static CilValue? Read(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => new(Code.Ldc_I4, -1),
        Code.Ldc_I4_0 => new(Code.Ldc_I4, 0),
        Code.Ldc_I4_1 => new(Code.Ldc_I4, 1),
        Code.Ldc_I4_2 => new(Code.Ldc_I4, 2),
        Code.Ldc_I4_3 => new(Code.Ldc_I4, 3),
        Code.Ldc_I4_4 => new(Code.Ldc_I4, 4),
        Code.Ldc_I4_5 => new(Code.Ldc_I4, 5),
        Code.Ldc_I4_6 => new(Code.Ldc_I4, 6),
        Code.Ldc_I4_7 => new(Code.Ldc_I4, 7),
        Code.Ldc_I4_8 => new(Code.Ldc_I4, 8),
        Code.Ldc_I4_S => new(Code.Ldc_I4, (sbyte)instruction.Operand),
        Code.Ldc_I4 => new(Code.Ldc_I4, (int)instruction.Operand),
        Code.Ldc_I8 => new(Code.Ldc_I8, (long)instruction.Operand),
        Code.Ldc_R4 => new(Code.Ldc_R4, BitConverter.SingleToInt32Bits((float)instruction.Operand)),
        Code.Ldc_R8 => new(Code.Ldc_R8, BitConverter.DoubleToInt64Bits((double)instruction.Operand)),
        Code.Ldstr => new(Code.Ldstr, 0, (string)instruction.Operand),
        _ => null
    };

    private static string FormatFloat(float value)
    {
        if (float.IsNaN(value))
            return "float.NaN";
        if (float.IsInfinity(value))
            return value > 0 ? "float.PositiveInfinity" : "float.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "f";
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            return "double.NaN";
        if (double.IsInfinity(value))
            return value > 0 ? "double.PositiveInfinity" : "double.NegativeInfinity";
        return value.ToString("R", CultureInfo.InvariantCulture) + "d";
    }
}

internal readonly record struct PatchLiteral(LiteralKind Kind, CilValue Cil)
{
    public string Type => Kind.ToString().ToLowerInvariant();

    public string Text => Kind switch
    {
        LiteralKind.Bool => Cil.Bits == 0 ? "false" : "true",
        LiteralKind.Char => $"U+{Cil.Bits:X4}",
        LiteralKind.UInt => unchecked((uint)Cil.Bits).ToString(CultureInfo.InvariantCulture) + "u",
        LiteralKind.ULong => unchecked((ulong)Cil.Bits).ToString(CultureInfo.InvariantCulture) + "ul",
        _ => Cil.CSharpOperand
    };

    public static PatchLiteral? FromInstruction(CilValue? value)
    {
        if (value is not { } cil)
            return null;

        LiteralKind kind = cil.Opcode switch
        {
            Code.Ldc_I8 => LiteralKind.Long,
            Code.Ldc_R4 => LiteralKind.Float,
            Code.Ldc_R8 => LiteralKind.Double,
            Code.Ldstr => LiteralKind.String,
            _ => LiteralKind.Int
        };
        return new PatchLiteral(kind, cil);
    }

    public static PatchLiteral? FromSource(object? value) => value switch
    {
        bool flag => new(LiteralKind.Bool, new(Code.Ldc_I4, flag ? 1 : 0)),
        char character => new(LiteralKind.Char, new(Code.Ldc_I4, character)),
        uint unsigned => new(LiteralKind.UInt, new(Code.Ldc_I4, unchecked((int)unsigned))),
        ulong unsigned => new(LiteralKind.ULong, new(Code.Ldc_I8, unchecked((long)unsigned))),
        int integer => new(LiteralKind.Int, new(Code.Ldc_I4, integer)),
        long integer => new(LiteralKind.Long, new(Code.Ldc_I8, integer)),
        float single => new(LiteralKind.Float, new(Code.Ldc_R4, BitConverter.SingleToInt32Bits(single))),
        double real => new(LiteralKind.Double, new(Code.Ldc_R8, BitConverter.DoubleToInt64Bits(real))),
        string text => new(LiteralKind.String, new(Code.Ldstr, 0, text)),
        _ => null
    };

    public PatchLiteral? Parse(string text)
    {
        string input = text.Trim();
        var culture = CultureInfo.InvariantCulture;
        return Kind switch
        {
            LiteralKind.Int when int.TryParse(input, NumberStyles.Integer, culture, out int value) => FromSource(value),
            LiteralKind.Long when long.TryParse(input.TrimEnd('l', 'L'), NumberStyles.Integer, culture, out long value) => FromSource(value),
            LiteralKind.UInt when uint.TryParse(input.TrimEnd('u', 'U'), NumberStyles.Integer, culture, out uint value) => FromSource(value),
            LiteralKind.ULong when ulong.TryParse(TrimUnsignedLongSuffix(input), NumberStyles.Integer, culture, out ulong value) => FromSource(value),
            LiteralKind.Float when TryParseFloat(input, out float value) => FromSource(value),
            LiteralKind.Double when TryParseDouble(input, out double value) => FromSource(value),
            LiteralKind.Bool when bool.TryParse(input, out bool value) => FromSource(value),
            LiteralKind.Char when text.Length == 1 => FromSource(text[0]),
            LiteralKind.Char when input.StartsWith("U+", StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(input.AsSpan(2), NumberStyles.AllowHexSpecifier, culture, out ushort value) => FromSource((char)value),
            LiteralKind.String => ParseString(input),
            _ => null
        };
    }

    private static string TrimUnsignedLongSuffix(string text)
    {
        if (text.EndsWith("ul", StringComparison.OrdinalIgnoreCase) || text.EndsWith("lu", StringComparison.OrdinalIgnoreCase))
            return text[..^2];
        return text.TrimEnd('u', 'U', 'l', 'L');
    }

    private static PatchLiteral? ParseString(string input)
    {
        if (!input.StartsWith('"'))
            return null;

        try
        {
            return FromSource(JsonSerializer.Deserialize<string>(input));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseFloat(string text, out float result)
        => float.TryParse(RealText(text, "float.", 'f'), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            && (!float.IsInfinity(result) || text.Contains("Infinity", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseDouble(string text, out double result)
        => double.TryParse(RealText(text, "double.", 'd'), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            && (!double.IsInfinity(result) || text.Contains("Infinity", StringComparison.OrdinalIgnoreCase));

    private static string RealText(string text, string prefix, char suffix)
    {
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            text = text[prefix.Length..];
        if (text.Equals("PositiveInfinity", StringComparison.OrdinalIgnoreCase))
            return "Infinity";
        if (text.Equals("NegativeInfinity", StringComparison.OrdinalIgnoreCase))
            return "-Infinity";
        return text.TrimEnd(suffix, char.ToUpperInvariant(suffix));
    }
}

internal static class CodeText
{
    private static readonly JsonSerializerOptions QuotingOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Quote(string text)
    {
        // JSON 编码会把独立代理项替换成 U+FFFD，CIL 字符串需要保留这些 UTF-16 码元。
        if (!text.Any(char.IsSurrogate))
            return JsonSerializer.Serialize(text, QuotingOptions);

        var result = new System.Text.StringBuilder("\"");
        foreach (char character in text)
        {
            if (char.IsSurrogate(character))
                result.Append($"\\u{(int)character:x4}");
            else
                result.Append(JsonSerializer.Serialize(character.ToString(), QuotingOptions).AsSpan(1..^1));
        }
        return result.Append('"').ToString();
    }
}

