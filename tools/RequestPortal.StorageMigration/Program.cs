using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Common.Secrets;
using Common.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.StorageMigration;
using RequestPortal.Infrastructure.Storage;

// Source is always SharePoint. Runtime provider selection is never changed by this tool.
if(args.Length<3 || args[0] is not ("inventory" or "copy" or "verify" or "final-fence" or "import-locations"))return 2;
var flags=new Dictionary<string,string>(StringComparer.Ordinal);
for(int i=1;i<args.Length;i+=2) {if(i+1>=args.Length || !flags.TryAdd(args[i],args[i+1]))return 2;}
if(!flags.TryGetValue("--state",out string? state) || !Path.IsPathRooted(state))return 2;
if(flags.Keys.Any(x=>x is not ("--state" or "--destination" or "--authorize-cutover" or "--plan")))return 2;
using var stop=new CancellationTokenSource(TimeSpan.FromHours(12));
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
var ct=stop.Token;
try
{
    if(!OperatingSystem.IsLinux() || Dns.GetHostName().Split('.')[0]!="kratos")throw new MigrationSourceException("KRATOS_HOST_GUARD");
    state=Path.GetFullPath(state);
    if(!state.StartsWith("/var/lib/requestportal-storage-migration/",StringComparison.Ordinal))throw new MigrationSourceException("PRIVATE_STATE_PATH_GUARD");
    if(args[0]!="inventory")
    {
        if(!flags.TryGetValue("--destination",out var destination) || Path.GetFileName(destination)!="files")throw new MigrationSourceException("SCOPED_DESTINATION_REQUIRED");
        string expectedRoot=Environment.GetEnvironmentVariable("REQUESTPORTAL_MIGRATION_EXPECTED_MOUNT_ROOT")
            ?? throw new MigrationSourceException("SCOPED_MOUNT_ROOT_REQUIRED");
        if(!expectedRoot.EndsWith("/RequestPortal",StringComparison.Ordinal))throw new MigrationSourceException("SCOPED_MOUNT_ROOT_INVALID");
        NetworkFolderMountGuard.Verify(Path.GetDirectoryName(Path.GetFullPath(destination))!,"//nas01.fftpj.local/ffpmanager_data",expectedRoot,await File.ReadAllTextAsync("/proc/self/mountinfo",ct));
    }
    Directory.CreateDirectory(state);
    if(!OperatingSystem.IsWindows())File.SetUnixFileMode(state,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    await using var exclusive=new FileStream(Path.Combine(state,"migration.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    var builder=Host.CreateApplicationBuilder();
    var overrides=new Dictionary<string,string?> {["CommonSecrets:OpenBao:DeleteSecretIdFileAfterRead"]="false"};
    foreach(var provider in builder.Configuration.GetSection("CommonSecrets:Providers").GetChildren())
        overrides[$"CommonSecrets:Providers:{provider.Key}:Settings:DeleteSecretIdFileAfterRead"]="false";
    builder.Configuration.AddInMemoryCollection(overrides);
    await using var secrets=CommonSecretsBootstrapRuntime.Create(builder.Configuration);
    string connection=await secrets.Provider.GetRequiredAsync("ConnectionStrings:RequestPortal",ct);
    var settings=new NpgsqlConnectionStringBuilder(connection);
    if(!settings.Host!.Equals("axios.fftpj.local",StringComparison.OrdinalIgnoreCase) || settings.Database!="requestportal" ||
       !(await Dns.GetHostAddressesAsync(settings.Host,ct)).Contains(IPAddress.Parse("10.10.0.226")))throw new MigrationSourceException("PRODUCTION_ENDPOINT_GUARD");
    await using var db=new RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortalDbContext>().UseNpgsql(connection).Options);
    await using(var identity=await db.Database.BeginTransactionAsync(ct))
    {
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='15s'",ct);
        if(!await db.Database.SqlQueryRaw<bool>("SELECT host(inet_server_addr())='10.10.0.226' AND current_database()='requestportal' AND NOT pg_is_in_recovery() AND current_setting('server_version_num')::int/10000 BETWEEN 13 AND 18 AS \"Value\"").SingleAsync(ct))
            throw new MigrationSourceException("ACTUAL_DATABASE_TARGET_GUARD");
        await identity.RollbackAsync(ct);
    }
    string secretName=builder.Configuration["Storage:SharePoint:ClientSecretName"]?.Trim() ?? "storage/sharepoint/client-secret";
    var sourceConfiguration=new ConfigurationBuilder().AddConfiguration(builder.Configuration)
        .AddInMemoryCollection(new Dictionary<string,string?> {["Storage:Provider"]="SharePoint"}).Build();
    var options=StorageTargetOptions.FromConfiguration(sourceConfiguration,await secrets.Provider.GetRequiredAsync(secretName,ct)).SharePoint!;
    using var http=new HttpClient();http.Timeout=TimeSpan.FromMinutes(5);
    var client=new GraphReadClient(http,options,state);var source=new SharePointSource(client,options);
    string inventoryFile=Path.Combine(state,"inventory.json");
    var inventory=await source.InventoryAsync(ct);
    string candidate=inventoryFile+".writing";await File.WriteAllTextAsync(candidate,JsonSerializer.Serialize(inventory),ct);
    if(!OperatingSystem.IsWindows())File.SetUnixFileMode(candidate,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    File.Move(candidate,inventoryFile,true);
    var references=await DatabaseReconciliation.ReadAsync(db,inventory,ct);
    string referenceReport=Path.Combine(state,"reference-reconciliation.json");
    await File.WriteAllTextAsync(referenceReport,JsonSerializer.Serialize(references),ct);
    File.SetUnixFileMode(referenceReport,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    var ledger=await MigrationLedger.LoadAsync(state,ct);ledger.ReferenceFingerprint=references.Fingerprint;
    ArchiveEvidence? archive=null;
    if(args[0]=="copy")
    {
        if(!flags.TryGetValue("--destination",out var destination))throw new MigrationSourceException("DESTINATION_REQUIRED");
        if(inventory.Problems.Any(x=>x.StartsWith("FOREIGN_APPLICATION_METADATA:",StringComparison.Ordinal)))
            throw new MigrationSourceException("SOURCE_SCOPE_OWNERSHIP_BLOCKED");
        if(!string.Equals(builder.Configuration["Storage:Provider"],"SharePoint",StringComparison.OrdinalIgnoreCase))
            throw new MigrationSourceException("COPY_FORBIDDEN_AFTER_PROVIDER_CUTOVER");
        ledger=await new MigrationEngine(source,state,destination).CopyAsync(inventory,ct);
        ledger.ReferenceFingerprint=references.Fingerprint;await ledger.SaveAsync(state,ct);
        archive=await SourceArchive.CopyAsync(source,inventory,Path.Combine(Path.GetDirectoryName(destination)!,"source-archive"),state,destination,ct);
    }
    if(args[0] is "verify" or "final-fence" or "import-locations")
    {
        if(!flags.TryGetValue("--destination",out var destination))throw new MigrationSourceException("DESTINATION_REQUIRED");
        if(inventory.Problems.Count>0 || references.MissingSourceReferences>0 || references.AssociationMismatches>0)
            throw new MigrationSourceException("SOURCE_RECONCILIATION_BLOCKED");
        bool archiveVerified=await SourceArchive.VerifyAsync(inventory,Path.Combine(Path.GetDirectoryName(destination)!,"source-archive"),destination,source,ct);
        if(!archiveVerified)throw new MigrationSourceException("SOURCE_METADATA_ARCHIVE_NOT_VERIFIED");
        var target=new NetworkFolderStorage(destination);
        foreach(var file in inventory.Files)
            if(!await MigrationEngine.VerifyAsync(target,file,ct))throw new MigrationSourceException("DESTINATION_RECONCILIATION_BLOCKED");
        if(args[0] is "final-fence" or "import-locations")
        {
            // The root launcher must stop Request Portal writers before requesting this final consistency fence.
            var info=new System.Diagnostics.ProcessStartInfo("/usr/bin/systemctl") {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string part in new[] {"show","aegis-requestportal","-p","ActiveState","-p","SubState","-p","MainPID","-p","ControlPID"})info.ArgumentList.Add(part);
            using var process=System.Diagnostics.Process.Start(info) ?? throw new MigrationSourceException("WRITER_FENCE_UNAVAILABLE");
            string service=await process.StandardOutput.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);
            if(process.ExitCode!=0)throw new MigrationSourceException("WRITER_FENCE_UNAVAILABLE");
            var properties=service.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1],StringComparer.Ordinal);
            if(properties.GetValueOrDefault("ActiveState")!="inactive" || properties.GetValueOrDefault("SubState")!="dead" ||
               properties.GetValueOrDefault("MainPID")!="0" || properties.GetValueOrDefault("ControlPID")!="0")throw new MigrationSourceException("SOURCE_WRITERS_NOT_STOPPED");
            var second=await source.InventoryAsync(ct);
            var finalReferences=await DatabaseReconciliation.ReadAsync(db,second,ct);
            if(second.SourceFingerprint!=inventory.SourceFingerprint || finalReferences.Fingerprint!=references.Fingerprint)
                throw new MigrationSourceException("SOURCE_CHANGED_DURING_FINAL_FENCE");
            ledger.FinalSourceFenceVerified=true;ledger.SourceFingerprint=inventory.SourceFingerprint;
            ledger.ReferenceFingerprint=references.Fingerprint;await ledger.SaveAsync(state,ct);
        }
    }
    object? locations=null;
    if(args[0]=="import-locations")
    {
        if(!flags.TryGetValue("--plan",out var planFile) || !flags.TryGetValue("--authorize-cutover",out var authorization))
            throw new MigrationSourceException("EXPLICIT_ADMIN_CUTOVER_AUTHORIZATION_REQUIRED");
        var plan=await CutoverAuthorization.ReadAsync(planFile,authorization,ct);
        string revision=System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+').Last();
        CutoverAuthorization.Verify(plan,revision,state,flags["--destination"],Environment.GetEnvironmentVariable("REQUESTPORTAL_MIGRATION_EXPECTED_MOUNT_ROOT")!,inventory,references,ledger);
        // Schema is applied separately by the reviewed administrator procedure. Never call EF Migrate here.
        var migrations=await db.Database.GetAppliedMigrationsAsync(ct);
        if(!migrations.Contains("20261106221543_PersistRequestLocations"))throw new MigrationSourceException("LOCATION_SCHEMA_NOT_APPLIED");
        locations=await DatabaseReconciliation.ImportLocationsAsync(db,inventory,flags["--destination"],authorization,references.Fingerprint,ct);
    }
    var report=new {status="migration-phase-complete",phase=args[0],sourceFingerprint=inventory.SourceFingerprint,
        sourceCurrentFiles=inventory.Files.Count,sourceVersions=inventory.Files.Sum(x=>x.Versions.Count),
        sourceMissingFiles=inventory.Files.Count(x=>x.Content is null),
        sourceFileBytes=inventory.Files.Sum(x=>x.Current.Length),sourceProblems=inventory.Problems.Count,
        provisional=args[0] is not ("final-fence" or "import-locations"),references=new {references.Documents,references.ProfilePhotos,
            references.MissingSourceReferences,references.AssociationMismatches,references.Fingerprint},
        archive=archive is null ? null : new {archive.ArchivedControlFiles,archive.ArchivedUnmanagedFiles,nativeVersions=archive.NativeVersions?.Count},
        preCutoverCopyVerified=inventory.Problems.Count==0 && references.MissingSourceReferences==0 && references.AssociationMismatches==0 &&
            inventory.Files.All(x=>ledger.Files.TryGetValue(x.Current.StorageKey,out var transfer) && transfer.Status=="Verified"),
        transfers=ledger.Summary(),locations,client.Requests,client.ThrottledResponses,
        sourceWrites=false,sourceDeletes=0,providerChanged=false,deploymentAttempted=false};
    await File.WriteAllTextAsync(Path.Combine(state,"last-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web) {WriteIndented=true}),ct);
    Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web)));return 0;
}
catch(Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new {status="blocked",errorType=error.GetType().Name,
        code=error is MigrationSourceException safe?safe.Code:"MIGRATION_FAILED",providerChanged=false,sourceWrites=false,sourceDeletes=0}));return 1;
}
