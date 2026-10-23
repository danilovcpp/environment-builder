using System.Text;
using EnvironmentBuilder.Core.Environments;

namespace EnvironmentBuilder.UnitTests.Contracts;

public sealed class DocumentRulesTests
{
    [Theory]
    [InlineData("1e-100", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("01", false, 0)]
    [InlineData("0einvalid", false, 0)]
    [InlineData("1.000000000000000000000000000000000001", false, 0)]
    [InlineData("0e99999999999999999999999", true, 0)]
    [InlineData("1.0", true, 1)]
    [InlineData("100e-2", true, 1)]
    [InlineData("1e2", true, 100)]
    [InlineData("-9223372036854775808", true, long.MinValue)]
    [InlineData("9223372036854775808", false, 0)]
    public void Typed_integers_are_exact_even_at_exponent_and_Int64_boundaries(string token, bool valid, long expected)
    {
        Assert.Equal(valid, ContractInteger.TryRead(token, out long value));
        if (valid) { Assert.Equal(expected, value); }
    }
    [Fact]
    public void Canonical_hash_orders_properties_and_preserves_numeric_lexemes()
    {
        Assert.Equal("{\"a\":[1.0,1],\"z\":\"Ж<\"}",
            Encoding.UTF8.GetString(CanonicalJson.Encode("{\"z\":\"Ж<\",\"a\":[1.0,1]}")));
        Assert.Equal(CanonicalJson.Hash("{\"b\":2,\"a\":1}"), CanonicalJson.Hash("{\"a\":1,\"b\":2}"));
        Assert.NotEqual(CanonicalJson.Hash("1"), CanonicalJson.Hash("1.0"));
    }
}
