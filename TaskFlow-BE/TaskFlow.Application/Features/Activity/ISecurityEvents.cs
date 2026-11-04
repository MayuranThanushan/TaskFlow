namespace TaskFlow.Application.Features.Activity;

public enum SecurityAction { Registration, Login, Logout, PasswordProof, ProfileChanged, RequestRejected, RecoveryRequested, RecoveryCompleted, VerificationRequested, EmailVerified, PasswordChanged, Reauthentication }
public enum SecurityOutcome { Succeeded, Denied, LockedOut, Failed }
public interface ISecurityEvents
{
    Task WriteAsync(SecurityAction action, SecurityOutcome outcome, long? subjectId, long? actorId = null, CancellationToken token = default);
    Task RejectedRequestAsync(long? actorId, string routeTemplate, int status, CancellationToken token = default);
}
