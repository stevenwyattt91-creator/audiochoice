using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Processing;
using AudioChoice.Api.Services;
using QRCoder;
#if POSTGRES
using Npgsql;
#endif

var builder = WebApplication.CreateBuilder(args);
var maximumUploadBytes = builder.Configuration.GetValue<long?>(
    "AudioChoice:MaximumUploadBytes") ?? 21_474_836_480;
var openAIOptions = builder.Configuration
    .GetSection("AudioChoice:OpenAI")
    .Get<OpenAIProcessingOptions>()
    ?? new OpenAIProcessingOptions();
var externalAuthOptions = builder.Configuration
    .GetSection("AudioChoice:Authentication")
    .Get<ExternalAuthOptions>() ?? new ExternalAuthOptions();
var transactionalEmailOptions = builder.Configuration
    .GetSection("AudioChoice:TransactionalEmail")
    .Get<TransactionalEmailOptions>() ?? new TransactionalEmailOptions();
var databaseOptions = builder.Configuration
    .GetSection("AudioChoice:Database")
    .Get<DatabaseOptions>() ?? new DatabaseOptions();
var temporaryAudioOptions = builder.Configuration
    .GetSection("AudioChoice:TemporaryAudioStorage")
    .Get<TemporaryAudioStorageOptions>() ?? new TemporaryAudioStorageOptions();

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maximumUploadBytes;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

// The Android clients do not need CORS, but the separately-hosted Admin and
// Auditor Portals sign in and call this API from a browser. Keep the browser
// allowlist small and explicit rather than opening the API to arbitrary web origins.
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminPortal", policy => policy
        .WithOrigins(
            "https://audiochoiceapp.com",
            "https://www.audiochoiceapp.com",
            "https://audiochoice-coming-soon.stevenwyattt91.chatgpt.site",
            "https://audiochoice.stevenwyattt91.workers.dev",
            "https://admin.audiochoiceapp.com",
            "https://auditor.audiochoiceapp.com",
            "https://audiochoice-admin-portal.stevenwyattt91.chatgpt.site",
            "https://audiochoice-auditor-portal.stevenwyattt91.chatgpt.site")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var dataPaths = new AudioChoiceDataPaths(
    builder.Environment,
    builder.Configuration);
dataPaths.EnsureDirectories();
builder.Services.AddSingleton(dataPaths);
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton(temporaryAudioOptions);
if (databaseOptions.Enabled)
{
    if (string.IsNullOrWhiteSpace(databaseOptions.ConnectionString))
    {
        throw new InvalidOperationException(
            "AudioChoice:Database:ConnectionString is required when PostgreSQL is enabled.");
    }

#if POSTGRES
    builder.Services.AddSingleton(
        NpgsqlDataSource.Create(databaseOptions.ConnectionString));
    builder.Services.AddHostedService<PostgresDatabaseInitializer>();
#else
    throw new InvalidOperationException(
        "This AudioChoice build does not include PostgreSQL support.");
#endif
}
if (temporaryAudioOptions.BlobEnabled)
{
#if POSTGRES
    if (string.IsNullOrWhiteSpace(temporaryAudioOptions.StorageAccountName))
    {
        throw new InvalidOperationException(
            "AudioChoice temporary Blob Storage account is required when direct uploads are enabled.");
    }
    builder.Services.AddSingleton(
        BlobTemporaryAudioStorage.CreateClient(temporaryAudioOptions));
    builder.Services.AddSingleton<ITemporaryAudioStorage, BlobTemporaryAudioStorage>();
    builder.Services.AddSingleton<ICompanionTransferStorage, BlobCompanionTransferStorage>();
    builder.Services.AddSingleton<IAuditReviewMediaStorage, BlobAuditReviewMediaStorage>();
#else
    throw new InvalidOperationException(
        "This AudioChoice build does not include Azure Blob Storage support.");
#endif
}
else
{
    builder.Services.AddSingleton<ITemporaryAudioStorage, LocalTemporaryAudioStorage>();
    builder.Services.AddSingleton<ICompanionTransferStorage, UnavailableCompanionTransferStorage>();
    builder.Services.AddSingleton<IAuditReviewMediaStorage, UnavailableAuditReviewMediaStorage>();
}
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IScanCatalog, PostgresScanCatalog>();
#endif
}
else
{
    builder.Services.AddSingleton<IScanCatalog>(
        new InMemoryScanCatalog(dataPaths.Catalog));
}
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IScanJobQueue, PostgresScanJobQueue>();
#endif
}
else
{
    builder.Services.AddSingleton<IScanJobQueue, ScanJobQueue>();
}
builder.Services.AddHostedService<TemporaryAudioCleanupService>();
builder.Services.AddHostedService<CompanionTransferCleanupService>();
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IAccountStore, PostgresAccountStore>();
    builder.Services.AddSingleton<IEntitlementStore, PostgresEntitlementStore>();
    builder.Services.AddSingleton<ICompanionTransferStore, PostgresCompanionTransferStore>();
#endif
}
else
{
    builder.Services.AddSingleton<IAccountStore>(
        new FileAccountStore(dataPaths.Accounts));
    builder.Services.AddSingleton<IEntitlementStore>(
        new FileEntitlementStore(dataPaths));
    builder.Services.AddSingleton<ICompanionTransferStore>(
        new FileCompanionTransferStore(dataPaths));
}
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IUserLibraryStore, PostgresUserLibraryStore>();
#endif
}
else
{
    builder.Services.AddSingleton<IUserLibraryStore>(
        new FileUserLibraryStore(dataPaths.UserLibrary));
}
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IUserDataStore, PostgresUserDataStore>();
    builder.Services.AddSingleton<IInternalAuditStore, PostgresInternalAuditStore>();
#endif
}
else
{
    builder.Services.AddSingleton<IUserDataStore, FileUserDataStore>();
    builder.Services.AddSingleton<IInternalAuditStore, DisabledInternalAuditStore>();
}
builder.Services.AddSingleton(externalAuthOptions);
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IConversionConsentStore, PostgresConversionConsentStore>();
#endif
}
else
{
    builder.Services.AddSingleton<IConversionConsentStore>(
        new FileConversionConsentStore(dataPaths.ConversionConsents));
}
builder.Services.AddHttpClient<ExternalIdentityVerifier>();
builder.Services.AddSingleton(transactionalEmailOptions);
if (transactionalEmailOptions.Enabled)
{
    if (string.IsNullOrWhiteSpace(transactionalEmailOptions.ApiKey) ||
        !Uri.TryCreate(
            transactionalEmailOptions.ActionBaseURL,
            UriKind.Absolute,
            out _))
    {
        throw new InvalidOperationException(
            "AudioChoice transactional email configuration is invalid.");
    }

    builder.Services.AddHttpClient(
        "ResendTransactionalEmail",
        client => client.BaseAddress = new Uri(transactionalEmailOptions.BaseURL));
    builder.Services.AddSingleton<ITransactionalEmailSender>(services =>
        new ResendTransactionalEmailSender(
            services.GetRequiredService<IHttpClientFactory>()
                .CreateClient("ResendTransactionalEmail"),
            transactionalEmailOptions));
}
else
{
    builder.Services.AddSingleton<ITransactionalEmailSender,
        DisabledTransactionalEmailSender>();
}
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("authentication", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("support", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0
            }));
});
if (temporaryAudioOptions.BlobTranscriptEnabled)
{
#if POSTGRES
    if (!temporaryAudioOptions.BlobEnabled)
    {
        throw new InvalidOperationException(
            "Blob audio storage must be enabled when Blob transcript storage is enabled.");
    }
    builder.Services.AddSingleton<IPrivateTranscriptStore, BlobPrivateTranscriptStore>();
#else
    throw new InvalidOperationException("This AudioChoice build does not include Blob transcript storage.");
#endif
}
else
{
    builder.Services.AddSingleton<IPrivateTranscriptStore, FilePrivateTranscriptStore>();
}
builder.Services.AddSingleton(
    builder.Configuration
        .GetSection("AudioChoice:Ffmpeg")
        .Get<FfmpegAudioChunkerOptions>()
    ?? new FfmpegAudioChunkerOptions());
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<IAuditReviewClipPrewarmer, AuditReviewClipPrewarmer>();
builder.Services.AddSingleton<IAudioChunker, FfmpegAudioChunker>();
builder.Services.AddSingleton(openAIOptions);

if (openAIOptions.WorkerEnabled)
{
    if (string.IsNullOrWhiteSpace(openAIOptions.ApiKey))
    {
        throw new InvalidOperationException(
            "AudioChoice:OpenAI:ApiKey is required when the scan worker is enabled.");
    }

    if (openAIOptions.MaximumRetries < 0 ||
        openAIOptions.MaximumJobAttempts <= 0 ||
        openAIOptions.MaximumSegmentsPerAnalysisRequest <= 0 ||
        openAIOptions.MaximumSceneVerificationRequestsPerJob <= 0 ||
        openAIOptions.MaximumChunksPerJob <= 0 ||
        openAIOptions.MaximumTranscriptSegmentsPerJob <= 0 ||
        openAIOptions.MaximumAudioDurationSeconds <= 0)
    {
        throw new InvalidOperationException(
            "AudioChoice OpenAI cost-control configuration is invalid.");
    }

    builder.Services.AddHttpClient(
        "OpenAIProcessing",
        client => client.BaseAddress = new Uri(openAIOptions.BaseURL));

    builder.Services.AddSingleton<ITranscriptionProvider>(services =>
        string.Equals(openAIOptions.TranscriptionProvider, "faster-whisper", StringComparison.OrdinalIgnoreCase)
            ? new FasterWhisperTranscriptionProvider(
                new HttpClient
                {
                    BaseAddress = new Uri(openAIOptions.FasterWhisperEndpoint),
                    // A faster-whisper chunk can legitimately take several minutes on
                    // a busy GPU. The HttpClient default is 100 seconds, which cancels
                    // otherwise healthy chunks mid-transcription.
                    Timeout = TimeSpan.FromMinutes(10)
                })
            : new OpenAITranscriptionProvider(
                services.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("OpenAIProcessing"),
                openAIOptions,
                services.GetRequiredService<ILogger<OpenAITranscriptionProvider>>()));
    builder.Services.AddSingleton<ConcurrentChunkTranscriber>();

    builder.Services.AddSingleton<IContentAnalysisProvider>(services =>
        new OpenAIContentAnalysisProvider(
            services.GetRequiredService<IHttpClientFactory>()
                .CreateClient("OpenAIProcessing"),
            openAIOptions,
            services.GetRequiredService<AudioChoiceDataPaths>(),
            services.GetRequiredService<ILogger<OpenAIContentAnalysisProvider>>()));

    builder.Services.AddSingleton<IScanPipeline, ScanPipeline>();
    builder.Services.AddHostedService<ScanWorker>();
}

var app = builder.Build();

static string ScanLane(HttpContext context) =>
    string.Equals(
        context.Request.Headers["X-AudioChoice-Scan-Channel"].ToString(),
        "ios-beta",
        StringComparison.OrdinalIgnoreCase)
        ? ScanProcessingLanes.IOSBetaLambda
        : ScanProcessingLanes.AzureOpenAI;

app.UseCors("AdminPortal");
app.UseRateLimiter();
var uploadFolder = dataPaths.Uploads;

if (openAIOptions.WorkerEnabled)
{
    var catalog = app.Services.GetRequiredService<IScanCatalog>();
    var queue = app.Services.GetRequiredService<IScanJobQueue>();
    foreach (var job in catalog.RecoverableJobs())
    {
        catalog.SetJobStatus(job.ID, CloudScanStatus.Queued);
        queue.TryQueue(job.ID);
    }
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var anonymousAuthenticationPath =
        path == "/v1/auth/register" ||
        path == "/v1/auth/login" ||
        path == "/v1/auth/verify-email" ||
        path == "/v1/auth/password-reset/request" ||
        path == "/v1/auth/password-reset/confirm" ||
        path == "/v1/auth/external";

    if (context.Request.Path == "/health" ||
        anonymousAuthenticationPath ||
        (context.Request.Path.StartsWithSegments("/v1/companion/transfers") &&
         context.Request.Path.Value?.EndsWith("/qr", StringComparison.OrdinalIgnoreCase) == true) ||
        (context.Request.Method == HttpMethods.Put &&
         context.Request.Path.StartsWithSegments("/v1/uploads")))
    {
        await next();
        return;
    }

    var suppliedHeader = context.Request.Headers.Authorization.ToString();
    var suppliedToken = suppliedHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? suppliedHeader[7..]
        : string.Empty;

    var configuredToken = app.Configuration["AudioChoice:ApiToken"] ?? string.Empty;
    var developmentTokenMatches = configuredToken.Length == suppliedToken.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(configuredToken),
            System.Text.Encoding.UTF8.GetBytes(suppliedToken));
    var user = context.RequestServices.GetRequiredService<IAccountStore>()
        .Authenticate(suppliedToken);
    if (!developmentTokenMatches && user is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (user is not null)
    {
        context.Items[typeof(AuthUser)] = user;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/auth/register", async (
    RegisterRequest request,
    IAccountStore accounts,
    ITransactionalEmailSender emailSender,
    CancellationToken cancellationToken) =>
{
    var registration = accounts.Register(request);
    if (registration is null)
    {
        return Results.BadRequest(new
        {
            error = "Use a valid email and a password of at least 12 characters. The email may already be registered."
        });
    }

    await emailSender.SendEmailVerification(
        registration.Verification.Email,
        BuildActionURL(
            transactionalEmailOptions.ActionBaseURL,
            "verify-email",
            registration.Verification.Token),
        cancellationToken);

    return Results.Ok(registration.Response);
}).RequireRateLimiting("authentication");

app.MapPost("/v1/auth/login", (LoginRequest request, IAccountStore accounts) =>
{
    var response = accounts.Login(request);
    return response is null ? Results.Unauthorized() : Results.Ok(response);
}).RequireRateLimiting("authentication");

app.MapGet("/v1/auth/verify-email", (
    string token,
    IAccountStore accounts) =>
    accounts.VerifyEmail(token)
        ? Results.Ok(new AuthActionResponse("verified"))
        : Results.BadRequest(new { error = "The verification link is invalid or expired." }))
    .RequireRateLimiting("authentication");

app.MapPost("/v1/auth/password-reset/request", async (
    PasswordResetRequest request,
    IAccountStore accounts,
    ITransactionalEmailSender emailSender,
    CancellationToken cancellationToken) =>
{
    var reset = accounts.CreatePasswordReset(request.Email);
    if (reset is not null)
    {
        await emailSender.SendPasswordReset(
            reset.Email,
            BuildActionURL(
                transactionalEmailOptions.ActionBaseURL,
                "reset-password",
                reset.Token),
            cancellationToken);
    }

    return Results.Accepted(
        value: new AuthActionResponse("accepted"));
}).RequireRateLimiting("authentication");

app.MapPost("/v1/auth/password-reset/confirm", (
    PasswordResetConfirmRequest request,
    IAccountStore accounts) =>
    accounts.ResetPassword(request.Token, request.NewPassword)
        ? Results.Ok(new AuthActionResponse("password-reset"))
        : Results.BadRequest(new { error = "The reset link is invalid, expired, or the new password is too short." }))
    .RequireRateLimiting("authentication");

app.MapPost("/v1/auth/external", async (
    ExternalLoginRequest request,
    ExternalIdentityVerifier verifier,
    IAccountStore accounts,
    CancellationToken cancellationToken) =>
{
    var identity = await verifier.Verify(request.Provider, request.AuthorizationCode, request.IdentityToken, cancellationToken);
    return identity is null
        ? Results.Unauthorized()
        : Results.Ok(accounts.LoginExternal(identity.Provider, identity.Subject, identity.Email, request.DisplayName));
}).RequireRateLimiting("authentication");

app.MapGet("/v1/auth/identities", (
    HttpContext context,
    IAccountStore accounts) =>
{
    var user = CurrentUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new LinkedIdentitiesResponse(accounts.ListIdentityProviders(user.ID)));
});

app.MapPost("/v1/auth/identities/link", async (
    ExternalLoginRequest request,
    HttpContext context,
    ExternalIdentityVerifier verifier,
    IAccountStore accounts,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var identity = await verifier.Verify(
        request.Provider,
        request.AuthorizationCode,
        request.IdentityToken,
        cancellationToken);
    if (identity is null) return Results.Unauthorized();
    return accounts.LinkExternal(
        user.ID,
        identity.Provider,
        identity.Subject,
        identity.Email)
        ? Results.Ok(new LinkedIdentitiesResponse(accounts.ListIdentityProviders(user.ID)))
        : Results.Conflict(new { error = "That sign-in identity is already linked." });
}).RequireRateLimiting("authentication");

app.MapPost("/v1/auth/logout", (HttpContext context, IAccountStore accounts) =>
{
    var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
    accounts.Logout(token);
    return Results.NoContent();
});

app.MapPost("/v1/auth/logout-all", (HttpContext context, IAccountStore accounts) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    accounts.LogoutAll(user.ID);
    return Results.NoContent();
});

app.MapGet("/v1/account", (HttpContext context, IEntitlementStore entitlements) =>
{
    var user = CurrentUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new
        {
            id = user.ID,
            email = user.Email,
            displayName = user.DisplayName,
            provider = user.Provider,
            access = entitlements.Access(user.ID)
        });
});

app.MapGet("/v1/account/access", (HttpContext context, IEntitlementStore entitlements) =>
{
    var user = CurrentUser(context);
    return user is null ? Results.Unauthorized() : Results.Ok(entitlements.Access(user.ID));
});

app.MapPost("/v1/admin/accounts/{userID:guid}/entitlements", (
    Guid userID,
    EntitlementGrantRequest request,
    HttpContext context,
    IEntitlementStore entitlements) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    try { return Results.Ok(entitlements.Grant(userID, request)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});

app.MapPost("/v1/companion/transfers", async (
    CompanionTransferCreateRequest request,
    HttpContext context,
    IEntitlementStore entitlements,
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!entitlements.Access(user.ID).CanUseCompanion)
        return Results.Forbid();
    if (!storage.IsAvailable)
        return Results.Problem("Companion transfers are not configured yet.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var sha256 = request.Sha256?.Trim().ToUpperInvariant();
    if (request.FileSize <= 0 || request.FileSize > maximumUploadBytes ||
        !System.Text.RegularExpressions.Regex.IsMatch(sha256 ?? string.Empty, "^[0-9A-F]{64}$"))
    {
        return Results.BadRequest(new { error = "A valid audiobook size and SHA-256 fingerprint are required." });
    }
    var fileName = Path.GetFileName(request.FileName);
    var extension = Path.GetExtension(fileName).ToLowerInvariant();
    if (extension is not ".m4b" and not ".mp3")
        return Results.BadRequest(new { error = "Companion transfers accept M4B and MP3 files only." });
    var expiration = DateTimeOffset.UtcNow.AddHours(2);
    var receiverCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var transfer = transfers.Create(user.ID, fileName, request.ContentType, request.FileSize, sha256!, expiration, receiverCode);
    var authorization = await storage.CreateUploadAuthorization(transfer, cancellationToken);
    if (authorization is null) return Results.Problem("A transfer upload could not be authorized.", statusCode: StatusCodes.Status503ServiceUnavailable);
    var receiverURL = $"audiochoice-beta://transfer/{transfer.ID}?code={receiverCode}";
    return Results.Ok(new CompanionTransferUploadResponse(transfer.ID, authorization.UploadURL, authorization.Method, authorization.Headers, receiverURL, expiration));
});

app.MapPost("/v1/companion/transfers/{transferID:guid}/complete", async (
    Guid transferID,
    HttpContext context,
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var transfer = transfers.Find(transferID);
    if (transfer is null || transfer.OwnerUserID != user.ID) return Results.NotFound();
    if (transfer.ExpiresAt <= DateTimeOffset.UtcNow || transfer.Status != "authorized")
        return Results.BadRequest(new { error = "This transfer is no longer available." });
    if (!await storage.VerifyUpload(transfer, cancellationToken))
        return Results.BadRequest(new { error = "The uploaded audiobook is missing or has an incorrect byte count." });
    return transfers.MarkUploaded(transferID) ? Results.NoContent() : Results.BadRequest(new { error = "The transfer could not be finalized." });
});

app.MapGet("/v1/companion/transfers/{transferID:guid}/qr", (
    Guid transferID,
    string code,
    ICompanionTransferStore transfers) =>
{
    var transfer = transfers.Find(transferID);
    var suppliedHash = FileCompanionTransferStore.Hash(code ?? string.Empty);
    if (transfer is null || transfer.Status != "uploaded" || transfer.ExpiresAt <= DateTimeOffset.UtcNow ||
        !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(transfer.ReceiverCodeHash), System.Text.Encoding.UTF8.GetBytes(suppliedHash)))
        return Results.NotFound();
    var receiverURL = $"audiochoice-beta://transfer/{transferID}?code={Uri.EscapeDataString(code ?? string.Empty)}";
    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(receiverURL, QRCodeGenerator.ECCLevel.M);
    var png = new PngByteQRCode(data).GetGraphic(8);
    // This endpoint is consumed directly by the website's <img> element.
    // Do not send a download filename, otherwise some browsers treat the QR
    // response as an attachment instead of rendering it as an image.
    return Results.File(png, "image/png");
});

app.MapGet("/v1/companion/transfers/{transferID:guid}/claim", async (
    Guid transferID,
    string code,
    HttpContext context,
    IEntitlementStore entitlements,
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!entitlements.Access(user.ID).CanUseCompanion) return Results.Forbid();
    var transfer = transfers.Find(transferID);
    if (transfer is null || transfer.OwnerUserID != user.ID || transfer.Status != "uploaded" || transfer.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.NotFound();
    var suppliedHash = FileCompanionTransferStore.Hash(code ?? string.Empty);
    if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(transfer.ReceiverCodeHash), System.Text.Encoding.UTF8.GetBytes(suppliedHash)))
        return Results.NotFound();
    var downloadURL = await storage.CreateDownloadAuthorization(transfer, cancellationToken);
    return downloadURL is null ? Results.NotFound() : Results.Ok(new CompanionTransferClaimResponse(transfer.ID, transfer.FileName, transfer.ContentType, transfer.FileSize, transfer.Sha256, downloadURL, transfer.ExpiresAt));
});

app.MapPost("/v1/companion/transfers/{transferID:guid}/received", async (
    Guid transferID,
    HttpContext context,
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var transfer = transfers.Find(transferID);
    if (transfer is null || transfer.OwnerUserID != user.ID || transfer.Status != "uploaded") return Results.NotFound();
    if (!transfers.MarkReceived(transferID)) return Results.BadRequest();
    await storage.Delete(transfer, cancellationToken);
    return Results.NoContent();
});

app.MapPost("/v1/support/messages", async (
    SupportMessageRequest request,
    HttpContext context,
    ITransactionalEmailSender emailSender,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();

    var subject = request.Subject?.Trim() ?? string.Empty;
    var message = request.Message?.Trim() ?? string.Empty;
    if (subject.Length is < 3 or > 120)
    {
        return Results.BadRequest(new
        {
            error = "Enter a support subject between 3 and 120 characters."
        });
    }
    if (message.Length is < 10 or > 5_000)
    {
        return Results.BadRequest(new
        {
            error = "Enter a support message between 10 and 5,000 characters."
        });
    }

    await emailSender.SendSupportMessage(
        user.Email,
        user.DisplayName,
        subject,
        message,
        cancellationToken);
    return Results.Ok(new { status = "received" });
}).RequireRateLimiting("support");

app.MapPost("/v1/conversion-consents", (
    ConversionConsentRequest request,
    HttpContext context,
    IConversionConsentStore consents) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    try { return Results.Ok(consents.Record(user, request)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});

app.MapGet("/v1/admin/conversion-consents", (
    string? query,
    int? limit,
    HttpContext context,
    IConversionConsentStore consents) =>
    IsConfiguredApiToken(context, app.Configuration)
        ? Results.Ok(consents.Search(query, limit ?? 50))
        : Results.Unauthorized());

app.MapGet("/v1/admin/conversion-consents/{id:guid}/document", (
    Guid id,
    HttpContext context,
    IConversionConsentStore consents) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    var record = consents.Find(id);
    if (record is null) return Results.NotFound();
    var document = JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    });
    return Results.File(document, "application/json", $"audiochoice-consent-{record.ID}.json");
});

app.MapPost("/v1/admin/scans/reanalysis", async (
    AdminReanalysisRequest request,
    HttpContext context,
    IScanCatalog catalog,
    IScanJobQueue queue,
    IPrivateTranscriptStore transcriptStore,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }

    var transcript = await transcriptStore.Load(request.Fingerprint, cancellationToken);
    if (transcript is null || transcript.IsComplete is false || transcript.Segments.Count == 0)
    {
        return Results.BadRequest(new
        {
            error = "No complete saved transcript exists for this exact audiobook fingerprint."
        });
    }

    var job = catalog.CreateReanalysisJob(request.OwnerUserID, request.Fingerprint);
    if (job is null)
    {
        return Results.BadRequest(new
        {
            error = "The saved transcript could not be linked to the selected account and audiobook."
        });
    }

    queue.TryQueue(job.ID);
    return Results.Json(
        new CloudScanResponse(job.Status, job.ID),
        statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/v1/admin/scans/jobs/{scanID:guid}", (
    Guid scanID,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }

    var job = catalog.FindJob(scanID);
    if (job is null) return Results.NotFound();
    var progress = catalog.GetJobProgress(scanID);
    return Results.Ok(new CloudScanResponse(
        job.Status,
        job.ID,
        job.Result,
        ProgressPercent: progress.Percent,
        ProgressStage: progress.Stage,
        CompletedChunks: progress.CompletedChunks,
        TotalChunks: progress.TotalChunks,
        PercentComplete: progress.Percent));
});

app.MapGet("/v1/admin/transcripts", async (
    HttpContext context,
    IScanCatalog catalog,
    IPrivateTranscriptStore transcriptStore,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }

    var transcripts = new List<AdminTranscriptInfo>();
    foreach (var fingerprint in catalog.ListFingerprints())
    {
        var transcript = await transcriptStore.Load(fingerprint, cancellationToken);
        if (transcript is null || transcript.Segments.Count == 0) continue;
        transcripts.Add(new AdminTranscriptInfo(
            fingerprint,
            transcript.Segments.Count,
            transcript.IsComplete is not false,
            transcript.TranscriptionModel,
            transcript.CreatedAt));
    }
    return Results.Ok(transcripts);
});

app.MapPut("/v1/admin/editions/metadata", (
    AdminEditionMetadataRequest request,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.WorkTitle) || request.WorkTitle.Trim().Length > 300)
    {
        return Results.BadRequest(new { error = "A valid audiobook title is required." });
    }

    return catalog.UpdateEditionMetadata(request)
        ? Results.NoContent()
        : Results.NotFound(new { error = "No completed scan matches that exact fingerprint." });
});

app.MapGet("/v1/admin/explore", (
    HttpContext context,
    IScanCatalog catalog) =>
{
    return IsConfiguredApiToken(context, app.Configuration)
        ? Results.Ok(catalog.ListExploreBooks())
        : Results.Unauthorized();
});

// Returns only aggregate audit-pricing information for an already scanned
// Explore edition. Individual event details remain protected behind the
// authenticated internal portals.
app.MapGet("/v1/admin/explore/{catalogID}/audit-estimate", (
    string catalogID,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    var result = catalog.FindExploreResult(catalogID);
    return result is null
        ? Results.NotFound(new { error = "No verified filter result is available for this edition." })
        : Results.Ok(FocusedAuditPricing.Estimate(result.Events));
});

// Creates the same focused audit job that new completed scans create automatically.
// This is intentionally an administrator-token route so an already scanned catalog
// edition can be queued for internal testing without a second scan or transcript run.
app.MapPost("/v1/admin/explore/{catalogID}/focused-audit", (
    string catalogID,
    HttpContext context,
    IInternalAuditStore audits) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    var assignmentID = audits.CreateFocusedAssignmentForExploreCatalog(catalogID);
    return assignmentID is null
        ? Results.NotFound(new { error = "No scanned Explore edition was found for this catalog entry." })
        : Results.Ok(new { assignmentID });
});

// This maintenance operation is deliberately protected by the staging admin token.
// It is used to reset the internal test queue before seeding a replacement audit job.
app.MapDelete("/v1/admin/audits", async (
    HttpContext context,
    IInternalAuditStore audits,
    IAuditReviewMediaStorage media,
    IAuditReviewClipPrewarmer clipPrewarmer,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    var assignments = audits.DeleteAllAssignments();
    foreach (var assignmentID in assignments)
        await media.DeletePrefix(assignmentID, cancellationToken);
    return Results.Ok(new { deleted = assignments.Count });
});

// Beta editions are matched locally by their immutable edition metadata after
// a local AAX-to-M4B conversion changes the file bytes. This exposes only the
// already-published filter result for the exact Explore catalog edition; it
// never starts a scan or returns a transcript.
app.MapGet("/v1/explore/{catalogID}/filter-result", (
    string catalogID,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (CurrentUser(context) is null) return Results.Unauthorized();
    var result = catalog.FindExploreResult(catalogID);
    return result is null
        ? Results.NotFound(new { error = "No verified filter result is available for this edition." })
        : Results.Ok(new CloudScanResponse(CloudScanStatus.Available, Result: result));
});

app.MapDelete("/v1/admin/explore/{catalogID}", (
    string catalogID,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }
    return catalog.HideExploreBook(catalogID)
        ? Results.NoContent()
        : Results.NotFound(new { error = "Explore audiobook was not found." });
});

app.MapGet("/v1/internal/me", (HttpContext context, IInternalAuditStore audits) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var access = audits.Access(user.ID);
    return access is { Active: true } ? Results.Ok(access) : Results.Forbid();
});

app.MapPost("/v1/internal/bootstrap-admin", (HttpContext context, IInternalAuditStore audits) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return audits.ClaimInitialAdmin(user.ID)
        ? Results.Ok(new { status = "initial-admin-created" })
        : Results.Conflict(new { error = "An AudioChoice administrator is already configured." });
});

app.MapGet("/v1/internal/audits", (HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access is null ? Results.Forbid() : Results.Ok(audits.Dashboard(access.UserID, access.Role == "admin"));
});

app.MapGet("/v1/internal/earnings", (HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access is null ? Results.Forbid() : Results.Ok(audits.Earnings(access.UserID));
});

app.MapPost("/v1/internal/audits/{assignmentID:guid}/claim", (Guid assignmentID, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access is null) return Results.Forbid();
    return audits.Claim(assignmentID, access.UserID) ? Results.NoContent() : Results.Conflict(new { error = "This audit is no longer available." });
});

app.MapGet("/v1/internal/audits/{assignmentID:guid}", (Guid assignmentID, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access is null) return Results.Forbid();
    var value = audits.Workspace(assignmentID, access.UserID, access.Role == "admin");
    return value is null ? Results.NotFound() : Results.Ok(value);
});

// The administrator attaches a source M4B only after a scan has created its
// focused audit job. The source is held in a private container and never gets
// a downloadable link in either portal.
app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/review-source", async (
    Guid assignmentID,
    IFormFile file,
    HttpContext context,
    IInternalAuditStore audits,
    IAuditReviewMediaStorage media,
    IAuditReviewClipPrewarmer clipPrewarmer,
    CancellationToken cancellationToken) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    if (!media.IsAvailable) return Results.Problem("Review media storage is not configured.", statusCode: 503);
    if (file.Length <= 0 || !Path.GetExtension(file.FileName).Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Attach the matching M4B audiobook file." });

    var safeName = Path.GetFileName(file.FileName);
    await using var input = file.OpenReadStream();
    var stored = await media.StoreSource(assignmentID, safeName, string.IsNullOrWhiteSpace(file.ContentType) ? "audio/mp4" : file.ContentType, input, cancellationToken);
    var source = new AuditReviewSource(stored.ObjectName, safeName, "audio/mp4", stored.Length);
    if (!audits.SaveReviewSource(access.UserID, assignmentID, source))
    {
        await media.DeletePrefix(assignmentID, cancellationToken);
        return Results.BadRequest(new { error = "This audit cannot accept review audio in its current state." });
    }
    clipPrewarmer.Queue(assignmentID);
    return Results.Ok(new { status = "ready", fileName = safeName, fileSize = stored.Length, clips = "preparing" });
}).DisableAntiforgery();

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/review-source/authorization", async (
    Guid assignmentID, AuditReviewSourceUploadRequest request, HttpContext context, IInternalAuditStore audits, IAuditReviewMediaStorage media, CancellationToken cancellationToken) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    if (!media.IsAvailable) return Results.Problem("Review media storage is not configured.", statusCode: 503);
    if (request.FileSize <= 0 || !Path.GetExtension(request.FileName).Equals(".m4b", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "Attach the matching M4B audiobook file." });
    var upload = await media.CreateSourceUploadAuthorization(assignmentID, "audio/mp4", cancellationToken);
    return upload is null ? Results.Problem("Could not prepare private review storage.", statusCode: 503) : Results.Ok(upload);
});

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/review-source/complete", async (
    Guid assignmentID, AuditReviewSourceUploadRequest request, HttpContext context, IInternalAuditStore audits, IAuditReviewMediaStorage media, IAuditReviewClipPrewarmer clipPrewarmer, CancellationToken cancellationToken) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    var stored = await media.VerifySource(assignmentID, cancellationToken);
    if (stored is null || stored.Length != request.FileSize) return Results.BadRequest(new { error = "The private M4B upload did not finish correctly. Please try again." });
    var source = new AuditReviewSource(stored.ObjectName, Path.GetFileName(request.FileName), "audio/mp4", stored.Length);
    if (!audits.SaveReviewSource(access.UserID, assignmentID, source))
        return Results.BadRequest(new { error = "This audit cannot accept review audio in its current state." });
    clipPrewarmer.Queue(assignmentID);
    return Results.Ok(new { status = "ready", clips = "preparing" });
});

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/review-clips/prewarm", (Guid assignmentID, HttpContext context, IInternalAuditStore audits, IAuditReviewMediaStorage media, IAuditReviewClipPrewarmer clipPrewarmer) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin" && !IsConfiguredApiToken(context, app.Configuration)) return Results.Forbid();
    if (!media.IsAvailable || audits.ReviewSource(assignmentID) is null) return Results.NotFound(new { error = "Attach the review source before preparing clips." });
    clipPrewarmer.Queue(assignmentID);
    return Results.Accepted($"/v1/internal/admin/audits/{assignmentID}/review-clips/prewarm", new { status = "preparing" });
});

// Auditors receive a generated M4A context clip for one event. A long event is
// preserved as one continuous clip so an auditor can verify the whole scene.
app.MapGet("/v1/internal/audits/{assignmentID:guid}/segments/{candidateID:guid}/audio", async (
    Guid assignmentID,
    Guid candidateID,
    bool asJson,
    HttpContext context,
    IInternalAuditStore audits,
    IAuditReviewMediaStorage media,
    IProcessRunner processes,
    FfmpegAudioChunkerOptions ffmpeg,
    CancellationToken cancellationToken) =>
{
    var access = InternalAccessFor(context, audits);
    if (access is null) return Results.Forbid();
    var workspace = audits.Workspace(assignmentID, access.UserID, access.Role == "admin");
    if (workspace is null || !workspace.ReviewMediaAvailable) return Results.NotFound(new { error = "Review audio is not ready for this job." });
    var candidate = workspace.Candidates.FirstOrDefault(value => value.ID == candidateID);
    if (candidate is null) return Results.NotFound();

    var clip = audits.ReviewClip(assignmentID, candidateID);
    if (clip is null)
    {
        var source = audits.ReviewSource(assignmentID);
        if (source is null) return Results.NotFound(new { error = "Review source is unavailable." });
        var isLongScene = candidate.EndSeconds - candidate.StartSeconds > 180;
        var from = Math.Max(0, isLongScene ? candidate.StartSeconds - 15 : candidate.StartSeconds - 15);
        var to = isLongScene ? candidate.EndSeconds + 15 : candidate.EndSeconds + 15;
        await using var materialized = await media.MaterializeSource(source.ObjectName, cancellationToken);
        var output = Path.Combine(Path.GetTempPath(), $"audiochoice-audit-clip-{Guid.NewGuid():N}.m4a");
        try
        {
            var result = await processes.Run(ffmpeg.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", from.ToString(CultureInfo.InvariantCulture), "-to", to.ToString(CultureInfo.InvariantCulture), "-i", materialized.Path, "-vn", "-c:a", "aac", "-b:a", "96k", output],
                cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(output))
                return Results.Problem("AudioChoice could not prepare this review clip.", statusCode: 500);
            await using var clipInput = File.OpenRead(output);
            var stored = await media.StoreClip(assignmentID, candidateID, clipInput, cancellationToken);
            clip = new AuditReviewClip(stored.ObjectName, from, to);
            if (!audits.SaveReviewClip(assignmentID, candidateID, clip))
                return Results.Problem("AudioChoice could not save this review clip.", statusCode: 500);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }
    var url = await media.CreateReadAuthorization(clip.ObjectName, cancellationToken);
    return url is null ? Results.NotFound(new { error = "The review clip has expired." }) : asJson ? Results.Ok(new { url = url.ToString() }) : Results.Redirect(url.ToString());
});

app.MapPut("/v1/internal/audits/{assignmentID:guid}/segments/{candidateID:guid}",
    (Guid assignmentID, Guid candidateID, AuditDecisionRequest request, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access is null) return Results.Forbid();
    var value = audits.SaveDecision(assignmentID, candidateID, access.UserID, access.Role == "admin", request);
    return value is null ? Results.BadRequest(new { error = "The decision or assignment is invalid." }) : Results.Ok(value);
});

app.MapPost("/v1/internal/audits/{assignmentID:guid}/complete", (Guid assignmentID, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access is null) return Results.Forbid();
    return audits.Complete(assignmentID, access.UserID, access.Role == "admin")
        ? Results.NoContent() : Results.BadRequest(new { error = "Every candidate must have a saved decision before completion." });
});

app.MapGet("/v1/internal/admin/users", (HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" ? Results.Ok(audits.Users()) : Results.Forbid();
});

app.MapPut("/v1/internal/admin/users/{userID:guid}/access", (Guid userID, string role, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin" && !IsConfiguredApiToken(context, app.Configuration)) return Results.Forbid();
    var actor = access?.UserID ?? userID;
    return audits.Grant(actor, userID, role) ? Results.NoContent() : Results.BadRequest();
});

app.MapPut("/v1/internal/admin/users/{userID:guid}/active", (Guid userID, InternalUserStatusRequest request, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    return audits.SetActive(access.UserID, userID, request.Active) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/v1/internal/admin/audits", (CreateAuditAssignmentRequest request, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    var id = audits.CreateAssignment(access.UserID, request);
    return id is null ? Results.BadRequest() : Results.Created($"/v1/internal/audits/{id}", new { id });
});

app.MapGet("/v1/internal/admin/dashboard", (HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" ? Results.Ok(audits.AdminDashboard()) : Results.Forbid();
});

app.MapGet("/v1/internal/admin/catalog", (string? search, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" ? Results.Ok(audits.Catalog(search)) : Results.Forbid();
});

app.MapGet("/v1/internal/admin/catalog/{editionID:guid}", (Guid editionID, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    var edition = audits.CatalogEdition(editionID);
    return edition is null ? Results.NotFound() : Results.Ok(edition);
});

app.MapGet("/v1/internal/admin/catalog/{editionID:guid}/filters", (Guid editionID, HttpContext context, IInternalAuditStore audits, IScanCatalog catalog) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    var edition = audits.CatalogEdition(editionID);
    if (edition is null) return Results.NotFound();
    var result = catalog.FindResult(edition.Fingerprint);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/v1/internal/admin/catalog/{editionID:guid}/transcript", async (Guid editionID, HttpContext context, IInternalAuditStore audits, IPrivateTranscriptStore transcripts, CancellationToken cancellationToken) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    var edition = audits.CatalogEdition(editionID);
    if (edition is null) return Results.NotFound();
    var transcript = await transcripts.Load(edition.Fingerprint, cancellationToken);
    return transcript is null ? Results.NotFound(new { error = "No saved transcript is available for this edition." }) : Results.Ok(transcript);
});

app.MapPost("/v1/internal/admin/catalog/{editionID:guid}/reprocess", (Guid editionID, HttpContext context, IInternalAuditStore audits, IScanCatalog catalog, IScanJobQueue queue) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin") return Results.Forbid();
    var edition = audits.CatalogEdition(editionID);
    if (edition is null) return Results.NotFound();
    var job = catalog.CreateReanalysisJob(access.UserID, edition.Fingerprint, ScanLane(context));
    if (job is null) return Results.BadRequest(new { error = "No saved transcript or account link exists for this edition." });
    queue.TryQueue(job.ID);
    return Results.Accepted($"/v1/internal/admin/catalog/{editionID}/reprocess", new { status = job.Status, jobID = job.ID });
});

app.MapGet("/v1/internal/admin/payments", (HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" ? Results.Ok(audits.Payments()) : Results.Forbid();
});

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/approve", async (Guid assignmentID, HttpContext context, IInternalAuditStore audits, IAuditReviewMediaStorage media, CancellationToken cancellationToken) =>
{
    var access = InternalAccessFor(context, audits);
    if (access?.Role != "admin" || !audits.ApproveAssignment(access.UserID, assignmentID)) return Results.BadRequest();
    if (audits.ScheduleReviewMediaCleanup(access.UserID, assignmentID)) await media.DeletePrefix(assignmentID, cancellationToken);
    return Results.NoContent();
});

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/compensation", (Guid assignmentID, AuditCompensationRequest request, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" && audits.SetAssignmentCompensation(access.UserID, assignmentID, request.Amount)
        ? Results.NoContent() : Results.BadRequest();
});

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/reject", (Guid assignmentID, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" && audits.RejectAssignment(access.UserID, assignmentID)
        ? Results.NoContent() : Results.BadRequest();
});

app.MapPost("/v1/internal/admin/audits/{assignmentID:guid}/mark-paid", (Guid assignmentID, AuditPaymentRequest request, HttpContext context, IInternalAuditStore audits) =>
{
    var access = InternalAccessFor(context, audits);
    return access?.Role == "admin" && audits.MarkAssignmentPaid(access.UserID, assignmentID, request.Note)
        ? Results.NoContent() : Results.BadRequest();
});

app.MapGet("/v1/library", (
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(library.List(user.ID));
});

app.MapPut("/v1/import/cover", (
    EmbeddedCoverUploadRequest request,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (CurrentUser(context) is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Base64Data) || request.Base64Data.Length > 2_800_000)
        return Results.BadRequest(new { error = "Embedded cover artwork is missing or too large." });
    if (request.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        return Results.BadRequest(new { error = "Embedded cover artwork must be JPEG, PNG, or WebP." });
    byte[] bytes;
    try { bytes = Convert.FromBase64String(request.Base64Data); }
    catch (FormatException) { return Results.BadRequest(new { error = "Embedded cover artwork is not valid base64." }); }
    return catalog.SaveEditionCover(request.Fingerprint, bytes, request.ContentType)
        ? Results.NoContent()
        : Results.NotFound(new { error = "No audiobook edition matches the complete fingerprint." });
});

app.MapPut("/v1/library", (
    LibraryBookUpsertRequest request,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(library.Upsert(user.ID, request));
});

app.MapDelete("/v1/library/{bookID:guid}", (
    Guid bookID,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return library.DeleteBook(user.ID, bookID)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapPut("/v1/library/{bookID:guid}/progress", (
    Guid bookID,
    PlaybackProgressRequest request,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var book = library.UpdateProgress(user.ID, bookID, request);
    return book is null ? Results.BadRequest() : Results.Ok(book);
});

app.MapPost("/v1/reader/alignments", async (
    ReaderAlignmentRequest request,
    HttpContext context,
    IUserLibraryStore library,
    IPrivateTranscriptStore transcripts,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (request.EpubText.Length is 0 or > 8_000_000)
        return Results.BadRequest(new { error = "The reading edition is empty or too large to sync." });

    var book = library.List(user.ID).FirstOrDefault(value => value.ID == request.LibraryBookID);
    if (book is null) return Results.NotFound(new { error = "That audiobook is not in your library." });

    var transcript = await transcripts.Load(book.Fingerprint, cancellationToken);
    if (transcript is null || transcript.Segments.Count == 0)
        return Results.NotFound(new { error = "No private timing data is available for this audiobook yet." });

    // This response contains only positions and time ranges. It never exposes the
    // private transcript, and the supplied EPUB text is not written to disk.
    var ranges = ReaderAlignment.Create(transcript.Segments, request.EpubText);
    logger.LogInformation(
        "Reader alignment created {RangeCount} ranges for library book {BookID}; timing coverage {FirstStart:F1} to {LastEnd:F1} seconds.",
        ranges.Count,
        book.ID,
        ranges.FirstOrDefault()?.StartTime ?? -1,
        ranges.LastOrDefault()?.EndTime ?? -1);
    return Results.Ok(new ReaderAlignmentResponse(ranges));
});

app.MapPut("/v1/library/{bookID:guid}/favorite", (
    Guid bookID,
    FavoriteRequest request,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var book = library.UpdateFavorite(user.ID, bookID, request.IsFavorite);
    return book is null ? Results.NotFound() : Results.Ok(book);
});

app.MapGet("/v1/library/{bookID:guid}/bookmarks", (
    Guid bookID,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var bookmarks = library.ListBookmarks(user.ID, bookID);
    return bookmarks is null ? Results.NotFound() : Results.Ok(bookmarks);
});

app.MapPost("/v1/library/{bookID:guid}/bookmarks", (
    Guid bookID,
    BookmarkCreateRequest request,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var bookmark = library.AddBookmark(user.ID, bookID, request);
    return bookmark is null ? Results.BadRequest() : Results.Ok(bookmark);
});

app.MapDelete("/v1/library/bookmarks/{bookmarkID:guid}", (
    Guid bookmarkID,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return library.DeleteBookmark(user.ID, bookmarkID)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapGet("/v1/library/{bookID:guid}/filter-settings", (
    Guid bookID,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var settings = data.GetBookFilterSettings(user.ID, bookID);
    return settings is null ? Results.NotFound() : Results.Ok(settings);
});

app.MapPut("/v1/library/{bookID:guid}/filter-settings", (
    Guid bookID,
    BookFilterSettingsUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var settings = data.SaveBookFilterSettings(user.ID, bookID, request);
    return settings is null ? Results.BadRequest() : Results.Ok(settings);
});

app.MapGet("/v1/filter-profiles", (
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    return user is null ? Results.Unauthorized() : Results.Ok(data.ListProfiles(user.ID));
});

app.MapPost("/v1/filter-profiles", (
    FilterProfileUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var value = data.SaveProfile(user.ID, null, request);
    return value is null ? Results.BadRequest() : Results.Created($"/v1/filter-profiles/{value.ID}", value);
});

app.MapPut("/v1/filter-profiles/{profileID:guid}", (
    Guid profileID,
    FilterProfileUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var value = data.SaveProfile(user.ID, profileID, request);
    return value is null ? Results.NotFound() : Results.Ok(value);
});

app.MapDelete("/v1/filter-profiles/{profileID:guid}", (
    Guid profileID,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return data.DeleteProfile(user.ID, profileID) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/v1/notes", (
    Guid? libraryBookID,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    return user is null ? Results.Unauthorized() : Results.Ok(data.ListNotes(user.ID, libraryBookID));
});

app.MapPost("/v1/notes", (
    BookNoteUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var value = data.SaveNote(user.ID, null, request);
    return value is null ? Results.BadRequest() : Results.Created($"/v1/notes/{value.ID}", value);
});

app.MapPut("/v1/notes/{noteID:guid}", (
    Guid noteID,
    BookNoteUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var value = data.SaveNote(user.ID, noteID, request);
    return value is null ? Results.NotFound() : Results.Ok(value);
});

app.MapDelete("/v1/notes/{noteID:guid}", (
    Guid noteID,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return data.DeleteNote(user.ID, noteID) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/v1/collections", (
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    return user is null ? Results.Unauthorized() : Results.Ok(data.ListCollections(user.ID));
});

app.MapPost("/v1/collections", (
    CollectionUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var value = data.SaveCollection(user.ID, null, request);
    return value is null ? Results.BadRequest() : Results.Created($"/v1/collections/{value.ID}", value);
});

app.MapPut("/v1/collections/{collectionID:guid}", (
    Guid collectionID,
    CollectionUpsertRequest request,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var value = data.SaveCollection(user.ID, collectionID, request);
    return value is null ? Results.NotFound() : Results.Ok(value);
});

app.MapDelete("/v1/collections/{collectionID:guid}", (
    Guid collectionID,
    HttpContext context,
    IUserDataStore data) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return data.DeleteCollection(user.ID, collectionID) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/v1/scans/requests", async (
    CloudScanRequest request,
    HttpContext context,
    IScanCatalog catalog,
    IScanJobQueue queue,
    IPrivateTranscriptStore transcriptStore,
    CancellationToken cancellationToken) =>
{
    var result = catalog.FindResult(request.Fingerprint);
    if (result is not null)
    {
        // Playback and imports must always receive the most recently saved filter
        // result. Scanner upgrades are intentional, admin-controlled work; they
        // must never hide an already usable filter profile or start paid work merely
        // because someone opens a book.
        return Results.Ok(new CloudScanResponse(
            CloudScanStatus.Available,
            Result: result));
    }

    var activeJob = catalog.FindActiveJob(request.Fingerprint);
    if (activeJob is not null)
    {
        var user = CurrentUser(context);
        var canAccess = user is not null && catalog.CanAccessJob(activeJob.ID, user.ID);
        return Results.Ok(new CloudScanResponse(
            activeJob.Status,
            canAccess ? activeJob.ID : null,
            activeJob.Result));
    }

    var currentUser = CurrentUser(context);
    var savedTranscript = await transcriptStore.Load(request.Fingerprint, cancellationToken);
    if (currentUser is not null && savedTranscript is not null &&
        savedTranscript.Segments.Count > 0)
    {
        var reanalysis = catalog.CreateReanalysisJob(
            currentUser.ID, request.Fingerprint, ScanLane(context));
        if (reanalysis is not null)
        {
            queue.TryQueue(reanalysis.ID);
            return Results.Ok(new CloudScanResponse(reanalysis.Status, reanalysis.ID));
        }
    }

    return Results.Ok(new CloudScanResponse(
        CloudScanStatus.UploadRequired));
});

app.MapGet("/v1/explore", (HttpContext context, IScanCatalog catalog) =>
{
    if (databaseOptions.Enabled && CurrentUser(context) is null) return Results.Unauthorized();
    return Results.Ok(catalog.ListExploreBooks());
});

app.MapGet("/v1/explore/{catalogID}/cover", (string catalogID, HttpContext context, IScanCatalog catalog) =>
{
    if (databaseOptions.Enabled && CurrentUser(context) is null) return Results.Unauthorized();
    var cover = catalog.FindExploreCover(catalogID);
    return cover is null ? Results.NotFound() : Results.File(cover.Value.Bytes, cover.Value.ContentType);
});

app.MapPut("/v1/explore/{catalogID}/cover", async (
    string catalogID,
    HttpContext context,
    IScanCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (databaseOptions.Enabled &&
        CurrentUser(context) is null &&
        !IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }
    if (context.Request.ContentLength is null or <= 0 or > 2_000_000)
        return Results.BadRequest(new { error = "Cover artwork must be between 1 byte and 2 MB." });
    var contentType = context.Request.ContentType?.ToLowerInvariant();
    if (contentType is not ("image/jpeg" or "image/png" or "image/webp"))
        return Results.BadRequest(new { error = "Cover artwork must be JPEG, PNG, or WebP." });
    await using var memory = new MemoryStream((int)context.Request.ContentLength.Value);
    await context.Request.Body.CopyToAsync(memory, cancellationToken);
    var isMaintenanceRequest = IsConfiguredApiToken(context, app.Configuration);
    return catalog.SaveExploreCover(catalogID, memory.ToArray(), contentType, isMaintenanceRequest)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapPost("/v1/uploads/authorizations", async (
    CloudUploadAuthorizationRequest request,
    HttpContext context,
    IScanCatalog catalog,
    ITemporaryAudioStorage temporaryAudio,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (databaseOptions.Enabled && user is null)
    {
        return Results.Unauthorized();
    }

    if (request.FileSize <= 0 ||
        request.FileSize > maximumUploadBytes ||
        request.FileSize != request.Fingerprint.FileSize)
    {
        return Results.BadRequest(new { error = "File size does not match the fingerprint." });
    }

    var expiration = DateTimeOffset.UtcNow.AddMinutes(15);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var upload = catalog.CreateUpload(
        user?.ID ?? Guid.Empty,
        request,
        expiration,
        token);
    var directAuthorization = await temporaryAudio.CreateDirectUploadAuthorization(
        upload,
        expiration,
        cancellationToken);
    if (directAuthorization is not null)
    {
        return Results.Ok(directAuthorization);
    }
    var uploadURL = new Uri(
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/v1/uploads/{upload.ID}");

    return Results.Ok(new CloudUploadAuthorizationResponse(
        upload.ID,
        uploadURL,
        "PUT",
        new Dictionary<string, string>
        {
            ["Content-Type"] = request.ContentType,
            ["X-AudioChoice-Upload-Token"] = token
        },
        expiration));
});

app.MapPut("/v1/uploads/{uploadID:guid}", async (
    Guid uploadID,
    HttpContext context,
    IScanCatalog catalog,
    ITemporaryAudioStorage temporaryAudio,
    CancellationToken cancellationToken) =>
{
    if (temporaryAudio.UsesDirectUpload)
    {
        return Results.BadRequest(new { error = "Use the authorized private Blob upload URL." });
    }
    var upload = catalog.FindUpload(uploadID);
    if (upload is null)
    {
        return Results.NotFound();
    }

    var suppliedToken = context.Request.Headers["X-AudioChoice-Upload-Token"].ToString();
    var suppliedTokenHash = InMemoryScanCatalog.HashToken(suppliedToken);
    if (upload.ExpiresAt <= DateTimeOffset.UtcNow ||
        !CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(upload.TokenHash),
            System.Text.Encoding.UTF8.GetBytes(suppliedTokenHash)))
    {
        return Results.Unauthorized();
    }

    if (context.Request.ContentLength is long contentLength &&
        contentLength != upload.FileSize)
    {
        return Results.BadRequest(new { error = "Uploaded byte count is incorrect." });
    }

    var destination = Path.Combine(uploadFolder, $"{upload.ID}.audio");
    long bytesWritten = 0;
    using var uploadedHash = IncrementalHash.CreateHash(
        HashAlgorithmName.SHA256);

    await using (var output = new FileStream(
        destination,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        81920,
        FileOptions.Asynchronous))
    {
        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await context.Request.Body.ReadAsync(
            buffer,
            cancellationToken)) > 0)
        {
            bytesWritten += bytesRead;
            if (bytesWritten > upload.FileSize)
            {
                output.Close();
                File.Delete(destination);
                return Results.BadRequest(new { error = "Upload exceeds the authorized size." });
            }

            await output.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);

            uploadedHash.AppendData(
                buffer,
                0,
                bytesRead);
        }
    }

    if (bytesWritten != upload.FileSize)
    {
        File.Delete(destination);
        return Results.BadRequest(new { error = "Uploaded byte count is incorrect." });
    }

    var uploadedSha256 = Convert.ToHexString(
        uploadedHash.GetHashAndReset());

    if (!uploadedSha256.Equals(
        upload.Fingerprint.Sha256,
        StringComparison.OrdinalIgnoreCase))
    {
        File.Delete(destination);
        return Results.BadRequest(new
        {
            error = "Uploaded audio does not match the authorized fingerprint."
        });
    }

    catalog.MarkUploaded(uploadID, destination);
    return Results.NoContent();
});

app.MapPost("/v1/uploads/{uploadID:guid}/complete", async (
    Guid uploadID,
    HttpContext context,
    IScanCatalog catalog,
    ITemporaryAudioStorage temporaryAudio,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!temporaryAudio.UsesDirectUpload)
    {
        return Results.BadRequest(new { error = "Direct Blob uploads are not enabled." });
    }
    var upload = catalog.FindUpload(uploadID);
    if (upload is null || upload.OwnerUserID != user.ID) return Results.NotFound();
    if (upload.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(
        new { error = "The upload authorization expired." });
    var storedReference = await temporaryAudio.CompleteDirectUpload(
        upload,
        cancellationToken);
    if (storedReference is null)
    {
        return Results.BadRequest(new { error = "The uploaded byte count is incorrect or the Blob is missing." });
    }
    return catalog.MarkUploaded(uploadID, storedReference)
        ? Results.NoContent()
        : Results.BadRequest(new { error = "The upload could not be finalized." });
});

app.MapPost("/v1/scans/jobs", (
    CloudScanJobSubmissionRequest request,
    HttpContext context,
    IScanCatalog catalog,
    IScanJobQueue queue) =>
{
    var user = CurrentUser(context);
    if (databaseOptions.Enabled && user is null)
    {
        return Results.Unauthorized();
    }

    var existingResult = catalog.FindResult(request.Fingerprint);
    if (existingResult is not null)
    {
        return Results.Ok(new CloudScanResponse(
            CloudScanStatus.Completed,
            Result: existingResult));
    }

    var job = catalog.CreateJob(
        user?.ID ?? Guid.Empty,
        request.UploadID,
        request.Fingerprint,
        ScanLane(context));
    if (job is null)
    {
        return Results.BadRequest(new
        {
            error = "Upload is missing, incomplete, or belongs to a different fingerprint."
        });
    }

    queue.TryQueue(job.ID);

    return Results.Json(
        new CloudScanResponse(job.Status, job.ID, job.Result),
        statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/v1/scans/jobs/{scanID:guid}", (
    Guid scanID,
    HttpContext context,
    IScanCatalog catalog) =>
{
    var user = CurrentUser(context);
    if (databaseOptions.Enabled && user is null)
    {
        return Results.Unauthorized();
    }

    var job = catalog.FindJob(scanID);

    if (job is null || !catalog.CanAccessJob(scanID, user?.ID ?? Guid.Empty))
    {
        return Results.NotFound();
    }

    var progress = catalog.GetJobProgress(scanID);
    return Results.Ok(new CloudScanResponse(
        job.Status,
        job.ID,
        job.Result,
        ProgressPercent: progress.Percent,
        ProgressStage: progress.Stage,
        CompletedChunks: progress.CompletedChunks,
        TotalChunks: progress.TotalChunks,
        PercentComplete: progress.Percent));
});

static Uri BuildActionURL(
    string actionBaseURL,
    string path,
    string token)
{
    var baseURL = new Uri(actionBaseURL.TrimEnd('/') + "/");
    return new Uri(
        baseURL,
        $"{path}?token={Uri.EscapeDataString(token)}");
}

static AuthUser? CurrentUser(HttpContext context) =>
    context.Items.TryGetValue(typeof(AuthUser), out var value)
        ? value as AuthUser
        : null;

static InternalAccess? InternalAccessFor(HttpContext context, IInternalAuditStore audits)
{
    var user = CurrentUser(context);
    if (user is null) return null;
    var access = audits.Access(user.ID);
    return access is { Active: true } ? access : null;
}

static bool IsConfiguredApiToken(HttpContext context, IConfiguration configuration)
{
    var header = context.Request.Headers.Authorization.ToString();
    var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header[7..]
        : string.Empty;
    var configured = configuration["AudioChoice:ApiToken"] ?? string.Empty;
    return configured.Length > 0 && configured.Length == supplied.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(configured),
            System.Text.Encoding.UTF8.GetBytes(supplied));
}

app.Run();

static class FocusedAuditPricing
{
    private static readonly Guid SexualContent = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SelfHarm = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid LegacySelfHarm = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly HashSet<Guid> SevereViolenceEvents =
    [
        Guid.Parse("31100000-0000-0000-0000-000000000001"), // legacy graphic violence/gore
        Guid.Parse("31100000-0000-0000-0000-000000000003"),
        Guid.Parse("31100000-0000-0000-0000-000000000004"),
        Guid.Parse("31100000-0000-0000-0000-000000000006"),
        Guid.Parse("31100000-0000-0000-0000-000000000007")
    ];

    public static FocusedAuditEstimate Estimate(IReadOnlyList<ScanEvent> events)
    {
        var eligible = events
            .Where(value => value.CategoryID is var category &&
                (category == SexualContent || category == SelfHarm || category == LegacySelfHarm ||
                 SevereViolenceEvents.Contains(value.EventID)))
            .OrderBy(value => value.StartTime)
            .ThenBy(value => value.EndTime)
            .ToList();

        var paymentGroups = (int)Math.Ceiling(eligible.Count / 7m);
        const decimal basePay = 0.00m;
        const decimal groupRate = 0.30m;
        const decimal longSceneRate = 0.00m;
        const decimal maximumPay = 10.00m;
        var uncapped = basePay + groupRate * paymentGroups;
        var estimated = Math.Min(maximumPay, uncapped);
        return new FocusedAuditEstimate(
            eligible.Count,
            paymentGroups,
            0,
            basePay,
            groupRate * paymentGroups,
            longSceneRate,
            estimated,
            estimated,
            maximumPay);
    }
}

public partial class Program
{
}
