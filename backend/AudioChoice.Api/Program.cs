using Amazon.BedrockRuntime;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Amazon.Polly;
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
var narrationOptions = builder.Configuration
    .GetSection("AudioChoice:Narration")
    .Get<NarrationOptions>() ?? new NarrationOptions();
builder.Services.AddSingleton(narrationOptions);
// Fatal on purpose. Narration synthesis sharing the transcription GPU would slow every
// audiobook scan, and it would look like load rather than like a misconfiguration, so refusing
// to start is the only failure mode that cannot be ignored.
SynthesisRouter.AssertEndpointsAreDistinct(
    openAIOptions.FasterWhisperEndpoint,
    narrationOptions.SynthesisEndpoint);
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
    builder.Services.AddSingleton<IScanCatalog>(services =>
        new InMemoryScanCatalog(
            dataPaths.Catalog,
            services.GetRequiredService<IEditionSignatureStore>()));
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
    builder.Services.AddSingleton<IEditionReferenceStore, PostgresEditionReferenceStore>();
#endif
}
else
{
    builder.Services.AddSingleton<IUserDataStore, FileUserDataStore>();
    builder.Services.AddSingleton<IInternalAuditStore, DisabledInternalAuditStore>();
    builder.Services.AddSingleton<IEditionReferenceStore, UnavailableEditionReferenceStore>();
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
// Apple identity tokens are verified against Apple's published signing keys. Without
// this the claims in a token are simply attacker-supplied text.
builder.Services.AddHttpClient<IAppleSigningKeyProvider, AppleSigningKeyProvider>();
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
// Reconnects a library row to artifacts stored under a different file fingerprint,
// which is what converting or re-tagging an audiobook produces.
// Constructed explicitly because the type also exposes a path-based constructor for
// tests, and container-selected constructors should not depend on that overload set.
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<IFilterReportStore, PostgresFilterReportStore>();
#endif
}
else
{
    builder.Services.AddSingleton<IFilterReportStore>(services =>
        new FileFilterReportStore(services.GetRequiredService<AudioChoiceDataPaths>()));
}
// Text-derived filter events for books with no audiobook. Registered regardless of whether
// narration is switched on, because a stored scan stays readable after the feature is turned
// off again; the endpoint that creates them is what the flag gates.
if (databaseOptions.Enabled)
{
#if POSTGRES
    builder.Services.AddSingleton<INarrationTextScanStore, PostgresNarrationTextScanStore>();
#endif
}
else
{
    builder.Services.AddSingleton<INarrationTextScanStore>(services =>
        new FileNarrationTextScanStore(services.GetRequiredService<AudioChoiceDataPaths>()));
}
// The measurements this feature refuses to derive. Registered unconditionally: a recorded
// measurement stays evidence whether or not narration is switched on.
builder.Services.AddSingleton<INarrationMeasurementStore>(services =>
    new FileNarrationMeasurementStore(services.GetRequiredService<AudioChoiceDataPaths>()));
// Which provider and voice made each chapter. Registered unconditionally, because these records
// explain audio a listener already has and stay meaningful after the feature is switched off.
builder.Services.AddSingleton<INarrationRenderStore>(services =>
    new FileNarrationRenderStore(services.GetRequiredService<AudioChoiceDataPaths>()));
// Registered unconditionally for the same reason as the two stores above, but for a sharper
// failure mode: every route handler that takes this as a parameter -- /v1/narration/voices
// among them -- is still mapped when narration is off, since MapGet runs regardless of any
// runtime flag. Minimal APIs validate parameter binding for every mapped route at startup, so
// leaving this registration inside the narration conditional did not disable those routes; it
// broke every route in the app, because ASP.NET could not resolve this as a service, tried to
// infer it as a request body instead, collided with another inferred parameter, and threw
// while building the route table itself -- before the first request, and before the flag check
// already written inside each of those handlers ever got to run.
builder.Services.AddSingleton<INarrationAgreementStore>(services =>
    new FileNarrationAgreementStore(services.GetRequiredService<AudioChoiceDataPaths>()));
// Same reasoning and the same failure mode: NarrationChapterJobs is bound directly as a route
// parameter (/v1/narration/chapters and its status route), so it has to be resolvable at
// startup regardless of whether synthesis is enabled. It holds nothing but an in-memory
// dictionary, so there is no cost to keeping it registered when the feature is off.
builder.Services.AddSingleton<NarrationChapterJobs>();
builder.Services.AddSingleton<IEditionAliasStore>(services =>
    new FileEditionAliasStore(services.GetRequiredService<AudioChoiceDataPaths>()));
// Looked up only for books whose own file carries no description. A short timeout because a
// missing synopsis must never hold up a request; the backfill simply tries again later.
builder.Services.AddHttpClient<ISynopsisProvider, OpenLibrarySynopsisProvider>(client =>
{
    client.BaseAddress = new Uri("https://openlibrary.org/");
    client.Timeout = TimeSpan.FromSeconds(10);
    // Open Library asks that callers identify themselves so they can contact whoever is
    // responsible for unusual traffic.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "AudioChoice/1.0 (+https://audiochoice.app)");
});
// No base address: this calls both itunes.apple.com and covers.openlibrary.org.
builder.Services.AddHttpClient<ICoverArtProvider, ITunesCoverArtProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "AudioChoice/1.0 (+https://audiochoice.app)");
});
builder.Services.AddHostedService<ExploreCatalogEnrichmentService>();

builder.Services.AddSingleton<IEditionSignatureStore>(services =>
    new FileEditionSignatureStore(services.GetRequiredService<AudioChoiceDataPaths>()));
builder.Services.AddSingleton<IEditionResolver, EditionResolver>();
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
    var usesBedrockAnalysis = string.Equals(
        openAIOptions.AnalysisProvider, "bedrock", StringComparison.OrdinalIgnoreCase);
    var usesOpenAITranscription = !string.Equals(
        openAIOptions.TranscriptionProvider, "faster-whisper", StringComparison.OrdinalIgnoreCase);
    // Required only by whatever actually calls OpenAI. A worker transcribing on the local GPU
    // and classifying on Bedrock needs no OpenAI account, and demanding a key it will never
    // use would refuse to start over a setting that does not apply.
    // A tier may name an OpenAI model even when the pipeline is otherwise on Bedrock, so the
    // key is required by what the tiers actually name rather than by the provider setting.
    var anyTierUsesOpenAI = new[]
        {
            openAIOptions.AnalysisModel,
            openAIOptions.SceneVerificationModel,
            openAIOptions.SceneEscalationModel
        }
            .Append(openAIOptions.EffectiveViolenceVerificationModel)
        .Any(RoutingAnalysisModelClient.IsOpenAIModel);
    if (string.IsNullOrWhiteSpace(openAIOptions.ApiKey) &&
        (!usesBedrockAnalysis || usesOpenAITranscription || anyTierUsesOpenAI))
    {
        throw new InvalidOperationException(
            "AudioChoice:OpenAI:ApiKey is required when the scan worker uses OpenAI for " +
            "transcription or content analysis.");
    }

    if (openAIOptions.MaximumRetries < 0 ||
        openAIOptions.MaximumJobAttempts <= 0 ||
        openAIOptions.ScanWorkerConcurrency <= 0 ||
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
                    // The provider applies the explicit timeout with a linked token.
                    // Infinite here prevents HttpClient's hidden 100-second default.
                    Timeout = Timeout.InfiniteTimeSpan
                },
                TimeSpan.FromSeconds(Math.Max(30, openAIOptions.FasterWhisperTimeoutSeconds)))
            : new OpenAITranscriptionProvider(
                services.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("OpenAIProcessing"),
                openAIOptions,
                services.GetRequiredService<ILogger<OpenAITranscriptionProvider>>()));
    builder.Services.AddSingleton<ConcurrentChunkTranscriber>();

    // How the models are reached. The scanner's judgement does not live here: every policy
    // that decides what a listener has removed stays in OpenAIContentAnalysisProvider, and
    // only the transport is selected. That is what makes a vendor change configuration.
    if (usesBedrockAnalysis)
    {
        builder.Services.AddSingleton<IAmazonBedrockRuntime>(_ =>
            string.IsNullOrWhiteSpace(openAIOptions.BedrockRegion)
                ? new AmazonBedrockRuntimeClient()
                : new AmazonBedrockRuntimeClient(
                    Amazon.RegionEndpoint.GetBySystemName(openAIOptions.BedrockRegion)));

        // Routed rather than chosen once. The tiers are not equally well served: Nova does the
        // bulk of the work well and cheaply, while the sexual-scene stages stay on OpenAI until
        // a frontier model is reachable on Bedrock. Which service a tier uses is decided by the
        // model it names, so moving one later is a configuration change.
        builder.Services.AddSingleton<IAnalysisModelClient>(services =>
            new RoutingAnalysisModelClient(
                bedrock: new BedrockConverseModelClient(
                    services.GetRequiredService<IAmazonBedrockRuntime>(),
                    openAIOptions,
                    services.GetRequiredService<ILogger<BedrockConverseModelClient>>()),
                openAI: new OpenAIResponsesModelClient(
                    services.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("OpenAIProcessing"),
                    openAIOptions,
                    services.GetRequiredService<ILogger<OpenAIResponsesModelClient>>()),
                services.GetRequiredService<ILogger<RoutingAnalysisModelClient>>()));
    }
    else
    {
        builder.Services.AddSingleton<IAnalysisModelClient>(services =>
            new OpenAIResponsesModelClient(
                services.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("OpenAIProcessing"),
                openAIOptions,
                services.GetRequiredService<ILogger<OpenAIResponsesModelClient>>()));
    }

    builder.Services.AddSingleton<IContentAnalysisProvider>(services =>
        new OpenAIContentAnalysisProvider(
            services.GetRequiredService<IAnalysisModelClient>(),
            openAIOptions,
            services.GetRequiredService<AudioChoiceDataPaths>(),
            services.GetRequiredService<ILogger<OpenAIContentAnalysisProvider>>()));

    builder.Services.AddSingleton<IScanPipeline, ScanPipeline>();
    // The same provider instance under its character-offset contract, so a text scan and an
    // audio scan share the prompt, the taxonomy and the confidence floor by construction.
    builder.Services.AddSingleton<ITextContentAnalysisProvider>(services =>
        (OpenAIContentAnalysisProvider)services.GetRequiredService<IContentAnalysisProvider>());
    builder.Services.AddSingleton<TextScanPipeline>();
    builder.Services.AddHostedService<ScanWorker>();
}

// Premium narration synthesis. Registered independently of the scan worker: a server can read a
// book's text for filtering without also being the one that speaks it, and vice versa.
if (narrationOptions.TextScanEnabled || narrationOptions.SynthesisEnabled)
{
    builder.Services.AddSingleton<IAmazonPolly>(_ => new AmazonPollyClient());
    builder.Services.AddSingleton<PollySynthesisProvider>();
    // Polly is both the primary and the fallback for now, because it is the only provider built.
    // The router still runs: it enforces the billing-coverage gate and records which provider
    // spoke each chapter, so introducing a real primary later changes configuration rather than
    // control flow.
    builder.Services.AddSingleton<ISynthesisProvider>(services =>
        services.GetRequiredService<PollySynthesisProvider>());
    builder.Services.AddSingleton(services => new SynthesisRouter(
        primary: services.GetRequiredService<PollySynthesisProvider>(),
        fallback: services.GetRequiredService<PollySynthesisProvider>(),
        options: narrationOptions,
        logger: services.GetRequiredService<ILogger<SynthesisRouter>>(),
        // A measured cold-start delay supersedes the configured one.
        measurements: services.GetRequiredService<INarrationMeasurementStore>()));
}

var app = builder.Build();
app.Logger.LogInformation(
    "Transcription provider {Provider}; faster-whisper endpoint {Endpoint}; chunk timeout {TimeoutSeconds}s; lane {Lane}",
    openAIOptions.TranscriptionProvider,
    openAIOptions.FasterWhisperEndpoint,
    openAIOptions.FasterWhisperTimeoutSeconds,
    openAIOptions.ProcessingLane);
app.Logger.LogInformation(
    "Analysis provider {AnalysisProvider}{Region}; first pass {AnalysisModel}; " +
    "scene verification {VerificationModel}; escalation {EscalationModel}; " +
    "violence verification {ViolenceModel}; scanner {ScannerVersion}",
    openAIOptions.AnalysisProvider,
    string.IsNullOrWhiteSpace(openAIOptions.BedrockRegion)
        ? string.Empty
        : $" in {openAIOptions.BedrockRegion}",
    openAIOptions.AnalysisModel,
    openAIOptions.SceneVerificationModel,
    openAIOptions.SceneEscalationModel,
    openAIOptions.EffectiveViolenceVerificationModel,
    openAIOptions.ScannerVersion);

// A request may still name the GPU lane explicitly, which beta clients do. Everything else
// takes the configured default, which is now that same lane: the Azure worker transcribes
// through OpenAI and, with the paid-test ceiling its deployment still carries, cannot finish an
// audiobook anyway. Nothing in scanning reaches OpenAI once this is the default.
string ScanLane(HttpContext context) =>
    string.Equals(
        context.Request.Headers["X-AudioChoice-Scan-Channel"].ToString(),
        "ios-beta",
        StringComparison.OrdinalIgnoreCase)
        ? ScanProcessingLanes.IOSBetaLambda
        : openAIOptions.DefaultProcessingLane;

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

    // Off by default. Nothing requires a verified address -- sign-in does not check it -- so the
    // email asked something of every new listener that changed nothing for them.
    if (transactionalEmailOptions.VerificationEnabled)
    {
        // The account exists by this point, so a failed verification email must not fail the request.
        // Resend rejects a send for reasons that have nothing to do with the caller -- an unverified
        // sending domain, an expired key, a recipient domain it refuses -- and EnsureSuccessStatusCode
        // turns each of those into a 500. That cost every new listener their sign-up: the account was
        // created and then reported as a failure, so trying again told them the email was taken.
        //
        // Verification is a side effect of registering, not a condition of it. An unverified account
        // can still sign in, and another email can be requested later.
        try
        {
            await emailSender.SendEmailVerification(
                registration.Verification.Email,
                BuildActionURL(
                    transactionalEmailOptions.ActionBaseURL,
                    "verify-email",
                    registration.Verification.Token),
                cancellationToken);
        }
        catch (Exception error)
        {
            app.Logger.LogError(
                error,
                "Registration succeeded but the verification email could not be sent.");
        }
    }
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
        // Deliberately not swallowed, unlike the verification email. Here the email *is* the
        // operation: accepting the request and sending nothing leaves someone locked out watching an
        // inbox, with no way to tell anything went wrong -- the exact failure this feature exists to
        // remove. A failure they can see is better than silence.
        //
        // This discloses nothing either way: a send fault fails identically whether or not the
        // address has an account, and an unknown address still returns 202 without sending.
        try
        {
            await emailSender.SendPasswordReset(
                reset.Email,
                BuildActionURL(
                    transactionalEmailOptions.ActionBaseURL,
                    "reset-password",
                    reset.Token),
                reset.Token,
                cancellationToken);
        }
        catch (Exception error)
        {
            app.Logger.LogError(error, "A password reset code could not be emailed.");
            return Results.Problem(
                title: "The reset email could not be sent.",
                detail: "AudioChoice could not send your reset code. Please try again shortly.",
                statusCode: StatusCodes.Status502BadGateway);
        }
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

// Founders are the beta testers, given full access permanently at no charge. Kept as its own
// endpoint rather than a generic grant so the plan, the source and the absence of an expiry cannot be
// typed wrongly: an expiry entered by mistake would produce access that quietly lapses months later.
// Public and unauthenticated: it is help text, it contains nothing about anyone, and the screen that
// shows it has to work before a listener can sign in as much as after. Cached so a version that has
// not changed costs nothing to ask for.
app.MapGet("/v1/faq", () => Results.Ok(FaqContent.Current));

app.MapPost("/v1/admin/founders", (
    FounderGrantRequest request,
    HttpContext context,
    IAccountStore accounts,
    IEntitlementStore entitlements) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    var userID = accounts.FindUserIDByEmail(request.Email);
    if (userID is null)
    {
        // Named plainly. This is an operator tool, not a public endpoint, so there is nothing to
        // disclose by saying the address is unknown -- and saying otherwise would leave someone
        // believing they had granted access they had not.
        return Results.NotFound(new { error = "No account was found for that email address." });
    }
    var granted = entitlements.Grant(userID.Value, new EntitlementGrantRequest(
        Plan: AccountPlans.Founder,
        Source: AccountPlans.Founder,
        // No expiry. The store treats null as never expiring and prefers it over any dated grant,
        // so a founder who later subscribes by accident is still read as a founder.
        ExpiresAt: null,
        ExternalReference: null));
    return Results.Ok(granted);
});

app.MapPost("/v1/companion/transfers", async (
    CompanionTransferCreateRequest request,
    HttpContext context,
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
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
    if (extension is not ".m4b" and not ".m4a" and not ".mp3")
        return Results.BadRequest(new { error = "Companion transfers accept M4B, M4A, and MP3 files only." });
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
    ICompanionTransferStore transfers,
    ICompanionTransferStorage storage,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
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

    var job = catalog.CreateReanalysisJob(
        request.OwnerUserID, request.Fingerprint, ScanLane(context));
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

// Lists every known edition, including those with no timing data. /v1/admin/transcripts
// only reports editions that already have one, so a missing transcript is invisible
// there -- which is exactly the case that needs finding.
// Reads an edition's stored filter result without causing any work.
//
// Every other route that returns a result can also start one. /v1/scans/requests, given an
// edition that has a transcript but no result, queues a reanalysis and charges for it -- and it
// does not check that the caller owns the edition, so any signed-in account can trigger that
// for any edition whose fingerprint it can compute, which is any account holding the same file.
// That is fine for a listener opening a book, whose intent is to get filters. It is not fine
// for reading what a scanner produced, which is what comparing two scanners requires: a
// nineteen-edition survey would have quietly started paid jobs.
//
// So this exists to be the harmless one. It looks up and returns, and can do nothing else.
// Editions that are the same recording arriving as separate entries.
//
// The Scanned Books list grew a second row whenever a listener imported a converted or re-tagged
// copy of a book already held: identity was byte-exact, so the same audio was scanned twice and
// listed twice with different filter counts. Chapter structure can now identify a recording, which
// means those pairs are findable -- this reports them so they can be looked at before anything is
// merged, because a wrong merge hands one book's filter timings to another.
//
// Read-only. It groups and explains, and changes nothing.
app.MapGet("/v1/admin/editions/duplicates", (
    HttpContext context,
    IScanCatalog catalog,
    IEditionSignatureStore signatures) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    var editions = catalog.ListFingerprints().ToArray();
    // Groups are built by union rather than by pairing, so three copies of one recording report as
    // one group of three instead of three separate pairs to reconcile by hand.
    var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
    var groups = new List<List<BookFingerprint>>();

    for (var i = 0; i < editions.Length; i += 1)
    {
        for (var j = i + 1; j < editions.Length; j += 1)
        {
            var left = editions[i];
            var right = editions[j];
            var leftSignature = signatures.Find(left);
            var rightSignature = signatures.Find(right);

            var sameStructure = EditionMatch.ChapterStructureIdentifies(leftSignature, rightSignature) &&
                EditionMatch.SameRuntime(left, right) &&
                EditionMatch.SameFileKind(left, right);
            var sameIdentifier =
                !string.IsNullOrWhiteSpace(leftSignature?.ProductIdentifier) &&
                !string.IsNullOrWhiteSpace(rightSignature?.ProductIdentifier) &&
                EditionMatch.SameRecording(left, right, leftSignature, rightSignature);
            if (!sameStructure && !sameIdentifier) continue;

            var leftKey = InMemoryScanCatalog.FingerprintKey(left);
            var rightKey = InMemoryScanCatalog.FingerprintKey(right);
            var leftGroup = groupOf.TryGetValue(leftKey, out var l) ? l : -1;
            var rightGroup = groupOf.TryGetValue(rightKey, out var r) ? r : -1;

            if (leftGroup >= 0 && rightGroup >= 0)
            {
                // Both already grouped, separately. Four copies of one recording can pair as A-B
                // and C-D before B-C is reached, and leaving those apart would report one book as
                // two groups to reconcile by hand -- the very thing this is meant to end.
                if (leftGroup == rightGroup) continue;
                foreach (var moved in groups[rightGroup])
                {
                    groups[leftGroup].Add(moved);
                    groupOf[InMemoryScanCatalog.FingerprintKey(moved)] = leftGroup;
                }
                groups[rightGroup].Clear();
            }
            else if (leftGroup >= 0)
            {
                groups[leftGroup].Add(right);
                groupOf[rightKey] = leftGroup;
            }
            else if (rightGroup >= 0)
            {
                groups[rightGroup].Add(left);
                groupOf[leftKey] = rightGroup;
            }
            else
            {
                groups.Add([left, right]);
                groupOf[leftKey] = groups.Count - 1;
                groupOf[rightKey] = groups.Count - 1;
            }
        }
    }

    return Results.Ok(groups.Where(group => group.Count > 1).Select(group => new
    {
        members = group.Select(item => new
        {
            item.Sha256,
            item.FileSize,
            item.Version,
            item.WorkTitle,
            item.Author,
            item.Duration,
            item.FileType,
            chapterMarks = signatures.Find(item)?.ChapterOffsetSeconds?.Count ?? 0,
            productIdentifier = signatures.Find(item)?.ProductIdentifier,
            // Which member to keep: the one already carrying a scan.
            hasResult = catalog.FindResult(item) is not null
        })
    }));
});

// Records that two file identities are the same recording.
//
// The resolver discovers this on its own from chapter structure or a retail identifier, and every
// alias it writes is a cache of that. This exists for the cases it cannot see: a copy whose tags
// carry no chapter marks, or two entries an operator has listened to and knows are one book.
//
// Linking is the whole operation. Nothing is deleted, because a link is reversible by ignoring it
// and a deletion is not, and because both file identities remain real files on real devices.
app.MapPost("/v1/admin/editions/alias", (
    AdminEditionAliasRequest request,
    HttpContext context,
    IEditionAliasStore aliases,
    IScanCatalog catalog) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    if (InMemoryScanCatalog.FingerprintKey(request.First) ==
        InMemoryScanCatalog.FingerprintKey(request.Second))
    {
        return Results.BadRequest(new { error = "Both fingerprints identify the same file." });
    }

    // Refused when neither side has a scan, because an alias between two unscanned editions
    // records a claim that nothing can act on and nothing can check.
    if (catalog.FindResult(request.First) is null && catalog.FindResult(request.Second) is null)
    {
        return Results.BadRequest(new
        {
            error = "Neither edition has a filter result, so linking them would achieve nothing."
        });
    }

    aliases.Link(request.First, request.Second);
    app.Logger.LogInformation(
        "Linked editions {First} and {Second} by hand.",
        request.First.Sha256, request.Second.Sha256);
    return Results.NoContent();
});

app.MapGet("/v1/admin/editions/result", (
    HttpContext context,
    IScanCatalog catalog,
    string? sha256,
    int? fingerprintVersion,
    long? fileSize) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    // All three parts identify an edition. A partial one is refused rather than guessed at,
    // because a wrong fingerprint here would report another recording's result as this one's.
    if (string.IsNullOrWhiteSpace(sha256) || fingerprintVersion is null || fileSize is null)
    {
        return Results.BadRequest(new
        {
            error = "sha256, fingerprintVersion and fileSize are all required to identify an edition."
        });
    }

    var result = catalog.FindResult(new BookFingerprint(
        fingerprintVersion.Value, sha256, fileSize.Value,
        null, string.Empty, null, null, null, null, null, null, null));
    return result is null ? Results.NotFound() : Results.Ok(result);
});

// Reads a saved transcript by exact fingerprint. Read-only, and exists for the same reason
// /v1/admin/editions/result does: deciding whether two file identities are the same
// recording needs to compare their actual transcripts, not just their metadata, and nothing
// else exposes what one contains outside the separately authenticated internal audit portal.
app.MapGet("/v1/admin/transcripts/content", async (
    HttpContext context,
    IPrivateTranscriptStore transcriptStore,
    string? sha256,
    int? fingerprintVersion,
    long? fileSize,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(sha256) || fingerprintVersion is null || fileSize is null)
    {
        return Results.BadRequest(new
        {
            error = "sha256, fingerprintVersion and fileSize are all required to identify an edition."
        });
    }

    var transcript = await transcriptStore.Load(new BookFingerprint(
        fingerprintVersion.Value, sha256, fileSize.Value,
        null, string.Empty, null, null, null, null, null, null, null), cancellationToken);
    return transcript is null ? Results.NotFound() : Results.Ok(transcript);
});

app.MapGet("/v1/admin/editions", async (
    HttpContext context,
    IScanCatalog catalog,
    IPrivateTranscriptStore transcriptStore,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    var editions = new List<AdminEditionInfo>();
    foreach (var fingerprint in catalog.ListFingerprints())
    {
        var transcript = await transcriptStore.Load(fingerprint, cancellationToken);
        var segmentCount = transcript?.Segments.Count ?? 0;
        editions.Add(new AdminEditionInfo(fingerprint, segmentCount > 0, segmentCount));
    }
    return Results.Ok(editions);
});

// What still names an edition, so a delete can be judged safe (or refused) before it is
// attempted rather than by reading a failed constraint's error text. Counts only -- this
// never returns which listener a library row belongs to, just whether one exists.
//
// Read-only, changes nothing. Every table it counts lacks an on delete cascade back to
// audiobook_editions, so a plain delete already fails loudly rather than orphaning a real
// listener's library row; this exists so that can be known in advance instead of by trying.
app.MapGet("/v1/admin/editions/references", (
    HttpContext context,
    IEditionReferenceStore references,
    string? sha256,
    int? fingerprintVersion,
    long? fileSize) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(sha256) || fingerprintVersion is null || fileSize is null)
    {
        return Results.BadRequest(new
        {
            error = "sha256, fingerprintVersion and fileSize are all required to identify an edition."
        });
    }

    var counts = references.CountReferences(new BookFingerprint(
        fingerprintVersion.Value, sha256, fileSize.Value,
        null, string.Empty, null, null, null, null, null, null, null));
    return counts is null ? Results.NotFound() : Results.Ok(counts);
});

// Moves every listener's library row off a duplicate edition and onto the one being kept,
// so the duplicate can later be retired without breaking anyone's library. Not a delete --
// nothing at the source edition is removed, including its own scan history, only library
// rows are moved. A row is left in place rather than merged when that listener already has
// one at the destination; see RepointLibraryBooks for why.
//
// Both fingerprints must resolve to a known edition, but this endpoint does not itself
// verify they are the same recording -- that judgment (transcript comparison, or a
// confirmed alias) is expected to have already been made before calling it, the same as
// /v1/admin/editions/result-copy.
app.MapPost("/v1/admin/editions/repoint", (
    AdminEditionRepointRequest request,
    HttpContext context,
    IEditionReferenceStore references,
    ILogger<Program> logger) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    if (InMemoryScanCatalog.FingerprintKey(request.Source) ==
        InMemoryScanCatalog.FingerprintKey(request.Destination))
    {
        return Results.BadRequest(new { error = "Both fingerprints identify the same file." });
    }

    var result = references.RepointLibraryBooks(request.Source, request.Destination);
    if (result is null)
    {
        return Results.BadRequest(new { error = "One or both fingerprints do not identify a known edition." });
    }

    logger.LogInformation(
        "Repointed {Repointed} library row(s) from {SourceTitle} ({SourceSha}) to " +
        "{DestinationTitle} ({DestinationSha}); {Skipped} left in place because that " +
        "listener already had a row at the destination.",
        result.Repointed, request.Source.WorkTitle, request.Source.Sha256[..12],
        request.Destination.WorkTitle, request.Destination.Sha256[..12], result.SkippedForExistingRow);
    return Results.Ok(result);
});

/// Copies a scan result from one edition to another, for the case FindResult's own alias
/// path cannot reach on its own: a fingerprint that already has a stale result of its own,
/// where an alias would never even be consulted, because a fingerprint's own result always
/// wins over any alias. Chapter structure and a retail identifier are how the resolver
/// verifies this automatically; this exists for a recording that has neither and was
/// instead confirmed by a listener or an operator comparing the transcripts by hand.
///
/// Refuses rather than trusts the caller's word: both fingerprints must already have a
/// saved transcript, and independently, at least three separately spaced excerpts of the
/// destination's own transcript must appear verbatim in the source's -- not merely a
/// similar runtime or title, which is exactly the evidence that was wrong for one of the
/// editions this endpoint was built to fix. A wrong copy hands one recording's filter
/// timings to a different one, which this product exists to never do.
app.MapPost("/v1/admin/editions/result-copy", async (
    AdminResultCopyRequest request,
    HttpContext context,
    IScanCatalog catalog,
    IPrivateTranscriptStore transcriptStore,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    if (InMemoryScanCatalog.FingerprintKey(request.Source) ==
        InMemoryScanCatalog.FingerprintKey(request.Destination))
    {
        return Results.BadRequest(new { error = "Both fingerprints identify the same file." });
    }

    var sourceResult = catalog.FindResult(request.Source);
    if (sourceResult is null)
    {
        return Results.BadRequest(new { error = "The source edition has no scan result to copy." });
    }
    // Each ScanEvent carries the Id it was already stored under at the source edition. The
    // events table's primary key is that Id alone, not (Id, edition), so writing the source's
    // own rows again under a different edition collides on the row that already exists rather
    // than creating a second one. Regenerated here, once, at the one call site that hands
    // SaveResult events it did not just create -- every other caller already passes freshly
    // generated ids because it is saving a scan that has never been stored before.
    var copiedResult = sourceResult with
    {
        Events = sourceResult.Events.Select(value => value with { Id = Guid.NewGuid() }).ToArray()
    };

    var sourceTranscript = await transcriptStore.Load(request.Source, cancellationToken);
    var destinationTranscript = await transcriptStore.Load(request.Destination, cancellationToken);
    if (sourceTranscript is null || destinationTranscript is null)
    {
        return Results.BadRequest(new
        {
            error = "Both editions need a saved transcript before their content can be compared."
        });
    }

    var mismatch = TranscriptComparison.FindMismatch(sourceTranscript, destinationTranscript);
    if (mismatch is not null)
    {
        return Results.BadRequest(new
        {
            error = "The two transcripts do not verbatim match; refusing to copy a result " +
                "between what the transcripts show are two different recordings.",
            detail = mismatch
        });
    }

    catalog.SaveResult(request.Destination, copiedResult);
    logger.LogInformation(
        "Copied the scan result from {SourceTitle} ({SourceSha}) to {DestinationTitle} " +
        "({DestinationSha}) after verifying their transcripts match verbatim.",
        request.Source.WorkTitle, request.Source.Sha256[..12],
        request.Destination.WorkTitle, request.Destination.Sha256[..12]);
    return Results.NoContent();
});

/// Accepts timing data produced outside the scan pipeline, keyed to an existing
/// edition. This exists because transcription needs audio the server deliberately
/// deletes after scanning, so a transcript lost to a storage fault cannot be
/// regenerated server-side. Saving through the store keeps the key derivation and
/// payload schema identical to the pipeline's own writes.
app.MapPost("/v1/admin/transcripts", async (
    AdminTranscriptIngestRequest request,
    HttpContext context,
    IPrivateTranscriptStore transcriptStore,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    var segments = request.Transcript.Segments;
    if (segments.Count is 0 or > 200_000)
        return Results.BadRequest(new { error = "A transcript must have between 1 and 200000 segments." });
    if (segments.Any(segment => segment.EndTime < segment.StartTime || segment.StartTime < 0))
        return Results.BadRequest(new { error = "Every segment needs a non-negative start at or before its end." });
    // Alignment walks segments in order, so reject rather than silently mis-time.
    if (segments.Zip(segments.Skip(1)).Any(pair => pair.Second.StartTime < pair.First.StartTime))
        return Results.BadRequest(new { error = "Segments must be ordered by start time." });

    await transcriptStore.Save(request.Fingerprint, request.Transcript, cancellationToken);
    logger.LogInformation(
        "Ingested {SegmentCount} externally produced transcript segments for {Title}, covering {Seconds:F0}s.",
        segments.Count,
        request.Fingerprint.WorkTitle,
        segments[^1].EndTime);
    return Results.Ok(new { segmentCount = segments.Count, coverageSeconds = segments[^1].EndTime });
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
    // Refused here rather than becoming a database error, since the column is bounded.
    if (request.Description?.Trim().Length > 4000)
    {
        return Results.BadRequest(new { error = "A synopsis is limited to 4000 characters." });
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

// Every scanned edition, including the ones listeners cannot see. Managing the catalogue
// needs this: an entry that was hidden, or that is being withheld because it names no book,
// does not appear in the listing above, which is the one place you would look for it.
app.MapGet("/v1/admin/explore/all", (
    HttpContext context,
    IScanCatalog catalog) =>
{
    return IsConfiguredApiToken(context, app.Configuration)
        ? Results.Ok(catalog.ListExploreCatalog())
        : Results.Unauthorized();
});

// Fills in synopses for catalogue entries that have none.
//
// A book's own description tags are the first source and cost nothing, but plenty of files
// carry none. This looks those up so the catalogue reads like a store front rather than a
// list of titles. Deliberately a request an administrator makes rather than something that
// runs during a scan: it calls an outside service, and a scan must not depend on one.
app.MapPost("/v1/admin/explore/descriptions/backfill", async (
    HttpContext context,
    IScanCatalog catalog,
    ISynopsisProvider synopses,
    CancellationToken cancellationToken) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();
    var missing = catalog.ListExploreCatalog()
        .Where(entry => entry.IsPublishable && string.IsNullOrWhiteSpace(entry.Book.Description))
        .ToArray();
    var filled = new List<string>();
    var unresolved = new List<string>();
    foreach (var entry in missing)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = catalog.ListFingerprints().FirstOrDefault(value =>
            value.Sha256.StartsWith(entry.Book.CatalogID, StringComparison.OrdinalIgnoreCase));
        if (fingerprint is null) continue;
        var synopsis = await synopses.Find(
            fingerprint, entry.Book.ProductIdentifier, cancellationToken);
        if (synopsis is not null && catalog.SaveEditionDescription(fingerprint, synopsis))
        {
            filled.Add(entry.Book.Title);
        }
        else
        {
            // Reported rather than silently skipped, because the remedy is to write one by
            // hand and that needs knowing which books are still waiting.
            unresolved.Add(entry.Book.Title);
        }
    }
    return Results.Ok(new
    {
        considered = missing.Length,
        filled = filled.Count,
        filledTitles = filled,
        stillMissing = unresolved
    });
});

// Undoes a hide. Without this, hiding the wrong edition could only be corrected by editing
// the database by hand.
app.MapPost("/v1/admin/explore/{catalogID}/restore", (
    string catalogID,
    HttpContext context,
    IScanCatalog catalog) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration))
    {
        return Results.Unauthorized();
    }
    return catalog.RestoreExploreBook(catalogID)
        ? Results.NoContent()
        : Results.NotFound(new { error = "No hidden Explore audiobook has that catalog ID." });
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
    IUserLibraryStore library,
    IScanCatalog catalog,
    IEditionAliasStore editionAliases,
    IEditionSignatureStore editionSignatures) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    // The synopsis the file carries, which is what Explore shows under "About this
    // audiobook". Stored against the edition because it describes the recording, so one
    // listener's well-tagged copy gives every other owner of that edition a real
    // description. Rejected values are ignored rather than failing the import: a book
    // still belongs in the library when its description tag is unusable.
    if (!string.IsNullOrWhiteSpace(request.Description))
    {
        catalog.SaveEditionDescription(request.Fingerprint, request.Description);
        if (request.SourceFingerprint is not null)
        {
            catalog.SaveEditionDescription(request.SourceFingerprint, request.Description);
        }
    }

    // Record the divergence while the client still knows about it. Without this the
    // link can only be rediscovered by comparing metadata, which needs a runtime and
    // a title to agree.
    if (request.SourceFingerprint is not null)
    {
        editionAliases.Link(request.Fingerprint, request.SourceFingerprint);
    }

    // The signature describes the recording, so it holds for both fingerprints.
    if (request.Signature is not null)
    {
        editionSignatures.Record(request.Fingerprint, request.Signature);
        if (request.SourceFingerprint is not null)
        {
            editionSignatures.Record(request.SourceFingerprint, request.Signature);
        }
    }

    return Results.Ok(library.Upsert(user.ID, request));
});

app.MapPut("/v1/library/{bookID:guid}/details", (
    Guid bookID,
    LibraryBookDetailsRequest request,
    HttpContext context,
    IUserLibraryStore library) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();

    // The stored columns are varchar(300), so oversized input has to be refused here
    // rather than becoming a database error.
    var title = request.Title?.Trim();
    if (string.IsNullOrWhiteSpace(title))
        return Results.BadRequest(new { error = "A title is required." });
    if (title.Length > 300 ||
        request.Author?.Trim().Length > 300 ||
        request.Narrator?.Trim().Length > 300)
    {
        return Results.BadRequest(new { error = "Titles, authors and narrators are limited to 300 characters." });
    }

    var book = library.UpdateDetails(user.ID, bookID, new LibraryBookDetailsRequest(
        title, request.Author?.Trim(), request.Narrator?.Trim()));
    return book is null ? Results.NotFound() : Results.Ok(book);
});

app.MapPost("/v1/editions/signatures", (
    EditionSignatureReportRequest request,
    HttpContext context,
    IUserLibraryStore library,
    IEditionAliasStore editionAliases,
    IEditionSignatureStore editionSignatures) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();

    // Restricted to editions the caller actually holds. A signature can decide which
    // filter results another listener is served, so this must not be an open write
    // against arbitrary editions.
    var owned = library.List(user.ID).Any(book =>
        InMemoryScanCatalog.FingerprintKey(book.Fingerprint) ==
        InMemoryScanCatalog.FingerprintKey(request.Fingerprint));
    if (!owned) return Results.NotFound(new { error = "That audiobook is not in your library." });

    editionSignatures.Record(request.Fingerprint, request.Signature);
    if (request.SourceFingerprint is not null)
    {
        editionAliases.Link(request.Fingerprint, request.SourceFingerprint);
        editionSignatures.Record(request.SourceFingerprint, request.Signature);
    }
    return Results.NoContent();
});

app.MapPost("/v1/editions/descriptions", (
    EditionDescriptionReportRequest request,
    HttpContext context,
    IUserLibraryStore library,
    IScanCatalog catalog) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    // Restricted to editions the caller actually holds, matching signature reporting. This
    // text is shown to other listeners, so it must not be an open write against arbitrary
    // editions.
    var owned = library.List(user.ID).Any(book =>
        InMemoryScanCatalog.FingerprintKey(book.Fingerprint) ==
        InMemoryScanCatalog.FingerprintKey(request.Fingerprint));
    if (!owned) return Results.NotFound(new { error = "That audiobook is not in your library." });
    // A rejected description is not an error the client can act on: the file simply does
    // not carry a usable one, and the first report for an edition has already won.
    catalog.SaveEditionDescription(request.Fingerprint, request.Description);
    return Results.NoContent();
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
    IEditionResolver editions,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (request.EpubText.Length is 0 or > 8_000_000)
        return Results.BadRequest(new { error = "The reading edition is empty or too large to sync." });

    var book = library.List(user.ID).FirstOrDefault(value => value.ID == request.LibraryBookID);
    if (book is null) return Results.NotFound(new { error = "That audiobook is not in your library." });

    // Resolved rather than loaded directly: a converted or re-tagged file leaves the
    // transcript under the fingerprint that was uploaded, not the one on this row.
    var transcript = await editions.LoadTranscript(book.Fingerprint, cancellationToken);
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

// Finds filterable content in a book that has no audiobook, by reading its text.
//
// The text is a parameter and never becomes a file, a column or a log line. It is bound to a
// local, sliced into passages that are views onto that local, and dropped when the request
// ends. The response carries character offsets, which is all the client needs, because the
// client is the only party that has the book.
//
// Deliberately synchronous, unlike an audio scan. There is no upload, no transcription and no
// GPU queue, so there is nothing for a job record to track: the work is one classification
// pass whose whole cost is the model calls. Making it a job would mean persisting the text
// between the request that supplied it and the worker that read it, which is the one thing
// this endpoint must not do.
app.MapPost("/v1/narration/text-scans", async (
    NarrationTextScanRequest request,
    HttpContext context,
    NarrationOptions narration,
    INarrationTextScanStore scans,
    IServiceProvider services,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!narration.TextScanEnabled) return Results.NotFound();

    // Resolved through the provider rather than declared as a nullable handler parameter.
    // The classifier is only registered where the processing worker is configured, and a
    // minimal-API parameter of an unregistered type is not treated as an absent service --
    // it is treated as a second body parameter, which fails while routes are being built.
    // That would turn "this environment has no worker" into "this environment will not
    // start", for environments that have nothing to do with narration.
    var pipeline = services.GetService<TextScanPipeline>();

    // A 503 says "not here, try later" rather than reporting the listener's book as the
    // problem.
    if (pipeline is null)
    {
        return Results.Problem(
            "Text scanning is not available on this server.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (request.Fingerprint is null || string.IsNullOrWhiteSpace(request.Fingerprint.Sha256))
        return Results.BadRequest(new { error = "A book fingerprint is required." });

    // The same bound the reader-alignment endpoint applies to an EPUB's text, for the same
    // reason: past this size the request is a mistake rather than a book.
    var bookText = request.BookText ?? string.Empty;
    if (bookText.Length is 0 or > TextScanPipeline.MaximumBookTextCharacters)
        return Results.BadRequest(new { error = "The book text is empty or too large to scan." });

    // Scanned once per book, then handed to everyone who imports it. Two listeners with the
    // same EPUB produce the same fingerprint, so the second pays nothing.
    var existing = scans.Load(request.Fingerprint, pipeline.ScannerVersion);
    if (existing is not null && existing.BookTextCharacters == bookText.Length)
    {
        logger.LogInformation(
            "Reused a stored text scan with {EventCount} events for a {CharacterCount}-character book.",
            existing.Events.Count,
            existing.BookTextCharacters);
        return Results.Ok(existing.ToResponse());
    }

    using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, narration.TextScanTimeoutSeconds)));
    try
    {
        var scan = await pipeline.Scan(
            request.Fingerprint, bookText, request.Language, null, budget.Token);
        scans.Save(request.Fingerprint, scan);
        logger.LogInformation(
            "Text scan produced {EventCount} events for a {CharacterCount}-character book.",
            scan.Events.Count,
            scan.BookTextCharacters);
        return Results.Ok(scan.ToResponse());
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        // The budget expired rather than the listener leaving. 504 rather than 500 because
        // retrying is reasonable and the book is not at fault.
        logger.LogWarning(
            "Text scan exceeded its {TimeoutSeconds}-second budget for a {CharacterCount}-character book.",
            narration.TextScanTimeoutSeconds,
            bookText.Length);
        return Results.Problem(
            "Scanning this book took too long. Please try again.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
});

// The voices a listener may choose, with the agreement premium synthesis requires.
//
// Samples are fixed pre-rendered assets rather than made on demand, so browsing voices costs
// nothing and sends no text anywhere.
app.MapGet("/v1/narration/voices", async (
    HttpContext context,
    NarrationOptions narration,
    INarrationAgreementStore agreements,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!narration.SynthesisEnabled) return Results.NotFound();

    var router = services.GetService<SynthesisRouter>();
    if (router is null)
    {
        return Results.Problem(
            "Narration synthesis is not available on this server.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var voices = await router.ProviderInEffect.Voices(cancellationToken);
    return Results.Ok(new NarrationVoicesResponse(
        voices.Select(voice => new NarrationVoiceDescriptor(
            voice.VoiceID, voice.DisplayName, voice.Language, voice.Provider, voice.SampleUrl))
            .ToArray(),
        agreements.Current.Version,
        agreements.Current.Text));
});

// Records that a listener accepted the premium synthesis agreement.
//
// Idempotent on the version, so the client's offline path can re-send an acceptance it recorded
// locally without creating a second record or moving the first one's timestamp.
app.MapPost("/v1/narration/acknowledgements", (
    NarrationAcknowledgementRequest request,
    HttpContext context,
    NarrationOptions narration,
    INarrationAgreementStore agreements) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!narration.SynthesisEnabled) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.AgreementVersion))
        return Results.BadRequest(new { error = "An agreement version is required." });

    return Results.Ok(agreements.Accept(
        user.ID, request.AgreementVersion.Trim(), request.AgreementText));
});

// Asks for one chapter to be spoken.
//
// A job rather than a synchronous response, because a chapter can hold twenty thousand characters
// and take minutes: the client polls instead of holding a request open past every sensible
// timeout. The units arrive with filtered characters already removed, so nothing the listener
// asked to have filtered is ever sent.
app.MapPost("/v1/narration/chapters", (
    NarrationChapterRequest request,
    HttpContext context,
    NarrationOptions narration,
    IEntitlementStore entitlements,
    INarrationAgreementStore agreements,
    NarrationChapterJobs jobs,
    INarrationRenderStore renders,
    IServiceProvider services,
    ILogger<Program> logger) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!narration.SynthesisEnabled) return Results.NotFound();

    var router = services.GetService<SynthesisRouter>();
    if (router is null)
    {
        return Results.Problem(
            "Narration synthesis is not available on this server.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Entitlement is read from the server's own record, never from anything the client says. A
    // receipt on a device says a payment happened, not that it cleared, was not refunded, and
    // belongs to the account now signed in.
    var access = entitlements.Access(user.ID);
    var entitled = access.IsActive &&
        (access.ExpiresAt is null || access.ExpiresAt > DateTimeOffset.UtcNow);
    if (!entitled)
    {
        return Results.Problem(
            "The premium voice requires an active subscription.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    // A stale agreement is refused rather than silently accepted: the listener agreed to a
    // different arrangement from the one now in force.
    if (!agreements.HasAcceptedCurrent(user.ID))
    {
        return Results.Problem(
            "The premium voice agreement has not been accepted, or its wording has changed.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (request.Fingerprint is null || string.IsNullOrWhiteSpace(request.Fingerprint.Sha256))
        return Results.BadRequest(new { error = "A book fingerprint is required." });
    if (request.Units.Count == 0)
        return Results.BadRequest(new { error = "A chapter needs at least one passage to speak." });
    if (request.CharacterCount > NarrationSynthesisLimits.MaximumChapterCharacters)
        return Results.BadRequest(new { error = "That chapter is too long to synthesize." });

    var job = jobs.Create(
        user.ID, request.Fingerprint.Sha256, request.ChapterIndex, request.VoiceID);
    if (job is null)
    {
        // A whole book queued at once would be a listener spending a great deal in one gesture.
        return Results.Problem(
            "Two chapters are already being made. Try again when one finishes.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    // Started detached on purpose: the response is a 202 and the client polls. The request's
    // cancellation token must not cancel the work, or closing the app would abandon a chapter the
    // listener has already been charged for.
    _ = Task.Run(async () =>
    {
        jobs.Update(job.JobID, existing => existing with { Status = NarrationJobStatus.Running });
        try
        {
            var input = new ChapterSynthesisInput(
                job.JobID,
                request.ChapterIndex,
                request.VoiceID,
                request.Language,
                request.Units
                    .Select(unit => new SpokenUnit(unit.StartCharacter, unit.EndCharacter, unit.Text))
                    .ToArray());
            var routed = await router.Synthesize(input, CancellationToken.None);
            jobs.Update(job.JobID, existing => existing with
            {
                Status = NarrationJobStatus.Completed,
                Chapter = routed.Chapter,
                Route = routed.Route,
            });
            // Recorded per chapter, which is what makes a book spanning a subscription lapse
            // explicable: it legitimately holds two voices, and without this its audio would be a
            // mystery to whoever is asked about it.
            renders.Record(new NarrationChapterRender(
                ID: Guid.NewGuid(),
                UserID: user.ID,
                Fingerprint: request.Fingerprint,
                ChapterIndex: request.ChapterIndex,
                VoiceID: routed.Chapter.VoiceID,
                Provider: routed.Chapter.Provider,
                ModelVersion: routed.Chapter.ModelVersion,
                DurationSeconds: routed.Chapter.DurationSeconds,
                // Names where the audio went, not where it is: it was returned to the device and
                // is kept nowhere on the server.
                ObjectPath: "device",
                CreatedAt: DateTimeOffset.UtcNow));
            logger.LogInformation(
                "Narration chapter {ChapterIndex} completed by {Provider} via {Route} in " +
                "{DurationSeconds:F1} seconds of audio.",
                request.ChapterIndex, routed.Chapter.Provider, routed.Route,
                routed.Chapter.DurationSeconds);
        }
        catch (Exception error)
        {
            jobs.Update(job.JobID, existing => existing with
            {
                Status = NarrationJobStatus.Failed,
                Error = "That chapter could not be made into audio. Please try again.",
            });
            logger.LogWarning(
                error, "Narration chapter {ChapterIndex} failed.", request.ChapterIndex);
        }
    });

    return Results.Accepted(
        $"/v1/narration/chapters/{job.JobID}",
        new NarrationChapterAccepted(job.JobID, job.Status.ToString().ToLowerInvariant()));
});

// Polls one chapter job, and hands back the audio once there is any.
//
// The audio travels in this response rather than through a signed URL to a storage container.
// Chapter audio is derived closely enough from a listener's book that keeping it in cloud storage
// would be the same disclosure the text handling is careful to avoid -- and unlike the text it
// would sit there indefinitely, governed by a retention policy rather than by the absence of
// anywhere to put it.
app.MapGet("/v1/narration/chapters/{jobID:guid}", (
    Guid jobID,
    HttpContext context,
    NarrationOptions narration,
    NarrationChapterJobs jobs) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!narration.SynthesisEnabled) return Results.NotFound();

    // Scoped to the account that created it: a job holds a chapter of somebody's book.
    var job = jobs.Find(jobID, user.ID);
    if (job is null) return Results.NotFound(new { error = "That narration job was not found." });

    if (job.Status != NarrationJobStatus.Completed || job.Chapter is null)
    {
        return Results.Ok(new NarrationChapterStatus(
            job.JobID,
            job.ChapterIndex,
            job.Status.ToString().ToLowerInvariant(),
            Error: job.Error));
    }

    var chapter = job.Chapter;
    var response = new NarrationChapterStatus(
        JobID: job.JobID,
        ChapterIndex: job.ChapterIndex,
        Status: "completed",
        Provider: chapter.Provider,
        ModelVersion: chapter.ModelVersion,
        VoiceID: chapter.VoiceID,
        DurationSeconds: chapter.DurationSeconds,
        Timings: chapter.Timings
            .Select(timing => new NarrationUnitTiming(
                timing.StartCharacter, timing.EndCharacter,
                timing.StartSeconds, timing.EndSeconds))
            .ToArray(),
        AudioBase64: Convert.ToBase64String(chapter.Audio));

    // Dropped from memory now it has been handed over, so a collected chapter is not held twice.
    // The record survives so a repeated poll still reports the outcome rather than a 404.
    jobs.Collected(job.JobID);
    return Results.Ok(response);
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

// Listeners telling us filtering was wrong. The only route by which a missed passage or an
// over-zealous skip becomes something anyone can act on, so it accepts generously: a
// malformed report is dropped with 400 rather than retried, because the listener has moved
// on and a queued retry would report the wrong moment.
app.MapPost("/v1/filter-reports", async (
    FilterReportRequest request,
    HttpContext context,
    IFilterReportStore reports,
    ITransactionalEmailSender emailSender,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    var report = reports.Record(user.ID, request);
    if (report is null)
    {
        return Results.BadRequest(new { error = "A fingerprint and a playback position are required." });
    }

    // Recorded first, then announced. A listener's report is the thing worth keeping, and until now
    // nothing told anyone it had arrived: no email, and neither portal reads the table, so reports
    // accumulated unseen.
    //
    // A failed send must not lose the report or report failure to the listener. They did their part,
    // the row exists, and triage can still find it through the admin endpoint.
    try
    {
        await emailSender.SendFilterReportAlert(report, cancellationToken);
    }
    catch (Exception error)
    {
        app.Logger.LogError(
            error,
            "A filter report was recorded but the alert could not be emailed. Report {ReportID}.",
            report.ID);
    }

    return Results.Created($"/v1/filter-reports/{report.ID}", report);
});

app.MapGet("/v1/admin/filter-reports", (
    HttpContext context,
    IFilterReportStore reports,
    int? limit,
    string? sha256,
    int? fingerprintVersion,
    long? fileSize) =>
{
    if (!IsConfiguredApiToken(context, app.Configuration)) return Results.Unauthorized();

    // All three parts identify an edition; a sha256 on its own is not enough to build a
    // fingerprint, so a partial filter is refused rather than silently ignored.
    BookFingerprint? fingerprint = null;
    if (!string.IsNullOrWhiteSpace(sha256) || fingerprintVersion is not null || fileSize is not null)
    {
        if (string.IsNullOrWhiteSpace(sha256) || fingerprintVersion is null || fileSize is null)
        {
            return Results.BadRequest(new
            {
                error = "Filtering by edition needs sha256, fingerprintVersion and fileSize together."
            });
        }
        fingerprint = new BookFingerprint(
            fingerprintVersion.Value, sha256!, fileSize.Value,
            null, "", null, null, null, null, null, null, null);
    }

    return Results.Ok(reports.List(limit ?? 200, fingerprint));
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
    IEditionResolver editions,
    CancellationToken cancellationToken) =>
{
    // Resolved rather than looked up directly, so a converted or re-tagged copy of an
    // already-scanned edition reuses its filters instead of paying to scan again.
    // FindResult only accepts proof, never metadata similarity.
    var result = editions.FindResult(request.Fingerprint);
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

// Re-run the current scanner against an already saved transcript. This is kept
// separate from /v1/scans/requests so opening a book never unexpectedly starts
// paid work, while an authenticated user can explicitly refresh filter results
// after a scanner upgrade without importing the audiobook again.
app.MapPost("/v1/scans/reanalysis", async (
    CloudScanRequest request,
    HttpContext context,
    IScanCatalog catalog,
    IScanJobQueue queue,
    IPrivateTranscriptStore transcriptStore,
    CancellationToken cancellationToken) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();

    var transcript = await transcriptStore.Load(request.Fingerprint, cancellationToken);
    if (transcript is null || transcript.IsComplete is false || transcript.Segments.Count == 0)
    {
        return Results.BadRequest(new
        {
            error = "No complete saved transcript exists for this audiobook."
        });
    }

    var job = catalog.CreateReanalysisJob(user.ID, request.Fingerprint, ScanLane(context));
    if (job is null)
    {
        return Results.BadRequest(new
        {
            error = "The saved transcript is not linked to this account."
        });
    }

    queue.TryQueue(job.ID);
    return Results.Json(
        new CloudScanResponse(job.Status, job.ID),
        statusCode: StatusCodes.Status202Accepted);
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
