using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace VortexDownloader.Tests;

public class ExtensionVerificationTests
{
    private static (string InTreeDir, string RootDir) ResolveDirectories()
    {
        string current = AppContext.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(current);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VortexDownloader.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
            throw new DirectoryNotFoundException("Could not locate solution directory containing VortexDownloader.sln");

        string repoRoot = dir.FullName;
        string inTreeDir = Path.Combine(repoRoot, "extension");
        string workspaceRoot = dir.Parent?.FullName ?? throw new DirectoryNotFoundException("Could not locate workspace root");
        string rootDir = Path.Combine(workspaceRoot, "VortexExtension");

        return (inTreeDir, rootDir);
    }

    [Fact]
    public void ExtensionFolders_AreSynchronized_AndShareIdenticalHashes()
    {
        var (inTreeDir, rootDir) = ResolveDirectories();

        Assert.True(Directory.Exists(inTreeDir), $"In-tree extension directory missing: {inTreeDir}");
        Assert.True(Directory.Exists(rootDir), $"Root VortexExtension directory missing: {rootDir}");

        var inTreeFiles = Directory.GetFiles(inTreeDir, "*.*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(inTreeDir, f), CalculateSha256);
        var rootFiles = Directory.GetFiles(rootDir, "*.*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(rootDir, f), CalculateSha256);

        // Assert all in-tree files exist in root with matching hashes
        foreach (var (relPath, hash) in inTreeFiles)
        {
            Assert.True(rootFiles.ContainsKey(relPath), 
                $"File '{relPath}' exists in VortexDownloader\\extension but is MISSING in VortexExtension. Run scripts/sync-extension.ps1.");
            Assert.Equal(hash, rootFiles[relPath]);
        }

        // Assert no extra files in root
        foreach (var relPath in rootFiles.Keys)
        {
            Assert.True(inTreeFiles.ContainsKey(relPath), 
                $"File '{relPath}' exists in VortexExtension but is MISSING in VortexDownloader\\extension.");
        }
    }

    [Fact]
    public void ManifestV3_HasValidSchemaAndRequiredPermissions()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string manifestPath = Path.Combine(inTreeDir, "manifest.json");
        Assert.True(File.Exists(manifestPath), "manifest.json not found");

        string json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("manifest_version").GetInt32());
        Assert.True(root.TryGetProperty("name", out _));
        Assert.Equal("1.6.0", root.GetProperty("version").GetString());

        // Validate Permissions
        var permissions = root.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToHashSet();

        Assert.Contains("downloads", permissions);
        Assert.Contains("notifications", permissions);
        Assert.Contains("contextMenus", permissions);
        Assert.Contains("cookies", permissions);
        Assert.Contains("storage", permissions);

        // Permissions must NOT contain match patterns
        Assert.DoesNotContain("<all_urls>", permissions);
        Assert.DoesNotContain("*://*/*", permissions);

        // Validate Host Permissions
        var hostPerms = root.GetProperty("host_permissions").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToHashSet();
        Assert.Contains("<all_urls>", hostPerms);

        // Validate Service Worker
        var bg = root.GetProperty("background");
        string sw = bg.GetProperty("service_worker").GetString()!;
        Assert.Equal("background.js", sw);
        Assert.True(File.Exists(Path.Combine(inTreeDir, sw)), $"Service worker file '{sw}' does not exist on disk.");

        // Validate Action Default Popup
        var action = root.GetProperty("action");
        string popup = action.GetProperty("default_popup").GetString()!;
        Assert.Equal("popup.html", popup);
        Assert.True(File.Exists(Path.Combine(inTreeDir, popup)), $"Default popup file '{popup}' does not exist on disk.");
    }

    [Fact]
    public void ManifestV3_AllDeclaredIconsExistOnDisk()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string manifestPath = Path.Combine(inTreeDir, "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        var icons = root.GetProperty("icons");
        foreach (string size in new[] { "16", "32", "48", "128" })
        {
            string iconRel = icons.GetProperty(size).GetString()!;
            string fullPath = Path.Combine(inTreeDir, iconRel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Icon {size}px at '{iconRel}' does not exist on disk.");
            Assert.True(new FileInfo(fullPath).Length > 100, $"Icon {size}px at '{iconRel}' is empty.");
        }
    }

    [Fact]
    public void PopupHtml_CompliesWithCSP_HasNoInlineScriptsOrEventHandlers()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string popupHtmlPath = Path.Combine(inTreeDir, "popup.html");
        Assert.True(File.Exists(popupHtmlPath));

        string html = File.ReadAllText(popupHtmlPath);

        // Check for inline <script> tags without src
        Assert.DoesNotMatch(@"<script(?![^>]*src=)[^>]*>[\s\S]*?<\/script>", html);

        // Check for inline event handlers (onclick=, onload=, onerror=, etc.)
        Assert.DoesNotMatch(@"\son[a-zA-Z]+\s*=", html);

        // Check for toggle switch element
        Assert.Contains("id=\"toggleIntercept\"", html);
        Assert.Contains("v1.6.0", html);
    }

    [Fact]
    public void BackgroundJs_DoesNotContainDeadActionOnClickListener()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string bgPath = Path.Combine(inTreeDir, "background.js");
        Assert.True(File.Exists(bgPath));

        string bgCode = File.ReadAllText(bgPath);

        // chrome.action.onClicked is dead code when default_popup is declared in manifest
        Assert.DoesNotContain("chrome.action.onClicked.addListener", bgCode);
    }

    [Fact]
    public void SecurityContract_AuthTokenAndPortAreConsistent()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string bgCode = File.ReadAllText(Path.Combine(inTreeDir, "background.js"));
        string popupCode = File.ReadAllText(Path.Combine(inTreeDir, "popup.js"));

        const string expectedToken = "vortex-bridge-extension-auth-token";
        const string expectedPort = "19741";

        Assert.Contains(expectedToken, bgCode);
        Assert.Contains(expectedPort, bgCode);

        Assert.Contains(expectedToken, popupCode);
        Assert.Contains(expectedPort, popupCode);
    }

    [Fact]
    public void BackgroundJs_ImplementsCookieRefererAndInterceptionToggle()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string bgCode = File.ReadAllText(Path.Combine(inTreeDir, "background.js"));

        Assert.Contains("getCookiesForUrl", bgCode);
        Assert.Contains("interceptEnabled", bgCode);
        Assert.Contains("syncInterceptionBadge", bgCode);
        Assert.Contains("referrer", bgCode);
        Assert.Contains("userAgent", bgCode);
    }

    [Fact]
    public void PopupJs_ImplementsInterceptionToggleAndCookieExtraction()
    {
        var (inTreeDir, _) = ResolveDirectories();
        string popupCode = File.ReadAllText(Path.Combine(inTreeDir, "popup.js"));

        Assert.Contains("toggleIntercept", popupCode);
        Assert.Contains("interceptEnabled", popupCode);
        Assert.Contains("chrome.storage.local", popupCode);
        Assert.Contains("chrome.cookies.getAll", popupCode);
    }

    [Fact]
    public void BrowserDownloadPayload_DeserializesCamelCaseJsonPayloadCorrectly()
    {
        string json = """
        {
            "url": "https://example.com/testfile.zip",
            "fileName": "testfile.zip",
            "fileSize": 1048576,
            "cookies": "sessionId=abc123xyz",
            "referer": "https://example.com/download-page",
            "userAgent": "Mozilla/5.0 Chrome/130"
        }
        """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var payload = JsonSerializer.Deserialize<VortexDownloader.Core.Models.BrowserDownloadPayload>(json, options);

        Assert.NotNull(payload);
        Assert.Equal("https://example.com/testfile.zip", payload.Url);
        Assert.Equal("testfile.zip", payload.FileName);
        Assert.Equal(1048576, payload.FileSize);
        Assert.Equal("sessionId=abc123xyz", payload.Cookies);
        Assert.Equal("https://example.com/download-page", payload.EffectiveReferrer);
        Assert.Equal("Mozilla/5.0 Chrome/130", payload.UserAgent);
    }

    private static string CalculateSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}
