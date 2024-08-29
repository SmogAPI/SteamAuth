using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace SteamAuthCore;

public static class SteamWeb
{
    public static readonly string MobileAppUserAgent = "Dalvik/2.1.0 (Linux; U; Android 9; Valve Steam App Version/3)";

    public static async Task<string> GetRequest(string url, CookieContainer cookies)
    {
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MobileAppUserAgent);
        var response = await client.GetStringAsync(url);
        return response;
    }

    public static async Task<string> PostRequest(string url, CookieContainer cookies, NameValueCollection body)
    {
        body ??= new NameValueCollection();

        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MobileAppUserAgent);
        var content = new FormUrlEncodedContent(body.Cast<string>().ToDictionary(k => k, k => body[k]));
        var result = await client.PostAsync(new Uri(url), content);
        var response = await result.Content.ReadAsStringAsync();
        return response;
    }
}