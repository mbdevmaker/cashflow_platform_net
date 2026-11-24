namespace CashFlow.Domain;

public enum DomainErrorKind
{
    Validation,
    NotFound,
    Conflict,
    BusinessRule
}

public sealed class DomainException : Exception
{
    public DomainException(DomainErrorKind kind, string code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public DomainErrorKind Kind { get; }
    public string Code { get; }

    public static DomainException Validation(string code, string message) => new(DomainErrorKind.Validation, code, message);
    public static DomainException NotFound(string code, string message) => new(DomainErrorKind.NotFound, code, message);
    public static DomainException Conflict(string code, string message) => new(DomainErrorKind.Conflict, code, message);
    public static DomainException Rule(string code, string message) => new(DomainErrorKind.BusinessRule, code, message);
}
