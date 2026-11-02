using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace FfpManager.Bridge;

public static class FfpSqlConnection
{
    public static string DatabaseFor(string target) => target switch
    {
        "Rehearsal" => "FFP",
        "Production" => "FFPJM",
        _ => throw new InvalidOperationException("Bridge:SqlTarget must explicitly select Rehearsal or Production.")
    };

    public static void ValidateTarget(string connectionString, string target = "Rehearsal")
    {
        string database = DatabaseFor(target);
        SqlConnectionStringBuilder settings;
        try { settings = new(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid bridge SQL configuration."); }
        if (!Regex.IsMatch(settings.DataSource, @"^(?:tcp:)?JMDB01(?:\.fftpj\.local)?(?:,1433)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !string.Equals(settings.InitialCatalog, database, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(settings.FailoverPartner)
            || !string.IsNullOrEmpty(settings.AttachDBFilename)
            || settings.UserInstance
            || settings.ApplicationIntent != ApplicationIntent.ReadWrite
            || !string.IsNullOrEmpty(settings.ServerSPN)
            || !string.IsNullOrEmpty(settings.FailoverPartnerSPN))
            throw new InvalidOperationException($"Bridge SQL access is restricted to JMDB01/{database}.");
        if (settings.Encrypt.Equals(SqlConnectionEncryptOption.Optional))
            throw new InvalidOperationException("Bridge SQL requires TLS encryption.");
        if (!settings.IntegratedSecurity || !string.IsNullOrEmpty(settings.UserID)
            || !string.IsNullOrEmpty(settings.Password) || settings.Authentication != SqlAuthenticationMethod.NotSpecified)
            throw new InvalidOperationException("Bridge SQL requires the dedicated integrated service identity.");
    }

    public static Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken = default) =>
        OpenAsync(connectionString, "Rehearsal", cancellationToken);

    public static async Task<SqlConnection> OpenAsync(string connectionString, string target, CancellationToken cancellationToken = default)
    {
        ValidateTarget(connectionString, target);
        SqlConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using SqlCommand guard = new("""
                IF DB_NAME() <> @Database
                   OR UPPER(CONVERT(nvarchar(128), SERVERPROPERTY('MachineName'))) <> N'JMDB01'
                   OR CONVERT(nvarchar(128), CONNECTIONPROPERTY('auth_scheme')) <> N'KERBEROS'
                   OR (@Production = 1 AND LOWER(ORIGINAL_LOGIN()) <> N'fftpj\svc_ffpbridge')
                   THROW 51050, 'Invalid bridge SQL target or service authentication', 1;
                """, connection);
            guard.Parameters.Add("@Database", SqlDbType.NVarChar, 128).Value = DatabaseFor(target);
            guard.Parameters.Add("@Production", SqlDbType.Bit).Value = target == "Production";
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
