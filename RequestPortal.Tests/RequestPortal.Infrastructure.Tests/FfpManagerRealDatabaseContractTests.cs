using Microsoft.Data.SqlClient;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerRealDatabaseContractTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("FFPMANAGER_TEST");

    [Fact]
    public async Task FfpManager_test_database_contains_RequestPortal_integration_schema()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        string[] requiredObjects =
        [
            "dbo.ApplicationLetters",
            "dbo.ItemsRequested",
            "dbo.SupportingDocument",
            "dbo.ApplicationStatusTypes",
            "dbo.ApplicationNotes",
            "dbo.DocumentTypes",
            "dbo.UnitsOfMeasure",
            "dbo.RequestPortalIntegrationOutbox"
        ];

        List<string> missing = [];

        foreach (string objectName in requiredObjects)
        {
            await using SqlCommand command = new(
                "SELECT CASE WHEN OBJECT_ID(@name) IS NULL THEN 0 ELSE 1 END;",
                connection);

            command.Parameters.AddWithValue("@name", objectName);

            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
            {
                missing.Add(objectName);
            }
        }

        await using (SqlCommand column = new(
            """
            SELECT COUNT(*)
            FROM sys.columns
            WHERE object_id = OBJECT_ID('dbo.ApplicationLetters')
              AND name = 'ExternalRequestId';
            """,
            connection))
        {
            if (Convert.ToInt32(await column.ExecuteScalarAsync()) == 0)
            {
                missing.Add("dbo.ApplicationLetters.ExternalRequestId");
            }
        }

        Assert.True(
            missing.Count == 0,
            "FFPMANAGER_TEST is missing Request Portal integration schema: " +
            string.Join(", ", missing));
    }

    [Fact]
    public async Task FfpManager_test_database_has_required_reference_data()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        Dictionary<string, string> queries = new()
        {
            ["ApplicationStatusTypes"] = "SELECT COUNT(*) FROM dbo.ApplicationStatusTypes;",
            ["DocumentTypes"] = "SELECT COUNT(*) FROM dbo.DocumentTypes;",
            ["UnitsOfMeasure"] = "SELECT COUNT(*) FROM dbo.UnitsOfMeasure;"
        };

        foreach ((string name, string sql) in queries)
        {
            await using SqlCommand command = new(sql, connection);
            int count = Convert.ToInt32(await command.ExecuteScalarAsync());

            Assert.True(
                count > 0,
                $"FFPMANAGER_TEST requires at least one {name} row for Request Portal integration testing.");
        }
    }

    [Fact]
    public async Task FfpManager_test_database_outbox_procedures_exist()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using SqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();

        string[] procedures =
        [
            "dbo.RequestPortal_EnqueueApplicationStatusChanged",
            "dbo.RequestPortal_EnqueueInformationRequest"
        ];

        List<string> missing = [];

        foreach (string procedure in procedures)
        {
            await using SqlCommand command = new(
                "SELECT CASE WHEN OBJECT_ID(@name, 'P') IS NULL THEN 0 ELSE 1 END;",
                connection);

            command.Parameters.AddWithValue("@name", procedure);

            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 0)
            {
                missing.Add(procedure);
            }
        }

        Assert.True(
            missing.Count == 0,
            "FFPMANAGER_TEST is missing Request Portal integration procedures: " +
            string.Join(", ", missing));
    }
}
