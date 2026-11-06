using System.Text.RegularExpressions;

namespace RequestPortal.Infrastructure.Storage;

/// <summary>Prevents a configured CIFS target silently becoming an ordinary local directory.</summary>
public static class NetworkFolderMountGuard
{
    public static void Verify(string root,string expectedSource,string expectedMountRoot,string mountInfo)
    {
        string target=Path.GetFullPath(root).TrimEnd('/');
        foreach(string line in mountInfo.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            string[] halves=line.Split(" - ",2,StringSplitOptions.None);
            if(halves.Length!=2)continue;
            string[] left=halves[0].Split(' ',StringSplitOptions.RemoveEmptyEntries),right=halves[1].Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(left.Length<6 || right.Length<2 || Decode(left[4]).TrimEnd('/')!=target)continue;
            if(right[0]!="cifs" || !Decode(right[1]).Equals(expectedSource,StringComparison.OrdinalIgnoreCase) || Decode(left[3])!=expectedMountRoot)
                throw new InvalidOperationException("RP-STORAGE-MOUNT-001: The storage mount identity or scoped root does not match the authorized CIFS target.");
            if(!left[5].Split(',').Contains("rw"))throw new InvalidOperationException("RP-STORAGE-MOUNT-002: The scoped CIFS mount is not writable.");
            return;
        }
        throw new InvalidOperationException("RP-STORAGE-MOUNT-003: The required scoped CIFS bind mount is missing; local storage fallback is prohibited.");
    }
    private static string Decode(string value)=>Regex.Replace(value,@"\\([0-7]{3})",match=>((char)Convert.ToInt32(match.Groups[1].Value,8)).ToString());
}
