using System.IO;

namespace VortexDownloader.Core.Common;

/// <summary>Extension → category mapping shared by the sidebar filters and the "folder by type" option.</summary>
public static class FileCategories
{
    // Keys match the sidebar filter names used by MainViewModel.CurrentFilter.
    public const string Compressed = "Compressed";
    public const string Documents = "Documents";
    public const string Music = "Music";
    public const string Programs = "Programs";
    public const string Video = "Video";

    private static readonly (string Category, string[] Extensions)[] Map =
    [
        (Compressed, [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2"]),
        (Documents, [".pdf", ".docx", ".doc", ".xlsx", ".txt", ".pptx", ".epub"]),
        (Music, [".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a"]),
        (Programs, [".exe", ".msi", ".iso", ".bat", ".cmd", ".apk"]),
        (Video, [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm"]),
    ];

    /// <summary>Category key for the file's extension, or null when it is not in any category.</summary>
    public static string? GetCategory(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        foreach (var (category, exts) in Map)
        {
            if (exts.Contains(ext)) return category;
        }
        return null;
    }

    public static bool Is(string? fileName, string category) => GetCategory(fileName) == category;

    /// <summary>Turkish subfolder name for a category; unknown types go to "Diğer".</summary>
    public static string FolderName(string? category) => category switch
    {
        Video => "Video",
        Music => "Müzik",
        Documents => "Belgeler",
        Compressed => "Arşivler",
        Programs => "Programlar",
        _ => "Diğer"
    };

    /// <summary>
    /// When <paramref name="destinationPath"/> sits directly in <paramref name="defaultRoot"/>, returns the same file name
    /// inside the category subfolder (made unique with <paramref name="isTaken"/>). Any other path is returned unchanged.
    /// </summary>
    public static string ResolveAutoFolderPath(string destinationPath, string? defaultRoot, Func<string, bool> isTaken)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || string.IsNullOrWhiteSpace(defaultRoot)) return destinationPath;
        try
        {
            string full = Path.GetFullPath(destinationPath);
            string? dir = Path.GetDirectoryName(full);
            if (dir == null || !string.Equals(
                    Path.TrimEndingDirectorySeparator(dir),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRoot)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return destinationPath;
            }
            string fileName = Path.GetFileName(full);
            return UniqueFilePath(Path.Combine(dir, FolderName(GetCategory(fileName)), fileName), isTaken);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return destinationPath;
        }
    }

    /// <summary>Returns <paramref name="path"/>, or "name (n).ext" next to it when taken. Never returns a taken path.</summary>
    public static string UniqueFilePath(string path, Func<string, bool> isTaken)
    {
        if (!isTaken(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!isTaken(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }
}
