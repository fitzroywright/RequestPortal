using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.ReferenceData;

public sealed record RequestCategoryOption(Guid Id, string Name, string? ParentName);

public sealed record ProgramClassificationOption(Guid Id, string Name);

public sealed record UnitOfMeasureOption(Guid Id, string Code, string Name);

public interface IRequestReferenceDataService
{
    Task<IReadOnlyList<RequestCategoryOption>> GetCategoriesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProgramClassificationOption>> GetProgramsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UnitOfMeasureOption>> GetUnitsOfMeasureAsync(
        CancellationToken cancellationToken = default);
}
