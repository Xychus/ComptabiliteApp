using System.Text.Json;
using Ledger.Contracts.Integrity;

namespace Ledger.Contracts.Tests.Integrity;

/// <summary>Conformité à la RFC 8785 (JCS), avec les exemples de la RFC.</summary>
public sealed class JsonCanonicalizerTests
{
    [Fact]
    public void Exemple_de_la_section_3_2_2()
    {
        const string input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;

        Assert.Equal(
            """{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}""",
            JsonCanonicalizer.Canonicalize(input));
    }

    [Fact]
    public void Tri_des_cles_par_unites_de_code_UTF16_section_3_2_3()
    {
        const string input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;

        using var document = JsonDocument.Parse(JsonCanonicalizer.Canonicalize(input));
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["\r", "1", "\u0080", "\u00f6", "\u20ac", "\ud83d\ude00", "\ufb33"], keys);
    }

    /// <summary>Vecteurs de l'annexe B de la RFC 8785 (bits IEEE-754 → texte attendu).</summary>
    [Theory]
    [InlineData(0x0000000000000000UL, "0")]
    [InlineData(0x8000000000000000UL, "0")]
    [InlineData(0x0000000000000001UL, "5e-324")]
    [InlineData(0x8000000000000001UL, "-5e-324")]
    [InlineData(0x7fefffffffffffffUL, "1.7976931348623157e+308")]
    [InlineData(0xffefffffffffffffUL, "-1.7976931348623157e+308")]
    [InlineData(0x4340000000000000UL, "9007199254740992")]
    [InlineData(0xc340000000000000UL, "-9007199254740992")]
    [InlineData(0x4430000000000000UL, "295147905179352830000")]
    [InlineData(0x44b52d02c7e14af5UL, "9.999999999999997e+22")]
    [InlineData(0x44b52d02c7e14af6UL, "1e+23")]
    [InlineData(0x44b52d02c7e14af7UL, "1.0000000000000001e+23")]
    [InlineData(0x444b1ae4d6e2ef4eUL, "999999999999999700000")]
    [InlineData(0x444b1ae4d6e2ef4fUL, "999999999999999900000")]
    [InlineData(0x444b1ae4d6e2ef50UL, "1e+21")]
    [InlineData(0x3eb0c6f7a0b5ed8cUL, "9.999999999999997e-7")]
    [InlineData(0x3eb0c6f7a0b5ed8dUL, "0.000001")]
    [InlineData(0x41b3de4355555553UL, "333333333.3333332")]
    [InlineData(0x41b3de4355555554UL, "333333333.33333325")]
    [InlineData(0x41b3de4355555555UL, "333333333.3333333")]
    [InlineData(0x41b3de4355555556UL, "333333333.3333334")]
    [InlineData(0x41b3de4355555557UL, "333333333.33333343")]
    [InlineData(0xbecbf647612f3696UL, "-0.0000033333333333333333")]
    [InlineData(0x43143ff3c1cb0959UL, "1424953923781206.2")]
    public void Nombres_selon_ECMAScript(ulong bits, string expected)
    {
        var number = BitConverter.UInt64BitsToDouble(bits);

        Assert.Equal(expected, JsonCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(number)));
    }

    [Theory]
    [InlineData("[1e400]")]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("\"\\ud800\"")]
    public void Entree_non_I_JSON_refusee(string json) =>
        Assert.Throws<FormatException>(() => JsonCanonicalizer.Canonicalize(json));

    [Fact]
    public void Indifferent_a_l_ordre_des_cles_et_aux_espaces() =>
        Assert.Equal(
            JsonCanonicalizer.Canonicalize("""{"b":[1,{"y":true,"x":null}],"a":"z"}"""),
            JsonCanonicalizer.Canonicalize("""
                {
                  "a" : "z",
                  "b" : [ 1.0, { "x": null, "y": true } ]
                }
                """));

    [Theory]
    [InlineData("\"\\b\\f\\t\\u0001\\u001f\"", "\"\\b\\f\\t\\u0001\\u001f\"")]
    [InlineData("\"\\u00e9\\u2028\"", "\"é\u2028\"")]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    [InlineData("100", "100")]
    [InlineData("0.5", "0.5")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("123e18", "123000000000000000000")]
    public void Valeurs_elementaires(string json, string expected) =>
        Assert.Equal(expected, JsonCanonicalizer.Canonicalize(json));
}
