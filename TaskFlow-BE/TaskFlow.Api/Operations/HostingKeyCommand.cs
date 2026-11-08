using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using TaskFlow.Api.Hosting;

namespace TaskFlow.Api.Operations;

public static class HostingKeyCommand
{
    public static bool IsRequested(string[] args) => args.Any(a => a.StartsWith("--hosting-keys", StringComparison.Ordinal));
    public static int Execute(string[] args)
    {
        try
        {
            var options = args.ToDictionary(a => a.Split('=', 2)[0], a => a.Split('=', 2).ElementAtOrDefault(1) ?? "");
            if (options.Keys.Any(k => k is not ("--hosting-keys" or "--previous-keyring"))) throw new InvalidOperationException();
            var path = Path.GetFullPath(options["--hosting-keys"]);
            if (Directory.Exists(path) || File.Exists(path) || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals("wwwroot", StringComparison.OrdinalIgnoreCase) || p.Equals("public", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException();
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var previous = options.TryGetValue("--previous-keyring", out var source)
                ? new ReadOnlyKeyRing(ReadBounded(source)).GetAllElements() : [];
            Directory.CreateDirectory(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var services = new ServiceCollection(); services.AddLogging();
            services.AddDataProtection().SetApplicationName("TaskFlow").PersistKeysToFileSystem(new DirectoryInfo(path));
            using var provider = services.BuildServiceProvider();
            var now = DateTimeOffset.UtcNow;
            provider.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(90));
            var current = Directory.GetFiles(path, "key-*.xml").Select(f => System.Xml.Linq.XElement.Load(f)).ToArray();
            var output = Path.Combine(path, "key-ring.base64");
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream)) writer.Write(ReadOnlyKeyRing.Encode(previous.Concat(current)));
            if (!OperatingSystem.IsWindows()) foreach (var file in Directory.GetFiles(path)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.WriteLine("Private hosting key ring created. Upload key-ring.base64 to DataProtection__ReadOnlyKeyRingBase64 in the hosting secret settings. Retain this directory privately; never commit or paste its contents into chat. Rotate before 90 days, retaining all prior keys.");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Hosting key generation refused or failed. Use a new private directory outside public assets, valid bounded prior key material and exact command options. Existing files are never overwritten; private diagnostics are suppressed.");
            return 1;
        }
    }
    private static string ReadBounded(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 512 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
        return File.ReadAllText(path).Trim();
    }
}
