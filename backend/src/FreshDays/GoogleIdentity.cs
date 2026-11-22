using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FreshDays;

internal sealed record GoogleAccount(string Subject, string Email, bool EmailAuthoritative);

internal sealed class GoogleIdentity
{
    private const string GoogleIssuer = "https://accounts.google.com";
    private readonly JsonWebTokenHandler tokens = new() { MaximumTokenSizeInBytes = 10_000 };
    private readonly TokenValidationParameters parameters;

    public GoogleIdentity(IHttpClientFactory clients, IConfiguration configuration)
    {
        parameters = new TokenValidationParameters
        {
            ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                GoogleIssuer + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(clients.CreateClient("GoogleIdentity"))),
            ValidIssuers = [GoogleIssuer, "accounts.google.com"],
            ValidAudience = configuration["Auth:GoogleClientId"],
            IgnoreTrailingSlashWhenValidatingAudience = false,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero,
            ValidateIssuerSigningKey = true,
        };
    }

    public async Task<GoogleAccount?> Verify(string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
            return null;

        var result = await tokens.ValidateTokenAsync(credential, parameters);
        if (!result.IsValid)
            return null;

        var token = (JsonWebToken)result.SecurityToken;
        if (!token.TryGetPayloadValue<bool>("email_verified", out var verified) || !verified ||
            string.IsNullOrWhiteSpace(token.Subject) ||
            !token.TryGetPayloadValue<string>("email", out var email) || string.IsNullOrWhiteSpace(email))
            return null;

        email = email.Trim().ToLowerInvariant();
        var authoritative = email.EndsWith("@gmail.com", StringComparison.Ordinal) ||
            (token.TryGetPayloadValue<string>("hd", out var hostedDomain) && !string.IsNullOrWhiteSpace(hostedDomain));
        return new GoogleAccount(token.Subject, email, authoritative);
    }
}
