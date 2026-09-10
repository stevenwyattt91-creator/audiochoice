using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioChoice.Api.Services;

public sealed class PushNotificationOptions
{
    public bool Enabled { get; init; }

    /// <summary>The 10-character key id Apple shows when the .p8 is created.</summary>
    public string KeyID { get; init; } = string.Empty;

    /// <summary>The Apple Developer team the key belongs to.</summary>
    public string TeamID { get; init; } = string.Empty;

    /// <summary>
    /// The .p8 contents, PEM or bare base64.
    /// </summary>
    /// <remarks>
    /// Supplied as configuration rather than a path so it can come from Key Vault the same way the
    /// transactional email key does, instead of a file that has to be mounted into the image.
    /// </remarks>
    public string PrivateKey { get; init; } = string.Empty;

    /// <summary>The app the notification is addressed to, which APNs calls the topic.</summary>
    public string BundleID { get; init; } = "com.audiochoice.mobile";

    /// <summary>
    /// Which APNs environment to send to.
    /// </summary>
    /// <remarks>
    /// The key in use is scoped to production, which is what App Store and TestFlight builds
    /// register against. A debug build installed from Xcode registers against sandbox and will not
    /// receive anything until a sandbox-capable key is configured here, which is why this is a
    /// setting rather than a constant.
    /// </remarks>
    public string Host { get; init; } = "https://api.push.apple.com";
}

/// <summary>
/// Tells a listener's devices that something they were waiting for is ready.
/// </summary>
public interface IPushNotificationSender
{
    Task NotifyScanReady(
        IReadOnlyList<DevicePushToken> devices,
        string bookTitle,
        CancellationToken cancellationToken);
}

/// <summary>
/// The sender used when push is not configured.
/// </summary>
/// <remarks>
/// Silent rather than throwing. A scan that finished successfully must not be reported as failed
/// because nobody could be told about it, and the same reasoning already governs the catalog alert
/// email in <c>ScanWorker</c>.
/// </remarks>
public sealed class DisabledPushNotificationSender(ILogger<DisabledPushNotificationSender> logger)
    : IPushNotificationSender
{
    private bool _warned;

    public Task NotifyScanReady(
        IReadOnlyList<DevicePushToken> devices,
        string bookTitle,
        CancellationToken cancellationToken)
    {
        // Once, not per scan. This is the ordinary state of a local or preview deployment, and a
        // line for every completed scan would be noise rather than information.
        if (!_warned && devices.Count > 0)
        {
            _warned = true;
            logger.LogInformation(
                "Push notifications are not configured, so {DeviceCount} device(s) were not told a " +
                "scan finished. Set AudioChoice:Push:PrivateKey to enable them.",
                devices.Count);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends through Apple Push Notification service.
/// </summary>
/// <remarks>
/// APNs requires HTTP/2 and a provider token: an ES256 JWT signed with the .p8, carrying the team as
/// issuer and the key id in its header. The token is valid for an hour, so it is cached rather than
/// re-signed per notification -- signing is cheap but Apple rejects a provider that mints a new one
/// for every request as abusive.
/// </remarks>
public sealed class ApnsPushNotificationSender : IPushNotificationSender
{
    private readonly HttpClient _client;
    private readonly PushNotificationOptions _options;
    private readonly IDevicePushTokenStore _tokens;
    private readonly ILogger<ApnsPushNotificationSender> _logger;
    private readonly ECDsa _signingKey;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _providerToken;
    private DateTimeOffset _providerTokenExpiry = DateTimeOffset.MinValue;

    public ApnsPushNotificationSender(
        HttpClient client,
        PushNotificationOptions options,
        IDevicePushTokenStore tokens,
        ILogger<ApnsPushNotificationSender> logger)
    {
        _client = client;
        _options = options;
        _tokens = tokens;
        _logger = logger;
        _client.BaseAddress = new Uri(options.Host);
        // Not negotiated. APNs speaks HTTP/2 only, and without this the first request downgrades
        // and is refused in a way that looks like an authentication problem.
        _client.DefaultRequestVersion = HttpVersion.Version20;
        _client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        _signingKey = ECDsa.Create();
        _signingKey.ImportPkcs8PrivateKey(DecodePrivateKey(options.PrivateKey), out _);
    }

    public async Task NotifyScanReady(
        IReadOnlyList<DevicePushToken> devices,
        string bookTitle,
        CancellationToken cancellationToken)
    {
        var apple = devices
            .Where(value => string.Equals(value.Platform, DevicePushPlatforms.Apns, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (apple.Count == 0) return;

        var payload = JsonSerializer.Serialize(new
        {
            aps = new
            {
                alert = new
                {
                    title = "Your audiobook is ready",
                    body = string.IsNullOrWhiteSpace(bookTitle)
                        ? "The scan finished. Your filters are ready to use."
                        : $"{bookTitle} finished scanning. Your filters are ready to use.",
                },
                sound = "default",
            },
        });

        var providerToken = await ProviderToken(cancellationToken);
        foreach (var device in apple)
        {
            try
            {
                await Send(device, payload, providerToken, cancellationToken);
            }
            catch (Exception error)
            {
                // One unreachable device must not stop the others being told.
                _logger.LogWarning(error, "A push notification to one device could not be sent.");
            }
        }
    }

    private async Task Send(
        DevicePushToken device,
        string payload,
        string providerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/3/device/{device.Token}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("authorization", $"bearer {providerToken}");
        request.Headers.TryAddWithoutValidation("apns-topic", _options.BundleID);
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        // Collapsing on the book means a listener who imports the same audiobook twice sees one
        // notification rather than a stack of identical ones.
        request.Headers.TryAddWithoutValidation("apns-collapse-id", Collapse(device.Token));

        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        // 410 means the app was uninstalled, and 400 BadDeviceToken means the token never belonged
        // here. Both are permanent, so the row is dropped rather than retried forever on every
        // future scan.
        if (response.StatusCode == HttpStatusCode.Gone ||
            (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("BadDeviceToken")))
        {
            _tokens.Remove(device.Token);
            _logger.LogInformation("Removed a device token APNs reported as no longer valid.");
            return;
        }
        _logger.LogWarning(
            "APNs refused a notification with {StatusCode}: {Body}", (int)response.StatusCode, body);
    }

    /// <summary>
    /// A stable, short id derived from the device token.
    /// </summary>
    /// <remarks>
    /// APNs caps a collapse id at 64 bytes and a device token is longer than that, so it is hashed
    /// rather than truncated -- truncating would make two devices on the same account collide.
    /// </remarks>
    private static string Collapse(string deviceToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken)))[..32];

    private async Task<string> ProviderToken(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Renewed early rather than on expiry. A token that lapses mid-batch would fail the
            // remaining devices for no reason.
            if (_providerToken is not null && DateTimeOffset.UtcNow < _providerTokenExpiry)
            {
                return _providerToken;
            }

            var issuedAt = DateTimeOffset.UtcNow;
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "ES256",
                kid = _options.KeyID,
                typ = "JWT",
            }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = _options.TeamID,
                iat = issuedAt.ToUnixTimeSeconds(),
            }));
            var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");
            // IEEE P1363 rather than DER: a JWS signature is the raw r||s pair, and the default
            // ASN.1 encoding here produces a token Apple rejects as malformed.
            var signature = _signingKey.SignData(
                signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            _providerToken = $"{header}.{claims}.{Base64Url(signature)}";
            _providerTokenExpiry = issuedAt.AddMinutes(50);
            return _providerToken;
        }
        finally { _tokenLock.Release(); }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Reads the .p8 whether it arrives as PEM or as bare base64.
    /// </summary>
    /// <remarks>
    /// Both shapes turn up in practice: pasted from the file it keeps its BEGIN/END lines, while a
    /// value round-tripped through a secret store often loses them along with the newlines.
    /// </remarks>
    private static byte[] DecodePrivateKey(string value)
    {
        var trimmed = value.Trim();
        var body = trimmed.Contains("-----BEGIN", StringComparison.Ordinal)
            ? string.Concat(trimmed
                .Split('\n', '\r')
                .Where(line => !line.StartsWith("-----", StringComparison.Ordinal)))
            : trimmed;
        return Convert.FromBase64String(body.Replace(" ", string.Empty));
    }
}
