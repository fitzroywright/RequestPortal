using System;

namespace RequestPortal.Domain.ReferenceData;

public sealed class RequestCategory
{
    private RequestCategory() { }
    public RequestCategory(Guid id, string name, string? parentName = null) { Id = id; Name = Require(name); ParentName = Normalize(parentName); IsActive = true; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? ParentName { get; private set; }
    public bool IsActive { get; private set; }
    public void SetActive(bool active) => IsActive = active;
    private static string Require(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Name is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProgramClassification
{
    private ProgramClassification() { }
    public ProgramClassification(Guid id, string name) { Id = id; Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Name is required.") : name.Trim(); IsActive = true; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public void SetActive(bool active) => IsActive = active;
}

public sealed class UnitOfMeasure
{
    private UnitOfMeasure() { }
    public UnitOfMeasure(Guid id, string code, string name) { Id = id; Code = code.Trim().ToUpperInvariant(); Name = name.Trim(); IsActive = true; }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public void SetActive(bool active) => IsActive = active;
}
