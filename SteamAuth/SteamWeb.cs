using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace SmogAuthCore;

public static class SteamWeb
{
    public const string MobileAppUserAgent = "Dalvik/2.1.0 (Linux; U; Android 9; Valve Steam App Version/3)";

    private static readonly HttpClient HttpClient = new();

    static SteamWeb()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd(MobileAppUserAgent);
    }

    public static async Task<string> GetAsync(string url, CookieContainer? cookies)
    {
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);

        if (cookies != null)
        {
            var cookieHeader = GetCookieHeader(url, cookies);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                requestMessage.Headers.Add("Cookie", cookieHeader);
            }
        }

        var response = await HttpClient.SendAsync(requestMessage);
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<string> PostAsync(string url, CookieContainer? cookies, NameValueCollection? body)
    {
        body ??= new NameValueCollection();
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(body.Cast<string>().ToDictionary(k => k, k => body[k]))
        };

        if (cookies != null)
        {
            var cookieHeader = GetCookieHeader(url, cookies);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                requestMessage.Headers.Add("Cookie", cookieHeader);
            }
        }

        var response = await HttpClient.SendAsync(requestMessage);
        return await response.Content.ReadAsStringAsync();
    }

    private static string GetCookieHeader(string url, CookieContainer cookies)
    {
        var uri = new Uri(url);
        return cookies.GetCookieHeader(uri);
    }
}