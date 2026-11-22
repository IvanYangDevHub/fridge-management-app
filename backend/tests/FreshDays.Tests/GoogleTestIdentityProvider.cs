using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FreshDays.Tests;

internal sealed class GoogleTestIdentityProvider : HttpMessageHandler
{
    private readonly RSA key = RSA.Create(2048);
    public const string Audience = "freshdays-test-client";
    private const string Issuer = "https://accounts.google.com";
    private const string Jwks = "https://www.googleapis.com/oauth2/v3/certs";
    private const string KeyId = "freshdays-google-key";
    private static readonly JsonSerializerOptions OmitNulls = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> phaseOne, TimeProvider? clock = null,
        string ownerEmail = "owner@gmail.com") =>
        phaseOne.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, settings) =>
            settings.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Product:PhaseTwo"] = "true",
                ["Auth:GoogleClientId"] = Audience,
                ["Auth:LegacyOwnerEmail"] = ownerEmail,
            }))
            .ConfigureTestServices(services =>
            {
                services.AddHttpClient("GoogleIdentity").ConfigurePrimaryHttpMessageHandler(() => this);
                if (clock != null)
                    services.AddSingleton(clock);
            }));

    public string Credential(string? subject, string? email = "owner@gmail.com", bool? verified = true,
        string audience = Audience, string issuer = Issuer, DateTimeOffset? expires = null, bool forged = false,
        string? domain = null)
    {
        var header = EncodeJson(new { alg = "RS256", kid = KeyId, typ = "JWT" });
        var payload = EncodeJson(new
        {
            sub = subject, email, email_verified = verified, aud = audience, iss = issuer, hd = domain,
            iat = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds(),
            exp = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds(),
        });
        var data = $"{header}.{payload}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (forged)
            signature[0] ^= 0xff;
        return $"{data}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    private static string EncodeJson(object value) =>
        WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value, OmitNulls));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var publicKey = key.ExportParameters(false);
        HttpContent content = request.RequestUri!.AbsoluteUri switch
        {
            Issuer + "/.well-known/openid-configuration" => JsonContent.Create(new { issuer = Issuer, jwks_uri = Jwks }),
            Jwks => JsonContent.Create(new
            {
                keys = new[]
                {
                    new
                    {
                        alg = "RS256", kty = "RSA", kid = KeyId, use = "sig",
                        n = WebEncoders.Base64UrlEncode(publicKey.Modulus!),
                        e = WebEncoders.Base64UrlEncode(publicKey.Exponent!),
                    },
                },
            }),
            _ => throw new InvalidOperationException($"Unexpected Google request {request.RequestUri}"),
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            key.Dispose();
        base.Dispose(disposing);
    }
}
