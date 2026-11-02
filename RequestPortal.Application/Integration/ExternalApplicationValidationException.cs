namespace RequestPortal.Application.Integration;

/// <summary>A submission field must be corrected before the bridge can accept it.</summary>
public sealed class ExternalApplicationValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}
