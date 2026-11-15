using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskFlow.Api.Hosting;
using TaskFlow.Api.Operations;

namespace TaskFlow.Tests;

public sealed class HostingTests
{
    [Theory]
    [InlineData("not base64")]
    [InlineData("<keyRing />")]
    [InlineData("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///private'>]><keyRing>&x;</keyRing>")]
    [InlineData("<other><key /></other>")]
    public void InvalidKeyMaterialFailsWithoutEchoingTheSecret(string value)
    {
        var encoded = value == "not base64" ? value : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
        var error = Assert.Throws<InvalidOperationException>(() => new ReadOnlyKeyRing(encoded));
        Assert.DoesNotContain(value, error.Message);
    }
    [Fact]
    public void RepositoryCannotBeMutatedByReadersOrKeyWriters()
    {
        var repository = new ReadOnlyKeyRing(ReadOnlyKeyRing.Encode([new XElement("key", new XAttribute("id", Guid.NewGuid()))]));
        repository.GetAllElements().Single().RemoveAttributes();
        Assert.NotNull(repository.GetAllElements().Single().Attribute("id"));
        Assert.Throws<InvalidOperationException>(() => repository.StoreElement(new XElement("key"), "test"));
    }
    [Fact]
    public void RotationKeepsOldPayloadsReadableAcrossIndependentServiceInstances()
    {
        using var folder = new PrivateFolder(); var first = Path.Combine(folder.Path, "first"); var second = Path.Combine(folder.Path, "second");
        Assert.Equal(0, HostingKeyCommand.Execute(["--hosting-keys=" + first]));
        var oldRing = File.ReadAllText(Path.Combine(first, "key-ring.base64"));
        using var old = Provider(oldRing); var payload = old.GetRequiredService<IDataProtectionProvider>().CreateProtector("hosting-test").Protect("synthetic payload");
        Assert.Equal(0, HostingKeyCommand.Execute(["--hosting-keys=" + second, "--previous-keyring=" + Path.Combine(first, "key-ring.base64")]));
        var newRing = File.ReadAllText(Path.Combine(second, "key-ring.base64"));
        Assert.Equal(2, new ReadOnlyKeyRing(newRing).GetAllElements().Count);
        using var replacement = Provider(newRing);
        Assert.Equal("synthetic payload", replacement.GetRequiredService<IDataProtectionProvider>().CreateProtector("hosting-test").Unprotect(payload));
        using var wrongApp = Provider(newRing, "different-app");
        Assert.Throws<CryptographicException>(() => wrongApp.GetRequiredService<IDataProtectionProvider>().CreateProtector("hosting-test").Unprotect(payload));
        var snapshot = File.ReadAllText(Path.Combine(first, "key-ring.base64"));
        Assert.Equal(1, HostingKeyCommand.Execute(["--hosting-keys=" + first])); Assert.Equal(snapshot, File.ReadAllText(Path.Combine(first, "key-ring.base64")));
        Assert.Equal(1, HostingKeyCommand.Execute(["--hosting-keys=" + Path.Combine(folder.Path, "wwwroot", "keys")]));
    }
    [Theory]
    [InlineData("https", "test.onrender.com", HttpStatusCode.OK)]
    [InlineData("http", "test.onrender.com", HttpStatusCode.BadRequest)]
    [InlineData("https,http", "test.onrender.com", HttpStatusCode.BadRequest)]
    [InlineData("https", "localhost", HttpStatusCode.BadRequest)]
    public async Task OnlyExplicitRenderHostAndSingleHttpsSchemeReceiveSecureCookies(string forwarded, string host, HttpStatusCode expected)
    {
        using var keys = new PrivateFolder();
        var ring = Ring(keys.Path);
        await using var factory = Factory(ring);
        using var client = factory.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Host = host; client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwarded); client.DefaultRequestHeaders.Add("X-Forwarded-Host", "attacker.example");
        var response = await client.GetAsync("/api/v1/auth/csrf"); Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-TaskFlow.Csrf="));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        }
    }
    [Fact]
    public void RenderModeRequiresPlatformIdentityAndActivePersistentKeys()
    {
        using var folder = new PrivateFolder(); var ring = Ring(folder.Path);
        using var missing = Factory(ring, new() { ["RENDER"] = "false" });
        Assert.Throws<InvalidOperationException>(() => missing.CreateClient());
        using var noKeys = Factory("", new()); Assert.Throws<InvalidOperationException>(() => noKeys.CreateClient());
        var expired = new ReadOnlyKeyRing(ring).GetAllElements().Select(e => new XElement(e)).ToArray();
        expired[0].Element("expirationDate")!.Value = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        using var old = Factory(ReadOnlyKeyRing.Encode(expired)); Assert.Throws<InvalidOperationException>(() => old.CreateClient());
    }
    [Fact]
    public async Task RenderInternalHttpHealthDoesNotAttemptAnApplicationHttpsRedirect()
    {
        using var keys=new PrivateFolder();var warnings=new RedirectWarningRecorder();await using var factory=Factory(Ring(keys.Path)).WithWebHostBuilder(builder=>builder.ConfigureLogging(logging=>logging.AddProvider(warnings)));
        using var client=factory.CreateClient(new(){BaseAddress=new("http://localhost"),AllowAutoRedirect=false});client.DefaultRequestHeaders.Host="healthcheck.render.com";
        var response=await client.GetAsync("/api/v1/health/live");Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.Null(response.Headers.Location);Assert.Equal(0,warnings.Count);
        var credentials=await client.GetAsync("/api/v1/auth/csrf");Assert.Equal(HttpStatusCode.BadRequest,credentials.StatusCode);
    }
    private sealed class RedirectWarningRecorder : ILoggerProvider,ILogger
    {
        private int count;
        public int Count=>Volatile.Read(ref count);
        public ILogger CreateLogger(string categoryName)=>categoryName=="Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware"?this:Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public bool IsEnabled(LogLevel logLevel)=>logLevel>=LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? error,Func<TState,Exception?,string> formatter){if(level>=LogLevel.Warning&&eventId.Id==3)Interlocked.Increment(ref count);}
        public void Dispose(){}
    }
    private static ServiceProvider Provider(string ring, string application = "TaskFlow")
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddDataProtection().SetApplicationName(application).DisableAutomaticKeyGeneration();
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = new ReadOnlyKeyRing(ring)); return services.BuildServiceProvider();
    }
    private static string Ring(string parent)
    {
        var output = Path.Combine(parent, "ring"); Assert.Equal(0, HostingKeyCommand.Execute(["--hosting-keys=" + output])); return File.ReadAllText(Path.Combine(output, "key-ring.base64"));
    }
    private static WebApplicationFactory<Program> Factory(string ring, Dictionary<string, string?>? overrides = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Production"); builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?> { ["Hosting:RenderEdge"] = "true", ["RENDER"] = "true", ["RENDER_SERVICE_TYPE"] = "web", ["RENDER_SERVICE_ID"] = "synthetic-service", ["RENDER_EXTERNAL_HOSTNAME"] = "test.onrender.com", ["DataProtection:ReadOnlyKeyRingBase64"] = ring, ["AllowedHosts"] = "*", ["Transport:RequireHttps"] = "true" };
            foreach (var pair in overrides ?? []) values[pair.Key] = pair.Value; config.AddInMemoryCollection(values);
        });
    });
    private sealed class PrivateFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "taskflow-hosting-" + Guid.NewGuid().ToString("N"));
        public PrivateFolder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var expected = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var actual = System.IO.Path.GetFullPath(Path);
            if (!actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(actual).StartsWith("taskflow-hosting-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup target.");
            Directory.Delete(actual, true);
        }
    }
}
