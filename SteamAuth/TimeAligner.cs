using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SteamAuth;

/// <summary>
///     Class to help align system time with the Steam server time. Not super advanced; probably not taking some things
///     into account that it should.
///     Necessary to generate up-to-date codes. In general, this will have an error of less than a second, assuming Steam
///     is operational.
/// </summary>
public class TimeAligner
{
    private static bool _aligned;
    private static int _timeDifference;

    public static long GetSteamTime()
    {
        if (!_aligned) AlignTime();
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeDifference;
    }

    public static async Task<long> GetSteamTimeAsync()
    {
        if (!_aligned) await AlignTimeAsync();
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _timeDifference;
    }

    public static void AlignTime()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var handler = new HttpClientHandler();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(SteamWeb.MobileAppUserAgent);
        try
        {
            var response = client.GetStringAsync(ApiEndpoints.TwoFactorTimeQuery + "?steamid=0").Result;
            var query = JsonSerializer.Deserialize<TimeQuery>(response);
            _timeDifference = (int)(query.Response.ServerTime - currentTime);
            _aligned = true;
        }
        catch (AggregateException)
        {
        }
    }

    public static async Task AlignTimeAsync()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var handler = new HttpClientHandler();
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(SteamWeb.MobileAppUserAgent);
        try
        {
            var response = await client.GetStringAsync(ApiEndpoints.TwoFactorTimeQuery + "?steamid=0");
            var query = JsonSerializer.Deserialize<TimeQuery>(response);
            _timeDifference = (int)(query.Response.ServerTime - currentTime);
            _aligned = true;
        }
        catch (HttpRequestException)
        {
        }
    }

    internal class TimeQuery
    {
        [JsonPropertyName("response")] internal TimeQueryResponse Response { get; set; }

        internal class TimeQueryResponse
        {
            [JsonPropertyName("server_time")] public long ServerTime { get; set; }
        }
    }
}