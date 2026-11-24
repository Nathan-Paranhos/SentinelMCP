namespace Sentinel.Core.Exceptions;

public abstract class DomainException : Exception
{
    public string Code { get; }

    protected DomainException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public class ValidationException : DomainException
{
    public ValidationException(string message) : base("VALIDATION_ERROR", message) { }
}

public class NotFoundException : DomainException
{
    public NotFoundException(string message) : base("NOT_FOUND", message) { }
}

public class VersionConflictException : DomainException
{
    public Guid? TopicId { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }

    public VersionConflictException(Guid? topicId, int expectedVersion, int actualVersion)
        : base("VERSION_CONFLICT", $"Version conflict on topic {topicId}: expected {expectedVersion}, but actual is {actualVersion}")
    {
        TopicId = topicId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}

public class IdempotencyKeyReusedException : DomainException
{
    public IdempotencyKeyReusedException(string message = "Idempotency key reused with different arguments.")
        : base("IDEMPOTENCY_KEY_REUSED", message) { }
}

public class UnauthorizedDomainException : DomainException
{
    public UnauthorizedDomainException(string message = "Unauthorized access or missing required scope/role.")
        : base("UNAUTHORIZED", message) { }
}

public class TemporarilyUnavailableException : DomainException
{
    public TemporarilyUnavailableException(string message = "Service or database is temporarily unavailable.")
        : base("TEMPORARILY_UNAVAILABLE", message) { }
}
