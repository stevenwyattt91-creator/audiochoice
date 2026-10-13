using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace AudioChoice.LocalScanner;

/// <summary>
/// Satisfies <see cref="AudioChoiceDataPaths"/>'s constructor outside of ASP.NET hosting.
/// </summary>
/// <remarks>
/// <c>AudioChoiceDataPaths</c> (reused as-is from the backend, see that class's own remarks)
/// takes a real <see cref="IWebHostEnvironment"/> because it has only ever been constructed
/// inside the web host's own dependency injection container. This console app is not a web
/// host and never will be -- it has no HTTP server, no requests, nothing <c>IWebHostEnvironment</c>
/// is actually for -- so rather than reimplement <c>AudioChoiceDataPaths</c>'s own folder-naming
/// logic a second time (which would then need to be kept in step with the original by hand),
/// this stands in for just the one property that class actually reads: <see cref="ContentRootPath"/>.
/// </remarks>
internal sealed class MinimalHostEnvironment(string contentRootPath) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "AudioChoice.LocalScanner";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = contentRootPath;
    public string EnvironmentName { get; set; } = "Production";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRootPath;
}

/// <summary>A file provider that finds nothing, for the one property above that is never read.</summary>
internal sealed class NullFileProvider : IFileProvider
{
    public IDirectoryContents GetDirectoryContents(string subpath) =>
        throw new NotSupportedException("Not used outside of ASP.NET hosting.");
    public IFileInfo GetFileInfo(string subpath) =>
        throw new NotSupportedException("Not used outside of ASP.NET hosting.");
    public Microsoft.Extensions.Primitives.IChangeToken Watch(string filter) =>
        throw new NotSupportedException("Not used outside of ASP.NET hosting.");
}
