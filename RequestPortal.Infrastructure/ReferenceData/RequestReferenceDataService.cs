using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.ReferenceData;

public sealed class RequestReferenceDataService : IRequestReferenceDataService
{
    private readonly RequestPortalDbContext dbContext;

    public RequestReferenceDataService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<RequestCategoryOption>> GetCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        List<RequestCategoryOption> items = await dbContext.RequestCategories
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.ParentName)
            .ThenBy(item => item.Name)
            .Select(item => new RequestCategoryOption(item.Id, item.Name, item.ParentName))
            .ToListAsync(cancellationToken);

        return items;
    }

    public async Task<IReadOnlyList<ProgramClassificationOption>> GetProgramsAsync(
        CancellationToken cancellationToken = default)
    {
        List<ProgramClassificationOption> items = await dbContext.ProgramClassifications
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .Select(item => new ProgramClassificationOption(item.Id, item.Name))
            .ToListAsync(cancellationToken);

        return items;
    }

    public async Task<IReadOnlyList<UnitOfMeasureOption>> GetUnitsOfMeasureAsync(
        CancellationToken cancellationToken = default)
    {
        List<UnitOfMeasureOption> items = await dbContext.UnitsOfMeasure
            .AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name)
            .Select(item => new UnitOfMeasureOption(item.Id, item.Code, item.Name))
            .ToListAsync(cancellationToken);

        return items;
    }
}
