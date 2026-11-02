using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace FfpManager.Bridge;

public static class FfpSqlConnection
{
    public static void ValidateTarget(string connectionString)
    {
        SqlConnectionStringBuilder settings;
        try { settings = new(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid bridge SQL configuration."); }
        if (!Regex.IsMatch(settings.DataSource, @"^(?:tcp:)?JMDB01(?:\.fftpj\.local)?(?:,1433)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !string.Equals(settings.InitialCatalog, "FFP", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(settings.FailoverPartner)
            || !string.IsNullOrEmpty(settings.AttachDBFilename)
            || settings.UserInstance)
            throw new InvalidOperationException("Bridge SQL access is restricted to JMDB01/FFP.");
        if (settings.Encrypt.Equals(SqlConnectionEncryptOption.Optional))
            throw new InvalidOperationException("Bridge SQL requires TLS encryption.");
    }

    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ValidateTarget(connectionString); // Reject forbidden targets before opening any connection.
        SqlConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using SqlCommand guard = new("IF DB_NAME() <> N'FFP' OR UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))) <> N'JMDB01' THROW 51050, 'Invalid bridge SQL target', 1;", connection);
            await guard.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
