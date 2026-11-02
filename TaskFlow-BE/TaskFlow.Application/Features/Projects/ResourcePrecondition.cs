using System.Globalization;
namespace TaskFlow.Application.Features.Projects;

public sealed class ResourceVersionException(int status, string code) : Exception("Resource revision check failed.")
{ public int Status { get; } = status; public string Code { get; } = code; }
// Request-scoped input; checked only inside the authorized, locked mutation boundary.
public sealed class ResourcePrecondition
{
    public string? Header { get; set; }
    public bool Required { get; set; }
    public string? Resource { get; set; }
    public long ResourceId { get; set; }
    public void Check(long current)
    {
        if (Header is null) { if (Required) throw new ResourceVersionException(428, "resource.version_required"); return; }
        if (Header.Length is < 1 or > 19 || Header.Any(c => !char.IsAsciiDigit(c)) || Header[0] == '0'
            || !long.TryParse(Header, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || expected <= 0)
            throw new ResourceVersionException(400, "resource.invalid_version");
        if (expected != current) throw new ResourceVersionException(409, "resource.conflict");
    }
}
