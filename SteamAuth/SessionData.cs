using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SteamAuth;

public class SessionData
{
    public ulong SteamId { get; set; }

    public string AccessToken { get; set; }

    public string RefreshToken { get; set; }

    public string SessionId { get; set; }

    public async Task RefreshAccessToken()
    {
        if (string.IsNullOrEmpty(RefreshToken))
            throw new Exception("Refresh token is empty");

        if (IsTokenExpired(RefreshToken))
            throw new Exception("Refresh token is expired");

        string responseStr;
        try
        {
            var postData = new NameValueCollection();
            postData.Add("refresh_token", RefreshToken);
            postData.Add("steamid", SteamId.ToString());
            responseStr = await SteamWeb.PostRequest(
                "https://api.steampowered.com/IAuthenticationService/GenerateAccessTokenForApp/v1/", null, postData);
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to refresh token: " + ex.Message);
        }

        var response = JsonSerializer.Deserialize<GenerateAccessTokenForAppResponse>(responseStr);
        AccessToken = response.Response.AccessToken;
    }

    public bool IsAccessTokenExpired()
    {
        if (string.IsNullOrEmpty(AccessToken))
            return true;

        return IsTokenExpired(AccessToken);
    }

    public bool IsRefreshTokenExpired()
    {
        if (string.IsNullOrEmpty(RefreshToken))
            return true;

        return IsTokenExpired(RefreshToken);
    }

    private bool IsTokenExpired(string token)
    {
        var tokenComponents = token.Split('.');
        // Fix up base64url to normal base64
        var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

        if (base64.Length % 4 != 0) base64 += new string('=', 4 - base64.Length % 4);

        var payloadBytes = Convert.FromBase64String(base64);
        var jwt = JsonSerializer.Deserialize<SteamAccessToken>(Encoding.UTF8.GetString(payloadBytes));

        // Compare expire time of the token to the current time
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > jwt.Exp;
    }

    public CookieContainer GetCookies()
    {
        if (SessionId == null)
            SessionId = GenerateSessionId();

        var cookies = new CookieContainer();
        foreach (var domain in new[] { "steamcommunity.com", "store.steampowered.com" })
        {
            cookies.Add(new Cookie("steamLoginSecure", GetSteamLoginSecure(), "/", domain));
            cookies.Add(new Cookie("sessionid", SessionId, "/", domain));
            cookies.Add(new Cookie("mobileClient", "android", "/", domain));
            cookies.Add(new Cookie("mobileClientVersion", "777777 3.6.4", "/", domain));
        }

        return cookies;
    }

    private string GetSteamLoginSecure()
    {
        return SteamId + "%7C%7C" + AccessToken;
    }

    private static string GenerateSessionId()
    {
        return GetRandomHexNumber(32);
    }

    private static string GetRandomHexNumber(int digits)
    {
        var random = new Random();
        var buffer = new byte[digits / 2];
        random.NextBytes(buffer);
        var result = string.Concat(buffer.Select(x => x.ToString("X2")).ToArray());
        if (digits % 2 == 0)
            return result;
        return result + random.Next(16).ToString("X");
    }

    private class SteamAccessToken
    {
        public long Exp { get; set; }
    }

    private class GenerateAccessTokenForAppResponse
    {
        [JsonPropertyName("response")] public GenerateAccessTokenForAppResponseResponse Response { get; set; }
    }

    private class GenerateAccessTokenForAppResponseResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; }
    }
}