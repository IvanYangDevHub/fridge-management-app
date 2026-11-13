using System.Security.Cryptography;
using System.Text;
using FreshDays;
using Xunit;

namespace FreshDays.Tests;

public class LineSignatureTests
{
    [Fact]
    public void ValidatesExactRawBodyAndRejectsTampering()
    {
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret"), body));
        Assert.True(LineSignature.Verify(body, signature, "secret"));
        Assert.False(LineSignature.Verify(Encoding.UTF8.GetBytes("{ \"events\":[]}"), signature, "secret"));
        Assert.False(LineSignature.Verify(body, "bad", "secret"));
        Assert.False(LineSignature.Verify(body, null, "secret"));
    }
}
