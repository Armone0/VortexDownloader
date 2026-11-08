using System.IO;

namespace VortexDownloader.UI.Common;

public static class PathHelper
{
    public static string GetUniqueDestinationPath(string targetPath, IEnumerable<string>? activeFilePaths = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return targetPath;
        }

        HashSet<string>? activeSet = null;
        if (activeFilePaths != null)
        {
            activeSet = new HashSet<string>(
                activeFilePaths.Where(p => !string.IsNullOrWhiteSpace(p)),
                StringComparer.OrdinalIgnoreCase);
        }

        if (!File.Exists(targetPath) && (activeSet == null || !activeSet.Contains(targetPath)))
        {
            return targetPath;
        }

        string? dir = Path.GetDirectoryName(targetPath);
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(targetPath);
        string ext = Path.GetExtension(targetPath);

        if (string.IsNullOrEmpty(fileNameWithoutExt) && !string.IsNullOrEmpty(ext))
        {
            fileNameWithoutExt = ext;
            ext = string.Empty;
        }

        int index = 1;
        while (index < 10000)
        {
            string candidateName = $"{fileNameWithoutExt} ({index}){ext}";
            string candidatePath = string.IsNullOrEmpty(dir)
                ? candidateName
                : Path.Combine(dir, candidateName);

            if (!File.Exists(candidatePath) && (activeSet == null || !activeSet.Contains(candidatePath)))
            {
                return candidatePath;
            }

            index++;
        }

        string fallbackName = $"{fileNameWithoutExt}_{Guid.NewGuid():N}{ext}";
        return string.IsNullOrEmpty(dir) ? fallbackName : Path.Combine(dir, fallbackName);
    }
}
