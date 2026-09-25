using System.Security.Cryptography;
using System.Text;

namespace RequestPortal.Web.Services;

internal static class FfpManagerEndpointAuthorization
{
    public static bool IsAuthorized(HttpRequest request, string? expectedSecret)
    {
        if (string.IsNullOrWhiteSpace(expectedSecret))
        {
            return false;
        }

        string supplied = request.Headers["X-FFP-Integration-Key"].ToString();
        byte[] expected = Encoding.UTF8.GetBytes(expectedSecret);
        byte[] actual = Encoding.UTF8.GetBytes(supplied);
        return expected.Length == actual.Length &&
               CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
