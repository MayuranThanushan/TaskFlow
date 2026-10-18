using Microsoft.AspNetCore.Identity;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<long>, IAuditable
{
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
