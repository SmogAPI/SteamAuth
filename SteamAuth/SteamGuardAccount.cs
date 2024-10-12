using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static System.String;

namespace SmogAuthCore;

public class SteamGuardAccount
{
    private static readonly byte[] SteamGuardCodeTranslations =
        { 50, 51, 52, 53, 54, 55, 56, 57, 66, 67, 68, 70, 71, 72, 74, 75, 77, 78, 80, 81, 82, 84, 86, 87, 88, 89 };

    [JsonPropertyName("shared_secret")] public string SharedSecret { get; set; } = string.Empty;

    [JsonPropertyName("serial_number")] public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("revocation_code")] public string RevocationCode { get; set; } = string.Empty;

    [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("server_time")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long ServerTime { get; set; }

    [JsonPropertyName("account_name")] public string AccountName { get; set; } = string.Empty;

    [JsonPropertyName("token_gid")] public string TokenGid { get; set; } = string.Empty;

    [JsonPropertyName("identity_secret")] public string IdentitySecret { get; set; } = string.Empty;

    [JsonPropertyName("secret_1")] public string Secret1 { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Status { get; set; }

    [JsonPropertyName("device_id")] public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("fully_enrolled")] public bool FullyEnrolled { get; set; }

    public SessionData Session { get; set; } = null!;

    /// <summary>
    ///     Remove steam guard from this account
    /// </summary>
    /// <param name="scheme">1 = Return to email codes, 2 = Remove completley</param>
    /// <returns></returns>
    public async Task<bool> DeactivateAuthenticator(int scheme = 1)
    {
        var postBody = new NameValueCollection();
        postBody.Add("revocation_code", RevocationCode);
        postBody.Add("revocation_reason", "1");
        postBody.Add("steamguard_scheme", scheme.ToString());
        var response = await SteamWeb.PostRequest(
            "https://api.steampowered.com/ITwoFactorService/RemoveAuthenticator/v1?access_token=" + Session.AccessToken,
            null, postBody);

        if (IsNullOrEmpty(response)) return false;

        var removeResponse = JsonSerializer.Deserialize<RemoveAuthenticatorResponse>(response);
        return removeResponse is { Response.Success: true };
    }

    public string? GenerateSteamGuardCode()
    {
        return GenerateSteamGuardCodeForTime(TimeAligner.GetSteamTime());
    }

    public async Task<string?> GenerateSteamGuardCodeAsync()
    {
        return GenerateSteamGuardCodeForTime(await TimeAligner.GetSteamTimeAsync());
    }

    public string? GenerateSteamGuardCodeForTime(long time)
    {
        if (IsNullOrEmpty(SharedSecret)) return "";

        var sharedSecretUnescaped = Regex.Unescape(SharedSecret);
        var sharedSecretArray = Convert.FromBase64String(sharedSecretUnescaped);
        var timeArray = new byte[8];

        time /= 30L;

        for (var i = 8; i > 0; i--)
        {
            timeArray[i - 1] = (byte)time;
            time >>= 8;
        }

        var hmacGenerator = new HMACSHA1();
        hmacGenerator.Key = sharedSecretArray;
        var hashedData = hmacGenerator.ComputeHash(timeArray);
        var codeArray = new byte[5];
        try
        {
            var b = (byte)(hashedData[19] & 0xF);
            var codePoint = ((hashedData[b] & 0x7F) << 24) | ((hashedData[b + 1] & 0xFF) << 16) |
                            ((hashedData[b + 2] & 0xFF) << 8) | (hashedData[b + 3] & 0xFF);

            for (var i = 0; i < 5; ++i)
            {
                codeArray[i] = SteamGuardCodeTranslations[codePoint % SteamGuardCodeTranslations.Length];
                codePoint /= SteamGuardCodeTranslations.Length;
            }
        }
        catch (Exception)
        {
            return null; //Change later, catch-alls are bad!
        }

        return Encoding.UTF8.GetString(codeArray);
    }

    public Confirmation[] FetchConfirmations()
    {
        var url = GenerateConfirmationUrl();
        var response = SteamWeb.GetRequest(url, Session.GetCookies()).Result;
        return FetchConfirmationInternal(response);
    }

    public async Task<Confirmation[]> FetchConfirmationsAsync()
    {
        var url = GenerateConfirmationUrl();
        var response = await SteamWeb.GetRequest(url, Session.GetCookies());
        return FetchConfirmationInternal(response);
    }

    private Confirmation[] FetchConfirmationInternal(string response)
    {
        var confirmationsResponse = JsonSerializer.Deserialize<ConfirmationsResponse>(response);
        if (confirmationsResponse == null) throw new Exception("Failed to parse response");

        if (!confirmationsResponse.Success) throw new Exception(confirmationsResponse.Message);

        if (confirmationsResponse.NeedAuthentication) throw new Exception("Needs Authentication");

        return confirmationsResponse.Confirmations;
    }

    /// <summary>
    ///     Deprecated. Simply returns conf.Creator.
    /// </summary>
    /// <param name="conf"></param>
    /// <returns>The Creator field of conf</returns>
    public long GetConfirmationTradeOfferId(Confirmation conf)
    {
        if (conf.ConfType != Confirmation.EMobileConfirmationType.Trade)
            throw new ArgumentException("conf must be a trade confirmation.");

        return (long)conf.Creator;
    }

    public async Task<bool> AcceptMultipleConfirmations(Confirmation[] confs)
    {
        return await _sendMultiConfirmationAjax(confs, "allow");
    }

    public async Task<bool> DenyMultipleConfirmations(Confirmation[] confs)
    {
        return await _sendMultiConfirmationAjax(confs, "cancel");
    }

    public async Task<bool> AcceptConfirmation(Confirmation conf)
    {
        return await _sendConfirmationAjax(conf, "allow");
    }

    public async Task<bool> DenyConfirmation(Confirmation conf)
    {
        return await _sendConfirmationAjax(conf, "cancel");
    }

    private async Task<bool> _sendConfirmationAjax(Confirmation conf, string op)
    {
        var url = ApiEndpoints.CommunityBase + "/mobileconf/ajaxop";
        var queryString = "?op=" + op + "&";
        // tag is different from op now
        var tag = op == "allow" ? "accept" : "reject";
        queryString += GenerateConfirmationQueryParams(tag);
        queryString += "&cid=" + conf.Id + "&ck=" + conf.Key;
        url += queryString;

        var response = await SteamWeb.GetRequest(url, Session.GetCookies());
        var confResponse = JsonSerializer.Deserialize<SendConfirmationResponse>(response);
        return confResponse is { Success: true };
    }

    private async Task<bool> _sendMultiConfirmationAjax(Confirmation[] confs, string op)
    {
        var url = ApiEndpoints.CommunityBase + "/mobileconf/multiajaxop";
        // tag is different from op now
        var tag = op == "allow" ? "accept" : "reject";
        var query = "op=" + op + "&" + GenerateConfirmationQueryParams(tag);
        foreach (var conf in confs) query += "&cid[]=" + conf.Id + "&ck[]=" + conf.Key;

        string response;
        using (var client = new HttpClient(new HttpClientHandler { CookieContainer = Session.GetCookies() }))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(SteamWeb.MobileAppUserAgent);
            var content = new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded");
            var result = await client.PostAsync(new Uri(url), content);
            response = await result.Content.ReadAsStringAsync();
        }

        if (IsNullOrEmpty(response)) return false;

        var confResponse = JsonSerializer.Deserialize<SendConfirmationResponse>(response);
        return confResponse is { Success: true };
    }

    public string GenerateConfirmationUrl(string tag = "conf")
    {
        const string endpoint = ApiEndpoints.CommunityBase + "/mobileconf/getlist?";
        var queryString = GenerateConfirmationQueryParams(tag);
        return endpoint + queryString;
    }

    public string GenerateConfirmationQueryParams(string tag)
    {
        if (IsNullOrEmpty(DeviceId))
            throw new ArgumentException("Device ID is not present");

        var queryParams = GenerateConfirmationQueryParamsAsNvc(tag);

        return Join("&", queryParams.AllKeys.Select(key => $"{key}={queryParams[key]}"));
    }

    public NameValueCollection GenerateConfirmationQueryParamsAsNvc(string tag)
    {
        if (IsNullOrEmpty(DeviceId))
            throw new ArgumentException("Device ID is not present");

        var time = TimeAligner.GetSteamTime();

        var ret = new NameValueCollection();
        ret.Add("p", DeviceId);
        ret.Add("a", Session.SteamId.ToString());
        ret.Add("k", _generateConfirmationHashForTime(time, tag));
        ret.Add("t", time.ToString());
        ret.Add("m", "react");
        ret.Add("tag", tag);

        return ret;
    }

    private string? _generateConfirmationHashForTime(long time, string? tag)
    {
        var decode = Convert.FromBase64String(IdentitySecret);
        var n2 = 8;
        if (tag != null)
        {
            if (tag.Length > 32)
                n2 = 8 + 32;
            else
                n2 = 8 + tag.Length;
        }

        var array = new byte[n2];
        var n3 = 8;
        while (true)
        {
            var n4 = n3 - 1;
            if (n3 <= 0) break;
            array[n4] = (byte)time;
            time >>= 8;
            n3 = n4;
        }

        if (tag != null) Array.Copy(Encoding.UTF8.GetBytes(tag), 0, array, 8, n2 - 8);

        try
        {
            var hmacGenerator = new HMACSHA1();
            hmacGenerator.Key = decode;
            var hashedData = hmacGenerator.ComputeHash(array);
            var encodedData = Convert.ToBase64String(hashedData, Base64FormattingOptions.None);
            var hash = WebUtility.UrlEncode(encodedData);
            return hash;
        }
        catch
        {
            return null;
        }
    }

    public class WgTokenInvalidException : Exception
    {
    }

    public class WgTokenExpiredException : Exception
    {
    }

    private class RemoveAuthenticatorResponse
    {
        [JsonPropertyName("response")] public RemoveAuthenticatorInternalResponse Response { get; set; } = null!;

        internal class RemoveAuthenticatorInternalResponse
        {
            [JsonPropertyName("success")] public bool Success { get; set; } = false;

            [JsonPropertyName("revocation_attempts_remaining")]
            public int RevocationAttemptsRemaining { get; set; }
        }
    }

    private class SendConfirmationResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
    }

    private class ConfirmationDetailsResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }

        [JsonPropertyName("html")] public string Html { get; set; } = string.Empty;
    }
}