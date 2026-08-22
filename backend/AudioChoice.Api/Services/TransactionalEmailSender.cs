using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public sealed class TransactionalEmailOptions
{
    public bool Enabled { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string BaseURL { get; init; } = "https://api.resend.com/";
    public string FromAddress { get; init; } = "AudioChoice <no-reply@audiochoiceapp.com>";
    public string ReplyToAddress { get; init; } = "support@audiochoiceapp.com";
    public string ActionBaseURL { get; init; } = "https://app.audiochoiceapp.com";
}

public interface ITransactionalEmailSender
{
    Task SendEmailVerification(
        string recipient,
        Uri verificationURL,
        CancellationToken cancellationToken);

    Task SendPasswordReset(
        string recipient,
        Uri resetURL,
        CancellationToken cancellationToken);

    Task SendSupportMessage(
        string userEmail,
        string displayName,
        string subject,
        string message,
        CancellationToken cancellationToken);

    Task SendNewCatalogScanAlert(
        Guid scanID,
        string sourceFileName,
        BookFingerprint fingerprint,
        ScanResult result,
        CancellationToken cancellationToken);
}

public sealed class DisabledTransactionalEmailSender : ITransactionalEmailSender
{
    public Task SendEmailVerification(
        string recipient,
        Uri verificationURL,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendPasswordReset(
        string recipient,
        Uri resetURL,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSupportMessage(
        string userEmail,
        string displayName,
        string subject,
        string message,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendNewCatalogScanAlert(
        Guid scanID,
        string sourceFileName,
        BookFingerprint fingerprint,
        ScanResult result,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ResendTransactionalEmailSender(
    HttpClient httpClient,
    TransactionalEmailOptions options) : ITransactionalEmailSender
{
    public Task SendEmailVerification(
        string recipient,
        Uri verificationURL,
        CancellationToken cancellationToken) => Send(
            recipient,
            "Verify your AudioChoice email",
            $"Verify your AudioChoice email by opening this link:\n\n{verificationURL}",
            options.ReplyToAddress,
            cancellationToken);

    public Task SendPasswordReset(
        string recipient,
        Uri resetURL,
        CancellationToken cancellationToken) => Send(
            recipient,
            "Reset your AudioChoice password",
            $"Reset your AudioChoice password by opening this link:\n\n{resetURL}\n\nIf you did not request this, you can ignore this message.",
            options.ReplyToAddress,
            cancellationToken);

    public async Task SendSupportMessage(
        string userEmail,
        string displayName,
        string subject,
        string message,
        CancellationToken cancellationToken)
    {
        await Send(
            options.ReplyToAddress,
            $"AudioChoice support: {subject}",
            $"From: {displayName} <{userEmail}>\n\n{message}",
            userEmail,
            cancellationToken);
        await Send(
            userEmail,
            "We received your AudioChoice support message",
            $"Hi {displayName},\n\nWe received your message about “{subject}”. The AudioChoice support team will reach out as soon as possible.\n\nAudioChoice Support",
            options.ReplyToAddress,
            cancellationToken);
    }

    public Task SendNewCatalogScanAlert(
        Guid scanID,
        string sourceFileName,
        BookFingerprint fingerprint,
        ScanResult result,
        CancellationToken cancellationToken)
    {
        var title = string.IsNullOrWhiteSpace(fingerprint.WorkTitle)
            ? sourceFileName
            : fingerprint.WorkTitle;
        var duration = fingerprint.Duration is double seconds
            ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss")
            : "Unknown";
        var series = string.IsNullOrWhiteSpace(fingerprint.SeriesTitle)
            ? "Not provided"
            : fingerprint.SeriesNumber is int number
                ? $"{fingerprint.SeriesTitle} · Book {number}"
                : fingerprint.SeriesTitle;

        var text = $"""
            A newly scanned audiobook edition is ready in AudioChoice.

            Title: {title}
            Author: {fingerprint.Author ?? "Not provided"}
            Series: {series}
            Edition: {fingerprint.EditionType ?? "Not provided"}
            Part: {fingerprint.PartNumber?.ToString() ?? "Not provided"}
            Runtime: {duration}
            Source file: {sourceFileName}
            Filter events: {result.Events.Count}
            Scanner version: {result.ScannerVersion}
            Scan ID: {scanID}
            Fingerprint: {fingerprint.Sha256}

            This alert is sent only for a newly created catalog scan. Re-imports and saved-transcript reanalysis do not send another alert.
            """;

        return Send(
            options.ReplyToAddress,
            $"New AudioChoice scan completed: {title}",
            text,
            options.ReplyToAddress,
            cancellationToken);
    }

    private async Task Send(
        string recipient,
        string subject,
        string text,
        string replyTo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new ResendEmailRequest(
                options.FromAddress,
                [recipient],
                subject,
                text,
                replyTo))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            options.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private sealed record ResendEmailRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("reply_to")] string ReplyTo);
}
