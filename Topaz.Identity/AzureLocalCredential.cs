using Azure.Core;

namespace Topaz.Identity;

public sealed class AzureLocalCredential(string objectId, bool isForGraph = false, string? preferredUsername = null)
    : TokenCredential
{
    private readonly string? _audience;

    internal AzureLocalCredential(
        string objectId,
        bool isForGraph,
        string? preferredUsername,
        string? audience)
        : this(objectId, isForGraph, preferredUsername)
    {
        _audience = audience;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var tokenString = JwtHelper.GenerateJwt(objectId, isForGraph, preferredUsername, _audience);
        return new AccessToken(tokenString, DateTimeOffset.MaxValue);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var tokenString = JwtHelper.GenerateJwt(objectId, isForGraph, preferredUsername, _audience);
        return ValueTask.FromResult(new AccessToken(tokenString, DateTimeOffset.MaxValue));
    }
}

public sealed class AzureFixedTokenLocalCredential(string token) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return new AccessToken(token, DateTimeOffset.MaxValue);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.MaxValue));
    }
}
