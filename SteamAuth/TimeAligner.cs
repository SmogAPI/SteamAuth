using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SmogAuthCore;

/// <summary>
///     Class to help align system time with the Steam server time. Not super advanced; probably not taking some things
///     into account that it should.
///     Necessary to generate up-to-date codes. In general, this will have an error of less than a second, assuming Steam
///     is operational.
/// </summary>
public static class TimeAligner
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

    private static void AlignTime()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var content = SteamWeb.PostAsync(ApiEndpoints.TwoFactorTimeQuery + "?steamid=0", null, null).Result;
        var query = JsonSerializer.Deserialize<TimeQuery>(content);
        if (query?.Response == null) return;

        _timeDifference = (int)(query.Response.ServerTime - currentTime);
        _aligned = true;
    }

    private static async Task AlignTimeAsync()
    {
        var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var content = await SteamWeb.PostAsync(ApiEndpoints.TwoFactorTimeQuery + "?steamid=0", null, null);
        var query = JsonSerializer.Deserialize<TimeQuery>(content);
        if (query?.Response == null) return;

        _timeDifference = (int)(query.Response.ServerTime - currentTime);
        _aligned = true;
    }

    internal class TimeQuery
    {
        [JsonPropertyName("response")] public TimeQueryResponse? Response { get; set; }

        internal class TimeQueryResponse
        {
            [JsonPropertyName("server_time")]
            [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
            public long ServerTime { get; set; }
        }
    }
}