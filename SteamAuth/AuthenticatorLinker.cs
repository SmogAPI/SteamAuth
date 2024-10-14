using System;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SmogAuthCore;

/// <summary>
///     Handles the linking process for a new mobile authenticator.
/// </summary>
public class AuthenticatorLinker
{
    public enum FinalizeResult
    {
        BadAuthCode,
        UnableToGenerateCorrectCodes,
        Success,
        GeneralFailure
    }

    public enum LinkResult
    {
        MustProvidePhoneNumber, //No phone number on the account
        MustRemovePhoneNumber, //A phone number is already on the account
        MustConfirmEmail, //User need to click link from confirmation email
        AwaitingFinalization, //Must provide an SMS code
        GeneralFailure, //General failure (really now!)
        AuthenticatorPresent,
        FailureAddingPhone
    }

    /// <summary>
    ///     Set when the confirmation email to set a phone number is set
    /// </summary>
    private bool _confirmationEmailSent;

    /// <summary>
    ///     Session data containing an access token for a steam account generated with k_EAuthTokenPlatformType_MobileApp
    /// </summary>
    private readonly SessionData _session;

    /// <summary>
    ///     Email address the confirmation email was sent to when adding a phone number
    /// </summary>
    public string? ConfirmationEmailAddress;

    /// <summary>
    ///     True if the authenticator has been fully finalized.
    /// </summary>
    public bool Finalized = false;

    public string? PhoneCountryCode = null;

    /// <summary>
    ///     Set to register a new phone number when linking. If a phone number is not set on the account, this must be set. If
    ///     a phone number is set on the account, this must be null.
    /// </summary>
    public string? PhoneNumber = null;

    /// <summary>
    ///     Create a new instance of AuthenticatorLinker
    /// </summary>
    /// <param name="sessionData">SessionData object containing an accessToken and a steamid</param>
    public AuthenticatorLinker(SessionData sessionData)
    {
        _session = sessionData;
        DeviceId = GenerateDeviceId();
    }

    /// <summary>
    ///     Randomly-generated device ID. Should only be generated once per linker.
    /// </summary>
    public string DeviceId { get; }

    /// <summary>
    ///     After the initial link step, if successful, this will be the SteamGuard data for the account. PLEASE save this
    ///     somewhere after generating it; it's vital data.
    /// </summary>
    public SteamGuardAccount LinkedAccount { get; private set; } = null!;

    /// <summary>
    ///     First step in adding a mobile authenticator to an account
    /// </summary>
    public async Task<LinkResult> AddAuthenticator()
    {
        // This method will be called again once the user confirms their phone number email
        if (_confirmationEmailSent)
        {
            // Check if email was confirmed
            var isStillWaiting = await _isAccountWaitingForEmailConfirmation();
            if (isStillWaiting) return LinkResult.MustConfirmEmail;

            // Now send the SMS to the phone number
            await _sendPhoneVerificationCode();

            // This takes time so wait a bit
            await Task.Delay(2000);
        }

        // Make request to ITwoFactorService/AddAuthenticator
        var addAuthenticatorBody = new NameValueCollection
        {
            { "steamid", _session.SteamId.ToString() },
            { "authenticator_time", (await TimeAligner.GetSteamTimeAsync()).ToString() },
            { "authenticator_type", "1" },
            { "device_identifier", DeviceId },
            { "sms_phone_id", "1" }
        };
        var addAuthenticatorResponseStr = await SteamWeb.PostAsync(
            "https://api.steampowered.com/ITwoFactorService/AddAuthenticator/v1/?access_token=" + _session.AccessToken,
            null, addAuthenticatorBody);

        // Parse response json to object
        var addAuthenticatorResponse =
            JsonSerializer.Deserialize<AddAuthenticatorResponse>(addAuthenticatorResponseStr);

        if (addAuthenticatorResponse?.Response == null)
            return LinkResult.GeneralFailure;

        switch (addAuthenticatorResponse.Response.Status)
        {
            // Status 2 means no phone number is on the account
            case 2 when PhoneNumber == null:
                return LinkResult.MustProvidePhoneNumber;
            // Add phone number
            // Get country code
            case 2:
            {
                var countryCode = PhoneCountryCode;

                // If given country code is null, use the one from the Steam account
                if (string.IsNullOrEmpty(countryCode)) countryCode = await GetUserCountry();

                // Set the phone number
                var res = await _setAccountPhoneNumber(PhoneNumber, countryCode);

                // Make sure it's successful then respond that we must confirm via email
                if (res?.Response.ConfirmationEmailAddress == null) return LinkResult.FailureAddingPhone;
                ConfirmationEmailAddress = res.Response.ConfirmationEmailAddress;
                _confirmationEmailSent = true;
                return LinkResult.MustConfirmEmail;

                // If something else fails, we end up here
            }
            case 29:
                return LinkResult.AuthenticatorPresent;
        }

        if (addAuthenticatorResponse.Response.Status != 1)
            return LinkResult.GeneralFailure;

        // Setup this.LinkedAccount
        LinkedAccount = addAuthenticatorResponse.Response;
        LinkedAccount.DeviceId = DeviceId;
        LinkedAccount.Session = _session;

        return LinkResult.AwaitingFinalization;
    }

    public async Task<FinalizeResult> FinalizeAddAuthenticator(string authCode)
    {
        var tries = 0;
        while (tries <= 10)
        {
            var finalizeAuthenticatorValues = new NameValueCollection
            {
                { "steamid", _session.SteamId.ToString() },
                { "authenticator_code", await LinkedAccount.GenerateSteamGuardCodeAsync() },
                { "authenticator_time", (await TimeAligner.GetSteamTimeAsync()).ToString() },
                { "activation_code", authCode },
                { "validate_sms_code", "1" }
            };

            string finalizeAuthenticatorResultStr;
            using (var client = new HttpClient(new HttpClientHandler { CookieContainer = _session.GetCookies() }))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(SteamWeb.MobileAppUserAgent);
                var content = new FormUrlEncodedContent(finalizeAuthenticatorValues.Cast<string>()
                    .ToDictionary(k => k, k => finalizeAuthenticatorValues[k]));
                var result =
                    await client.PostAsync(
                        new Uri(
                            "https://api.steampowered.com/ITwoFactorService/FinalizeAddAuthenticator/v1/?access_token=" +
                            _session.AccessToken), content);
                finalizeAuthenticatorResultStr = await result.Content.ReadAsStringAsync();
            }

            var finalizeAuthenticatorResponse =
                JsonSerializer.Deserialize<FinalizeAuthenticatorResponse>(finalizeAuthenticatorResultStr);

            if (finalizeAuthenticatorResponse?.Response == null)
                return FinalizeResult.GeneralFailure;

            switch (finalizeAuthenticatorResponse.Response.Status)
            {
                case 89:
                    return FinalizeResult.BadAuthCode;
                case 88 when tries >= 10:
                    return FinalizeResult.UnableToGenerateCorrectCodes;
            }

            if (!finalizeAuthenticatorResponse.Response.Success) return FinalizeResult.GeneralFailure;

            if (finalizeAuthenticatorResponse.Response.WantMore)
            {
                tries++;
                continue;
            }

            LinkedAccount.FullyEnrolled = true;
            return FinalizeResult.Success;
        }

        return FinalizeResult.GeneralFailure;
    }

    private async Task<string> GetUserCountry()
    {
        var getCountryBody = new NameValueCollection { { "steamid", _session.SteamId.ToString() } };
        var getCountryResponseStr = await SteamWeb.PostAsync(
            "https://api.steampowered.com/IUserAccountService/GetUserCountry/v1?access_token=" + _session.AccessToken,
            null, getCountryBody);

        // Parse response json to object
        var response = JsonSerializer.Deserialize<GetUserCountryResponse>(getCountryResponseStr);
        if (response?.Response == null) throw new Exception("Failed to get country");

        return response.Response.Country;
    }

    private async Task<SetAccountPhoneNumberResponse> _setAccountPhoneNumber(string phoneNumber, string countryCode)
    {
        var setPhoneBody = new NameValueCollection
        {
            { "phone_number", phoneNumber },
            { "phone_country_code", countryCode }
        };
        var getCountryResponseStr = await SteamWeb.PostAsync(
            "https://api.steampowered.com/IPhoneService/SetAccountPhoneNumber/v1?access_token=" + _session.AccessToken,
            null, setPhoneBody);

        var response = JsonSerializer.Deserialize<SetAccountPhoneNumberResponse>(getCountryResponseStr);
        if (response?.Response == null) throw new Exception("Failed to set phone number");
        return response;
    }

    private async Task<bool> _isAccountWaitingForEmailConfirmation()
    {
        var waitingForEmailResponse = await SteamWeb.PostAsync(
            "https://api.steampowered.com/IPhoneService/IsAccountWaitingForEmailConfirmation/v1?access_token=" +
            _session.AccessToken, null, null);

        // Parse response json to object
        var response =
            JsonSerializer.Deserialize<IsAccountWaitingForEmailConfirmationResponse>(waitingForEmailResponse);
        if (response?.Response == null) throw new Exception("Failed to check if account is waiting for email");

        return response.Response.AwaitingEmailConfirmation;
    }

    private async Task<bool> _sendPhoneVerificationCode()
    {
        await SteamWeb.PostAsync(
            "https://api.steampowered.com/IPhoneService/SendPhoneVerificationCode/v1?access_token=" +
            _session.AccessToken, null, null);
        return true;
    }

    public static string GenerateDeviceId()
    {
        return "android:" + Guid.NewGuid();
    }

    private class GetUserCountryResponse
    {
        [JsonPropertyName("response")] public GetUserCountryResponseResponse Response { get; set; } = null!;
    }

    private class GetUserCountryResponseResponse
    {
        [JsonPropertyName("country")] public string Country { get; set; } = string.Empty;
    }

    private class SetAccountPhoneNumberResponse
    {
        [JsonPropertyName("response")] public SetAccountPhoneNumberResponseResponse Response { get; set; } = null!;
    }

    private class SetAccountPhoneNumberResponseResponse
    {
        [JsonPropertyName("confirmation_email_address")]
        public string? ConfirmationEmailAddress { get; set; } = null;

        [JsonPropertyName("phone_number_formatted")]
        public string? PhoneNumberFormatted { get; set; } = null;
    }

    private class IsAccountWaitingForEmailConfirmationResponse
    {
        [JsonPropertyName("response")]
        public IsAccountWaitingForEmailConfirmationResponseResponse Response { get; set; } = null!;
    }

    private class IsAccountWaitingForEmailConfirmationResponseResponse
    {
        [JsonPropertyName("awaiting_email_confirmation")]
        public bool AwaitingEmailConfirmation { get; set; } = false;

        [JsonPropertyName("seconds_to_wait")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int SecondsToWait { get; set; }
    }

    private class AddAuthenticatorResponse
    {
        [JsonPropertyName("response")] public SteamGuardAccount? Response { get; set; }
    }

    private class FinalizeAuthenticatorResponse
    {
        [JsonPropertyName("response")] public FinalizeAuthenticatorInternalResponse? Response { get; set; }

        internal class FinalizeAuthenticatorInternalResponse
        {
            [JsonPropertyName("success")] public bool Success { get; set; } = false;

            [JsonPropertyName("want_more")] public bool WantMore { get; set; } = false;

            [JsonPropertyName("server_time")]
            [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
            public long ServerTime { get; set; }

            [JsonPropertyName("status")]
            [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
            public int? Status { get; set; } = null;
        }
    }
}