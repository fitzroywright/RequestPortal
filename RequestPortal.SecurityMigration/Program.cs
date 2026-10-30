using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Security.Abstractions;
using Common.Security.Models;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Security;

try
{
    if(args.Length<2 || args[0] is not ("export-public" or "apply-public" or "apply-staff"))
    {Console.WriteLine("export-public OUTPUT_JSON | apply-public APPROVED_JSON | apply-staff APPROVED_JSON. Uses REQUESTPORTAL_MIGRATION_POSTGRES. Export changes no database records. Review and protect the manifest before apply. Never links by email.");return 0;}
    string connection=Environment.GetEnvironmentVariable("REQUESTPORTAL_MIGRATION_POSTGRES") ?? throw new ArgumentException("Protected migration connection is required.");
    using var db=new RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortalDbContext>().UseNpgsql(connection).Options);
    var json=new JsonSerializerOptions {WriteIndented=true,PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()} };
    if(args[0]=="export-public")
    {
        var users=await db.Users.AsNoTracking().ToDictionaryAsync(x=>x.Id);
        var logins=await db.UserLogins.AsNoTracking().ToListAsync();var rows=new List<AccountCreation>();
        foreach(var group in logins.GroupBy(x=>x.UserId))
        {
            var user=users[group.Key];
            if(user.IsInternalStaff) throw new InvalidOperationException("A staff record has social-provider links. Resolve population classification against approved historical records before migration.");
            var keys=group.Select(x=>new ExternalIdentityKey(x.LoginProvider,PublicExternalIdentity.Scope(x.LoginProvider),x.ProviderKey)).ToArray();
            rows.Add(new AccountCreation(user.Id.ToString("D"),user.UserName ?? throw new InvalidOperationException("Missing approved alias."),user.DisplayName,AuthenticationProviderKind.External,
                AccountPopulation.PublicApplicant,user.LockoutEnd>DateTimeOffset.UtcNow ? SecurityAccountStatus.Suspended : SecurityAccountStatus.Active,keys[0],keys.Skip(1).ToArray()));
        }
        var fileOptions=new FileStreamOptions {Mode=FileMode.CreateNew,Access=FileAccess.Write};
        if(!OperatingSystem.IsWindows())fileOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        using var output=new FileStream(args[1],fileOptions);
        await JsonSerializer.SerializeAsync(output,rows,json);Console.WriteLine($"Exported {rows.Count} existing applicant mappings for review. No database changes.");return 0;
    }
    if(!OperatingSystem.IsWindows() && (File.GetUnixFileMode(args[1])&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))!=0) throw new ArgumentException("Manifest must be owner-only.");
    var manifest=JsonSerializer.Deserialize<AccountCreation[]>(await File.ReadAllTextAsync(args[1]),json) ?? throw new ArgumentException("Manifest is required.");
    bool staff=args[0]=="apply-staff";
    foreach(var row in manifest)
    {
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==Guid.Parse(row.Id));
        if(user is null || user.IsInternalStaff!=staff || row.Population!=(staff?AccountPopulation.Staff:AccountPopulation.PublicApplicant))
            throw new InvalidOperationException("Approved account identifiers and populations must match existing business records.");
    }
    var accounts=new RequestPortalAccounts(connection);accounts.EnsureSchema();
    (staff?accounts.Staff:accounts.Public).ImportApprovedAccounts(manifest,"approved-offline-identity-migration");
    Console.WriteLine($"Imported {manifest.Length} existing identity records transactionally. Applicant data, roles and provider-history tables were retained.");return 0;
}
catch(Exception exception)
{Console.Error.WriteLine("Identity migration stopped: "+exception.GetType().Name+". Resolve protected manifest/database conflicts before retrying; credentials and identities are not printed.");return 1;}
