using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RequestPortal.Common.Contacts;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

public sealed record InputFieldQuality(string Field,long Inspected,long Empty,long InvalidUnderNewRules,long NonCanonical);
public sealed record InputDataQualityReport(DateTimeOffset ObservedUtc,bool ReadOnly,IReadOnlyList<InputFieldQuality> Fields,long HistoricalUnknownMonthlyIncome,long OtherNegativeMonthlyIncome);

/// <summary>Fixed SELECTs, totals only. Never emits contact values or changes historical records.</summary>
public sealed class InputDataQualityAudit(RequestPortalDbContext db)
{
    public async Task<InputDataQualityReport> RunAsync(CancellationToken ct=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='30s'",ct);
        var fields=new List<InputFieldQuality>();
        // Identifiers are fixed source-code constants, never caller input.
        (string Table,string Column,bool Email)[] columns=[
            ("AspNetUsers","Email",true),("AspNetUsers","PhoneNumber",false),("AspNetUsers","AlternativePhoneNumber",false),
            ("quick_intakes","Email",true),("quick_intakes","MobileNumber",false),("quick_intakes","SecondaryTelephone",false),
            ("organizations","Email",true),("organizations","SecondaryContactEmail",true),
            ("organizations","Phone",false),("organizations","AlternativePhone",false),("organizations","SecondaryContactPhone",false)];
        foreach(var field in columns)
        {
            long total=0,empty=0,invalid=0,nonCanonical=0;
            await using var command=db.Database.GetDbConnection().CreateCommand();
            command.Transaction=Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(transaction);
            command.CommandText=$"SELECT \"{field.Column}\" FROM \"{field.Table}\"";
            await using var reader=await command.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
            {
                total++;string? value=reader.IsDBNull(0)?null:reader.GetString(0);
                if(string.IsNullOrWhiteSpace(value)){empty++;continue;}
                try {string? normalized=field.Email?ContactEmail.Normalize(value):ApplicantTelephone.NormalizeNew(value);if(normalized!=value)nonCanonical++;}
                catch(ArgumentException){invalid++;}
            }
            fields.Add(new(field.Table+"."+field.Column,total,empty,invalid,nonCanonical));
        }
        long unknown=0,otherNegative=0;
        await using(var command=db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction=Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(transaction);
            command.CommandText="""
                SELECT COUNT(*) FILTER (WHERE "MonthlyIncome"<0 AND "DateOfBirth"=DATE '1900-01-01' AND "StreetAddress"='Not captured'),
                       COUNT(*) FILTER (WHERE "MonthlyIncome"<0 AND NOT ("DateOfBirth"=DATE '1900-01-01' AND "StreetAddress"='Not captured'))
                FROM quick_intakes
                """;
            await using var reader=await command.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);unknown=reader.GetInt64(0);otherNegative=reader.GetInt64(1);
        }
        await transaction.RollbackAsync(ct);
        return new(DateTimeOffset.UtcNow,true,fields,unknown,otherNegative);
    }
}
