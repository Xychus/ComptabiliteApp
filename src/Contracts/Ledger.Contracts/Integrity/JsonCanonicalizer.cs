using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ledger.Contracts.Integrity;

/// <summary>
/// Forme canonique JSON selon la RFC 8785 (JCS) : clés triées par unités de code UTF-16, aucun
/// espace, chaînes et nombres sérialisés comme <c>JSON.stringify</c> en ECMAScript (ADR 0006).
/// </summary>
public static class JsonCanonicalizer
{
    public static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        try
        {
            Write(element, builder);
        }
        catch (InvalidOperationException exception)
        {
            // System.Text.Json refuse de décoder un demi-caractère de substitution isolé (interdit par I-JSON).
            throw new FormatException("Chaîne JSON invalide en UTF-16.", exception);
        }

        return builder.ToString();
    }

    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, builder);
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    Write(item, builder);
                    first = false;
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(element.GetString()!, builder);
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(element.TryGetDouble(out var number)
                    ? number
                    : throw new FormatException($"Nombre non représentable en IEEE-754 : {element.GetRawText()}.")));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new FormatException($"Valeur JSON inattendue : {element.ValueKind}.");
        }
    }

    private static void WriteObject(JsonElement element, StringBuilder builder)
    {
        var properties = element.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
        properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        builder.Append('{');
        for (var index = 0; index < properties.Count; index++)
        {
            if (index > 0)
            {
                if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
                {
                    throw new FormatException($"Clé en double : « {properties[index].Name} ».");
                }

                builder.Append(',');
            }

            WriteString(properties[index].Name, builder);
            builder.Append(':');
            Write(properties[index].Value, builder);
        }

        builder.Append('}');
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case < ' ':
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>Algorithme <c>Number::toString</c> d'ECMAScript (RFC 8785, section 3.2.2.3).</summary>
    private static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new FormatException("NaN et l'infini ne sont pas des nombres JSON.");
        }

        if (value == 0)
        {
            return "0";
        }

        if (value < 0)
        {
            return "-" + FormatNumber(-value);
        }

        // Chiffres significatifs les plus courts qui redonnent la même valeur (« R » en .NET Core 3+).
        var (digits, pointPosition) = ShortestDigits(value.ToString("R", CultureInfo.InvariantCulture));
        var k = digits.Length;
        var n = pointPosition;
        if (k <= n && n <= 21)
        {
            return digits + new string('0', n - k);
        }

        if (0 < n && n <= 21)
        {
            return digits[..n] + "." + digits[n..];
        }

        if (-6 < n && n <= 0)
        {
            return "0." + new string('0', -n) + digits;
        }

        var exponent = n - 1;
        var mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
        return mantissa + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Décompose une représentation .NET (ex. <c>1.5E-07</c>) en chiffres significatifs <c>s</c>
    /// et position de la virgule <c>n</c>, telles que la valeur vaut <c>0,s × 10^n</c>.
    /// </summary>
    private static (string Digits, int PointPosition) ShortestDigits(string text)
    {
        var exponent = 0;
        var exponentIndex = text.IndexOf('E', StringComparison.Ordinal);
        if (exponentIndex >= 0)
        {
            exponent = int.Parse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..exponentIndex];
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var integerPart = dot < 0 ? text : text[..dot];
        var digits = dot < 0 ? text : integerPart + text[(dot + 1)..];
        var pointPosition = integerPart.Length + exponent;
        var leadingZeros = digits.Length - digits.TrimStart('0').Length;
        return (digits.Trim('0'), pointPosition - leadingZeros);
    }
}
