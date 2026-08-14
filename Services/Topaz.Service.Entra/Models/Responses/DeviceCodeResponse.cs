using System.Text.Json;
using System.Text.Json.Serialization;
using Topaz.Identity;
using Topaz.Shared;

namespace Topaz.Service.Entra.Models.Responses;

internal sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; } = 1800;

    [JsonPropertyName("interval")]
    public int Interval { get; init; } = 5;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    public static DeviceCodeResponse Create(
        string deviceCode,
        string userCode,
        EntraAuthority? configuredAuthority = null)
    {
        var verificationUri =
            (configuredAuthority ?? EntraAuthority.Current).GetEndpoint("/devicelogin");
        return new DeviceCodeResponse
        {
            DeviceCode = deviceCode,
            UserCode = userCode,
            VerificationUri = verificationUri,
            Message = $"To sign in, use a web browser to open the page {verificationUri} and enter the code {userCode} to authenticate."
        };
    }

    public override string ToString() => JsonSerializer.Serialize(this, GlobalSettings.JsonOptions);
}
