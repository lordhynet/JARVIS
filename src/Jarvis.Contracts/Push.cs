namespace Jarvis.Contracts;

public sealed record SavePushSubscriptionRequest(string Endpoint, string P256dh, string Auth);
public sealed record BrowserPushSubscription(Guid Id, string Endpoint, string P256dh, string Auth, string KeyFingerprint, DateTimeOffset CreatedAt);
public sealed record PushDelivery(Guid Id, Guid NotificationId, Guid SubscriptionId, int Attempt, string Status,
    DateTimeOffset AvailableAt, Guid? ClaimToken = null, DateTimeOffset? LeaseExpiresAt = null);
