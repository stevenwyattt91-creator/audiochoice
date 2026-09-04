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

    /// <summary>
    /// Whether a verification email is sent when an account is created.
    /// </summary>
    /// <remarks>
    /// Off. Nothing requires a verified address -- sign-in does not check it -- so the email asked
    /// something of every new listener that changed nothing for them, and its only visible effect was
    /// an extra message during sign-up. Password reset is unaffected and still sends.
    ///
    /// Separate from <see cref="Enabled"/> so verification can come back without touching the
    /// sending configuration, which is the part that is awkward to get right.
    /// </remarks>
    public bool VerificationEnabled { get; init; }

    /// <summary>Where operational alerts are sent.</summary>
    public string AlertAddress { get; init; } = "admin@audiochoiceapp.com";

    /// <summary>
    /// Where a listener's filter report is sent, separately from <see cref="AlertAddress"/>.
    /// </summary>
    /// <remarks>
    /// A listener's own report of a specific missed or wrongly-filtered moment is the one
    /// alert kind that needs a named owner reading it before launch, not a shared operational
    /// inbox. Kept as its own setting rather than repointing <see cref="AlertAddress"/> itself,
    /// since every other alert (a new catalog scan completing) has no reason to move with it.
    /// </remarks>
    public string FilterReportAddress { get; init; } = "steven.wyatt@audiochoiceapp.com";
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
        string resetCode,
        CancellationToken cancellationToken);

    Task SendSupportMessage(
        string userEmail,
        string displayName,
        string subject,
        string message,
        CancellationToken cancellationToken);

    /// <summary>
    /// Tells the team a listener reported a filter as wrong.
    /// </summary>
    Task SendFilterReportAlert(
        FilterReport report,
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
        string resetCode,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendSupportMessage(
        string userEmail,
        string displayName,
        string subject,
        string message,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendFilterReportAlert(
        FilterReport report,
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
        string resetCode,
        CancellationToken cancellationToken) => Send(
            recipient,
            "Reset your AudioChoice password",
            // The code comes first, and alone on its line, because the apps complete a reset with it
            // directly. A link is the wrong primary instruction for someone holding a phone: it
            // requires a page to land on, and the account it recovers is only reachable in the app.
            // The link is kept below for anyone reading this on a computer.
            $"""
            Your AudioChoice password reset code:

            {resetCode}

            Open AudioChoice, choose "Forgot password", and enter this code with your new password.

            The code lasts 15 minutes and can be used once.

            You can also reset in a browser: {resetURL}

            If you did not ask to reset your password, you can ignore this message and nothing will change.
            """,
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

    public Task SendFilterReportAlert(
        FilterReport report,
        CancellationToken cancellationToken)
    {
        var kind = report.Kind == FilterReportKind.MissedContent
            ? "Something played that should have been removed"
            : "Something was removed that should have played";

        // A narrated book's position is a character offset into its text, carried in the same field an
        // audiobook uses for seconds. Formatting one as a timestamp would render offset 84,000 as
        // "23:20:00" -- a number that looks authoritative and means nothing -- so the unit decides how
        // it reads.
        var position = report.PositionUnit == FilterReportPositionUnits.CharacterOffset
            ? $"character {report.PositionSeconds:F0} of the book's text"
            : TimeSpan.FromSeconds(report.PositionSeconds).ToString(@"h\:mm\:ss");
        var window = report.PositionUnit == FilterReportPositionUnits.CharacterOffset
            ? $"{report.WindowSeconds:F0} characters"
            : $"{report.WindowSeconds:F1} seconds";

        var text = $"""
            A listener reported a filter problem in AudioChoice.

            What they reported: {kind}
            Where: {position}
            Span: {window}

            Title: {report.Fingerprint.WorkTitle ?? "Not provided"}
            Author: {report.Fingerprint.Author ?? "Not provided"}
            Edition: {report.Fingerprint.EditionType ?? "Not provided"}
            File type: {report.Fingerprint.FileType}

            Scanner version: {report.ScannerVersion ?? "Not recorded"}
            Scan event: {report.ScanEventID?.ToString() ?? "None -- no control covered that moment"}
            Category: {CategoryName(report.CategoryID)}

            Reported at: {report.ReportedAt:u}
            Report ID: {report.ID}
            Account ID: {report.AccountID}
            Fingerprint: {report.Fingerprint.Sha256}
            Fingerprint version: {report.Fingerprint.Version}
            File size: {report.Fingerprint.FileSize}

            No audio, transcript or text is included, by design: a report says where in which
            edition and which control was responsible, and the passage is looked up from the scan.

            To see this edition's other reports:
            GET /v1/admin/filter-reports?sha256={report.Fingerprint.Sha256}&fingerprintVersion={report.Fingerprint.Version}&fileSize={report.Fingerprint.FileSize}
            """;

        return Send(
            options.FilterReportAddress,
            $"Filter report · {report.Fingerprint.WorkTitle ?? report.Fingerprint.Sha256[..12]}",
            text,
            options.ReplyToAddress,
            cancellationToken);
    }

    /// <summary>
    /// The taxonomy's top-level category names, keyed by the same six GUIDs both clients'
    /// own hardcoded pickers use (<c>IOSContentTaxonomy</c> on iOS, the top-level entries
    /// <c>PlaybackFilterTaxonomy</c> groups on Android). Kept here rather than read from the
    /// audit portal's own <c>audit_filter_categories</c> table so this class, which otherwise
    /// only formats text, does not gain a database dependency for six names that never change.
    /// </summary>
    private static readonly IReadOnlyDictionary<Guid, string> CategoryNames = new Dictionary<Guid, string>
    {
        [Guid.Parse("10000000-0000-0000-0000-000000000001")] = "Sexual Content",
        [Guid.Parse("20000000-0000-0000-0000-000000000001")] = "Profanity",
        [Guid.Parse("30000000-0000-0000-0000-000000000001")] = "Violence",
        [Guid.Parse("40000000-0000-0000-0000-000000000001")] = "Drugs & Alcohol",
        [Guid.Parse("50000000-0000-0000-0000-000000000001")] = "Blasphemy",
        [Guid.Parse("60000000-0000-0000-0000-000000000001")] = "Self-Harm & Suicide",
    };

    private static string CategoryName(Guid? categoryID) => categoryID is { } id
        ? CategoryNames.GetValueOrDefault(id, id.ToString())
        : "Not specified";

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
