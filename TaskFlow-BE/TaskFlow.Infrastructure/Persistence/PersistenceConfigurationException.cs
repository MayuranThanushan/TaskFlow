namespace TaskFlow.Infrastructure.Persistence;

public sealed class PersistenceConfigurationException()
    : InvalidOperationException("Configure ConnectionStrings__TaskFlow before using persistence.");
