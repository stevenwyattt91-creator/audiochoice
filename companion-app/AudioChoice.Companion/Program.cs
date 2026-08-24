using System.Net.Http.Headers;
using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Features;
using QRCoder;

const string ConversionAgreementText = "By continuing, I confirm that I legally acquired this audiobook and have the right to convert it for my personal use. I will not use AudioChoice to copy, share, distribute, sell, or process content I do not lawfully own or control.";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:47621");
// Audiobooks are selected locally by the Companion. Raise both ASP.NET request
// limits so the local acknowledgement and transfer workflows can handle normal
// audiobook-sized files without sending the source file to AudioChoice.
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 21_474_836_480L);
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = 21_474_836_480L);
builder.Services.AddSingleton<CompanionSettings>();
builder.Services.AddSingleton<CompanionTransferJobs>();
builder.Services.AddHttpClient("audiochoice", client => client.Timeout = TimeSpan.FromMinutes(10));

var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Companion request failed for {Path}", context.Request.Path);
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Companion could not process the selected file.",
            detail = exception.Message
        });
    }
});
app.Use(async (context, next) =>
{
    if (context.Request.Headers.Origin == "https://audiochoiceapp.com")
    {
        context.Response.Headers.AccessControlAllowOrigin = "https://audiochoiceapp.com";
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
    }
    if (context.Request.Method == HttpMethods.Options)
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // The Companion runs on a stable local URL. Never let a browser keep a
        // previous installer version of the page or its logo in its cache.
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", app = "AudioChoice Companion" }));

app.MapPost("/v1/quit", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.NoContent();
});

app.MapGet("/v1/settings", (CompanionSettings settings) => Results.Ok(settings.Public()));

// The desktop UI checks this before opening the file picker. The backend also
// enforces access before it creates a transfer, so the check is both clear to
// the user and safe if a local request is bypassed.
app.MapGet("/v1/access", async (IHttpClientFactory clients, CompanionSettings settings, CancellationToken cancellationToken) =>
{
    if (!settings.IsSignedIn) return Results.Unauthorized();
    using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.ApiBaseUrl}/v1/account/access");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
    using var response = await clients.CreateClient("audiochoice").SendAsync(request, cancellationToken);
    var content = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(content, "application/json", statusCode: (int)response.StatusCode);
});

app.MapPost("/v1/sign-in", async (SignInRequest request, IHttpClientFactory clients, CompanionSettings settings, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Enter your AudioChoice email and password." });

    using var response = await clients.CreateClient("audiochoice").PostAsJsonAsync(
        $"{settings.ApiBaseUrl}/v1/auth/login",
        new { email = request.Email.Trim(), password = request.Password },
        cancellationToken);
    if (!response.IsSuccessStatusCode)
        return Results.Unauthorized();

    var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
    if (string.IsNullOrWhiteSpace(login?.AccessToken))
        return Results.Problem("AudioChoice did not return a usable sign-in session.");

    settings.Save(login.AccessToken, login.User?.Email ?? request.Email.Trim(), login.User?.DisplayName ?? request.Email.Trim());
    return Results.Ok(settings.Public());
});

app.MapPost("/v1/google-sign-in", async (GoogleSignInRequest request, IHttpClientFactory clients, CompanionSettings settings, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.IdentityToken))
        return Results.BadRequest(new { error = "Google did not return a usable AudioChoice identity." });

    using var response = await clients.CreateClient("audiochoice").PostAsJsonAsync(
        $"{settings.ApiBaseUrl}/v1/auth/external",
        new { provider = "google", authorizationCode = string.Empty, identityToken = request.IdentityToken, displayName = (string?)null },
        cancellationToken);
    if (!response.IsSuccessStatusCode)
        return Results.Unauthorized();

    var login = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: cancellationToken);
    if (string.IsNullOrWhiteSpace(login?.AccessToken) || string.IsNullOrWhiteSpace(login.User?.Email))
        return Results.Problem("AudioChoice did not return a usable Google sign-in session.");

    settings.Save(login.AccessToken, login.User.Email, login.User.DisplayName ?? login.User.Email);
    return Results.Ok(settings.Public());
});

app.MapPost("/v1/sign-out", (CompanionSettings settings) =>
{
    settings.Clear();
    return Results.NoContent();
});

// AAX is acknowledged here so there is an auditable per-book record before a
// user continues through an external conversion workflow. The source AAX is
// never uploaded or transferred by the companion.
app.MapPost("/v1/aax-consents", async (HttpRequest request, IHttpClientFactory clients, CompanionSettings settings, CancellationToken cancellationToken) =>
{
    if (!settings.IsSignedIn) return Results.Unauthorized();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Choose an AAX file first." });

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("audiobook");
    if (file is null || file.Length == 0 || !file.FileName.EndsWith(".aax", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Choose an AAX file first." });
    if (!string.Equals(form["accepted"].ToString(), "true", StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Accept the ownership acknowledgment to continue." });

    await using var source = file.OpenReadStream();
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
    var consent = new
    {
        fingerprint = new
        {
            version = 1,
            sha256 = hash,
            fileSize = file.Length,
            duration = (double?)null,
            fileType = "aax",
            workTitle = (string?)null,
            author = (string?)null,
            seriesTitle = (string?)null,
            seriesNumber = (int?)null,
            editionType = (string?)null,
            partNumber = (int?)null,
            totalParts = (int?)null
        },
        sourceFileName = Path.GetFileName(file.FileName),
        agreementVersion = "2026-08-06",
        agreementText = ConversionAgreementText
    };
    using var record = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBaseUrl}/v1/conversion-consents")
    {
        Content = JsonContent.Create(consent)
    };
    record.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
    using var response = await clients.CreateClient("audiochoice").SendAsync(record, cancellationToken);
    if (!response.IsSuccessStatusCode)
        return Results.Problem(await ReadApiError(response), statusCode: (int)response.StatusCode);

    return Results.Ok(new { message = "Acknowledgment recorded. Convert the AAX externally, then select the resulting M4B to transfer." });
});

app.MapPost("/v1/transfers", async (HttpRequest request, CompanionSettings settings, CompanionTransferJobs jobs, CancellationToken cancellationToken) =>
{
    if (!settings.IsSignedIn) return Results.Unauthorized();
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "Choose an M4B, MP3, or AAX file to transfer." });

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("audiobook");
    var extension = file is null ? string.Empty : Path.GetExtension(file.FileName).ToLowerInvariant();
    if (file is null || file.Length == 0 || extension is not ".m4b" and not ".m4a" and not ".mp3")
        return Results.BadRequest(new { error = "AudioChoice Companion transfers M4B, M4A, and MP3 files. For AAX, record the acknowledgment and complete conversion first." });

    var stagedPath = Path.Combine(Path.GetTempPath(), "AudioChoice", "companion-transfers", $"{Guid.NewGuid():N}{extension}");
    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
    await using (var destination = File.Create(stagedPath))
    await using (var source = file.OpenReadStream())
        await source.CopyToAsync(destination, cancellationToken);

    var job = jobs.Start(stagedPath, Path.GetFileName(file.FileName), file.Length, extension == ".mp3" ? "audio/mpeg" : "audio/mp4", settings.ApiBaseUrl, settings.AccessToken!);
    return Results.Accepted($"/v1/transfers/{job.ID}", new { transferJobID = job.ID, message = "File received. Companion is preparing the secure phone transfer." });
});

app.MapGet("/v1/transfers/{jobID:guid}", (Guid jobID, CompanionTransferJobs jobs) =>
{
    var job = jobs.Find(jobID);
    return job is null ? Results.NotFound() : Results.Ok(job.Public());
});

// Generate the QR code in Companion instead of relying on a browser CDN. This
// keeps the final phone-transfer step available even when a web script fails
// to load or the browser blocks third-party assets.
app.MapGet("/v1/transfers/{jobID:guid}/qr", (Guid jobID, string? app, CompanionTransferJobs jobs) =>
{
    var job = jobs.Find(jobID);
    if (job?.ReceiverURL is not { Length: > 0 } receiverUrl)
        return Results.NotFound();

    // Main and Beta use their own registered schemes. This avoids Android's
    // chooser and guarantees the QR code opens the app the person selected.
    var scheme = string.Equals(app, "main", StringComparison.OrdinalIgnoreCase)
        ? "audiochoice"
        : "audiochoice-beta";
    // Keep the complete transfer path and one-time code intact. Replacing the
    // scheme as text is deliberate: UriBuilder can normalize custom mobile
    // schemes in ways that make a perfectly valid transfer QR unusable.
    var schemeSeparator = receiverUrl.IndexOf(':');
    if (schemeSeparator <= 0) return Results.Problem("The phone transfer link is invalid.");
    var qrPayload = scheme + receiverUrl[schemeSeparator..];
    using var generator = new QRCodeGenerator();
    using var qrData = generator.CreateQrCode(qrPayload, QRCodeGenerator.ECCLevel.M);
    var png = new PngByteQRCode(qrData).GetGraphic(8);
    return Results.File(png, "image/png");
});

var companionURL = "http://127.0.0.1:47621/?build=1.0.5";
app.Lifetime.ApplicationStarted.Register(() =>
{
    try { Process.Start(new ProcessStartInfo(companionURL) { UseShellExecute = true }); }
    catch { /* The companion remains available at its local address. */ }
});
app.Run();

static async Task<string> ReadApiError(HttpResponseMessage response)
{
    var value = await response.Content.ReadAsStringAsync();
    if (string.IsNullOrWhiteSpace(value)) return "AudioChoice could not complete that request.";
    try
    {
        var document = JsonDocument.Parse(value).RootElement;
        foreach (var property in new[] { "error", "detail", "title", "message" })
            if (document.TryGetProperty(property, out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? value;
        return value;
    }
    catch { return value; }
}

sealed record SignInRequest(string Email, string Password);
sealed record GoogleSignInRequest(string IdentityToken);
sealed record LoginResponse(string AccessToken, LoginUser? User);
sealed record LoginUser(string Email, string? DisplayName);
sealed record TransferResponse(Guid TransferID, Uri UploadURL, string Method, Dictionary<string, string>? Headers, string ReceiverURL, DateTimeOffset ExpiresAt);

sealed class CompanionTransferJobs(IHttpClientFactory clients, ILogger<CompanionTransferJobs> logger)
{
    private readonly ConcurrentDictionary<Guid, Job> jobs = new();

    public Job Start(string stagedPath, string fileName, long fileSize, string contentType, string apiBaseUrl, string accessToken)
    {
        var job = new Job(Guid.NewGuid(), stagedPath, fileName, fileSize, contentType);
        jobs[job.ID] = job;
        _ = Task.Run(() => Process(job, apiBaseUrl, accessToken));
        return job;
    }

    public Job? Find(Guid id) => jobs.TryGetValue(id, out var job) ? job : null;

    private static async Task<string> ReadError(HttpResponseMessage response)
    {
        var value = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(value)) return "AudioChoice could not complete that request.";
        try
        {
            var document = JsonDocument.Parse(value).RootElement;
            foreach (var property in new[] { "error", "detail", "title", "message" })
                if (document.TryGetProperty(property, out var error) && error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? value;
        }
        catch { }
        return value;
    }

    private async Task Process(Job job, string apiBaseUrl, string accessToken)
    {
        try
        {
            job.Update("hashing", "Checking your audiobook before transfer.", 2);
            await using var source = File.OpenRead(job.StagedPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
            job.Update("authorizing", "Creating your secure, temporary transfer.", 7);
            using var create = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}/v1/companion/transfers")
            {
                Content = JsonContent.Create(new { fileName = job.FileName, contentType = job.ContentType, fileSize = job.FileSize, sha256 = hash })
            };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var createResponse = await clients.CreateClient("audiochoice").SendAsync(create);
            if (!createResponse.IsSuccessStatusCode) throw new InvalidOperationException(await ReadError(createResponse));
            var transfer = await createResponse.Content.ReadFromJsonAsync<TransferResponse>() ?? throw new InvalidOperationException("The transfer could not be prepared.");

            job.Update("uploading", "Uploading securely. Keep Companion open until the QR code appears.", 10);
            await using var uploadStream = File.OpenRead(job.StagedPath);
            using var uploadContent = new ProgressStreamContent(uploadStream, job.FileSize, job.UpdateUploadProgress);
            using var upload = new HttpRequestMessage(new HttpMethod(transfer.Method), transfer.UploadURL) { Content = uploadContent };
            upload.Content.Headers.ContentType = new MediaTypeHeaderValue(job.ContentType);
            upload.Content.Headers.ContentLength = job.FileSize;
            foreach (var header in transfer.Headers ?? [])
                if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) upload.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var uploadResponse = await clients.CreateClient("audiochoice").SendAsync(upload);
            if (!uploadResponse.IsSuccessStatusCode) throw new InvalidOperationException($"The secure upload was declined ({(int)uploadResponse.StatusCode}). {await ReadError(uploadResponse)}");

            job.Update("finalizing", "Finalizing the handoff.", 96);
            using var finish = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}/v1/companion/transfers/{transfer.TransferID}/complete");
            finish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var finishResponse = await clients.CreateClient("audiochoice").SendAsync(finish);
            if (!finishResponse.IsSuccessStatusCode) throw new InvalidOperationException(await ReadError(finishResponse));
            job.Complete(transfer.ReceiverURL, transfer.ExpiresAt);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Companion transfer job {TransferJobID} failed", job.ID);
            job.Fail(exception.Message);
        }
        finally
        {
            try { File.Delete(job.StagedPath); } catch { }
        }
    }

    public sealed class Job(Guid id, string stagedPath, string fileName, long fileSize, string contentType)
    {
        private readonly object gate = new();
        public Guid ID { get; } = id;
        public string StagedPath { get; } = stagedPath;
        public string FileName { get; } = fileName;
        public long FileSize { get; } = fileSize;
        public string ContentType { get; } = contentType;
        private string status = "received", message = "File received.";
        private int progress;
        private string? receiverURL, error;
        private DateTimeOffset? expiresAt;
        public void Update(string newStatus, string newMessage, int? newProgress = null) { lock (gate) { status = newStatus; message = newMessage; if (newProgress is not null) progress = Math.Clamp(newProgress.Value, 0, 100); } }
        public void UpdateUploadProgress(long bytesTransferred) { lock (gate) { progress = Math.Clamp(10 + (int)Math.Round(bytesTransferred * 84d / Math.Max(1, FileSize)), 10, 94); } }
        public void Complete(string url, DateTimeOffset expiration) { lock (gate) { status = "ready"; progress = 100; message = "Transfer is ready. Scan the QR code with AudioChoice on your phone."; receiverURL = url; expiresAt = expiration; } }
        public void Fail(string reason) { lock (gate) { status = "failed"; error = reason; message = "The phone transfer could not be completed."; } }
        public string? ReceiverURL { get { lock (gate) return receiverURL; } }
        public object Public() { lock (gate) return new { status, message, progress, receiverURL, expiresAt, error }; }
    }
}

sealed class ProgressStreamContent(Stream source, long length, Action<long> onProgress) : HttpContent
{
    protected override bool TryComputeLength(out long contentLength)
    {
        contentLength = length;
        return true;
    }

    protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
    {
        var buffer = new byte[128 * 1024];
        long transferred = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read));
            transferred += read;
            onProgress(transferred);
        }
    }
}

sealed class CompanionSettings
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioChoice", "companion-session.json");
    private Session _session;
    public string ApiBaseUrl => Environment.GetEnvironmentVariable("AUDIOCHOICE_API_URL")?.TrimEnd('/') ?? "https://audiochoice-stg-api.grayocean-b35d4bf9.eastus.azurecontainerapps.io";
    public string GoogleClientID => Environment.GetEnvironmentVariable("AUDIOCHOICE_GOOGLE_CLIENT_ID") ?? "105248861745-34kh2v9g9825kb1drs3jrgmijjum2p3o.apps.googleusercontent.com";
    public string? AccessToken => _session.AccessToken;
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_session.AccessToken);

    public CompanionSettings() => _session = Load();
    public object Public() => new { signedIn = IsSignedIn, email = _session.Email, displayName = _session.DisplayName, googleClientID = GoogleClientID };
    public void Save(string token, string email, string displayName)
    {
        _session = new Session(token, email, displayName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_session));
    }
    public void Clear()
    {
        _session = new Session(null, null, null);
        if (File.Exists(_path)) File.Delete(_path);
    }
    private Session Load()
    {
        try { return JsonSerializer.Deserialize<Session>(File.ReadAllText(_path)) ?? new(null, null, null); }
        catch { return new(null, null, null); }
    }
    private sealed record Session(string? AccessToken, string? Email, string? DisplayName);
}
