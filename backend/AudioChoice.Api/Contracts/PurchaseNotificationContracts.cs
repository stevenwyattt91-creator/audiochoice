using System.Text.Json.Serialization;

namespace AudioChoice.Api.Contracts;

/// <summary>The outer envelope Apple posts for every App Store Server Notification V2.</summary>
public sealed record AppleServerNotification(
    [property: JsonPropertyName("signedPayload")] string? SignedPayload);

/// <summary>The decoded notification payload, once <see cref="AppleServerNotification"/>'s JWS verifies.</summary>
public sealed record AppleNotificationPayload(
    [property: JsonPropertyName("notificationType")] string? NotificationType,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("data")] AppleNotificationData? Data);

public sealed record AppleNotificationData(
    [property: JsonPropertyName("signedTransactionInfo")] string? SignedTransactionInfo);

/// <summary>The transaction fields read out of a notification's nested, separately-signed JWS.</summary>
public sealed record AppleNotificationTransaction(
    [property: JsonPropertyName("originalTransactionId")] string? OriginalTransactionID);

/// <summary>
/// The envelope Google's Pub/Sub push subscription posts for a Real-time Developer Notification.
/// </summary>
public sealed record GooglePubSubEnvelope(
    [property: JsonPropertyName("message")] GooglePubSubMessage? Message);

public sealed record GooglePubSubMessage(
    [property: JsonPropertyName("data")] string? Data);

/// <summary>The base64-decoded body of a Google Real-time Developer Notification.</summary>
public sealed record GoogleRealtimeNotification(
    [property: JsonPropertyName("packageName")] string? PackageName,
    [property: JsonPropertyName("subscriptionNotification")] GoogleSubscriptionNotification? SubscriptionNotification);

public sealed record GoogleSubscriptionNotification(
    [property: JsonPropertyName("purchaseToken")] string? PurchaseToken,
    [property: JsonPropertyName("notificationType")] int NotificationType);
