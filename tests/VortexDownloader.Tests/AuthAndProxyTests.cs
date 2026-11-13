using System.IO;
using System.Net;
using System.Text;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Network;
using VortexDownloader.Core.Storage;

namespace VortexDownloader.Tests;

public class AuthAndProxyTests
{
    private static readonly byte[] Data = EngineHarness.RandomData(512 * 1024, seed: 11);

    private static string Basic(string user, string pass) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    [Fact]
    public async Task BasicAuth_Downloads_AndPasswordIsNeverPlainTextInStateFile()
    {
        await using var server = new RawHttpServer(Data, requiredAuth: Basic("alice", "s3cret"));
        await using var h = new EngineHarness();
        var task = await h.Engine.AddDownloadAsync(server.Url, h.Dest, chunkCount: 4, autoStart: false, authUser: "alice", authPassword: "s3cret");

        Assert.Null(await h.RunToEndAsync(task));
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));

        await h.Engine.SaveStateAsync();
        string json = File.ReadAllText(Path.Combine(h.Dir, "tasks.json"));
        Assert.DoesNotContain("s3cret", json);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")), json);
        Assert.Contains("AuthPasswordProtected", json);

        // Restart: the DPAPI blob decrypts back for the same user, so the task can resume.
        var reloaded = Assert.Single(await new StateStore(Path.Combine(h.Dir, "tasks.json")).LoadTasksAsync());
        Assert.Equal("s3cret", reloaded.AuthPassword);
        Assert.Equal(task.AuthOrigin, reloaded.AuthOrigin);
    }

    [Fact]
    public async Task UserInfoInUrl_IsStripped_AndBecomesTheCredential()
    {
        await using var server = new RawHttpServer(Data, requiredAuth: Basic("bob", "p@ss:w"));
        await using var h = new EngineHarness();
        string withUserInfo = server.Url.Replace("http://", "http://bob:p%40ss:w@");

        var task = await h.Engine.AddDownloadAsync(withUserInfo, h.Dest, chunkCount: 2, autoStart: false);

        Assert.Equal(server.Url, task.Url);
        Assert.Equal("bob", task.AuthUser);
        Assert.Null(await h.RunToEndAsync(task));
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
    }

    [Theory]
    [InlineData("https://u:p@host:8443/a?b=1#f", "https://host:8443/a?b=1#f", "u", "p")]
    [InlineData("http://u%3Ax@host/x", "http://host/x", "u:x", null)]
    [InlineData("http://host/x", "http://host/x", null, null)]
    public void StripUserInfo_SplitsUserAndPassword(string url, string clean, string? user, string? password)
    {
        Assert.Equal(clean, HttpCredential.StripUserInfo(url, out var u, out var p));
        Assert.Equal(user, u);
        Assert.Equal(password, p);
    }

    [Fact]
    public void Credential_AnswersOnlyForItsOwnOrigin()
    {
        var c = HttpCredential.For("https://files.example.com/a.iso", "u", "p")!;
        Assert.NotNull(c.HeaderFor("https://files.example.com/other"));
        Assert.NotNull(c.HeaderFor("https://FILES.example.com:443/x"));
        Assert.Null(c.HeaderFor("http://files.example.com/a.iso"));      // no downgrade to plain HTTP
        Assert.Null(c.HeaderFor("https://files.example.com:8443/a.iso")); // other port
        Assert.Null(c.HeaderFor("https://mirror.example.com/a.iso"));
        Assert.Null(c.HeaderFor("https://files.example.com.evil.net/a.iso"));
    }

    [Fact]
    public async Task Credential_IsNotSentToMirrorOnAnotherHost()
    {
        await using var primary = new RawHttpServer(Data, requiredAuth: Basic("alice", "pw"));
        await using var mirror = new RawHttpServer(Data);
        await using var h = new EngineHarness();
        var task = await h.Engine.AddDownloadAsync(primary.Url, h.Dest, chunkCount: 8, autoStart: false,
            mirrorUrls: [mirror.Url], authUser: "alice", authPassword: "pw");
        Assert.Single(task.MirrorUrls);

        Assert.Null(await h.RunToEndAsync(task));
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
        Assert.True(mirror.GetCount > 0, "mirror was not used");
        Assert.All(mirror.SeenAuthorization, a => Assert.Null(a));
    }

    [Fact]
    public async Task Credential_IsNotResentAfterRedirectToAnotherHost()
    {
        await using var target = new RawHttpServer(Data);
        await using var origin = new RawHttpServer(Data, _ => new RawHttpServer.Plan(Location: target.Url), requiredAuth: Basic("alice", "pw"));
        await using var h = new EngineHarness();
        var task = await h.Engine.AddDownloadAsync(origin.Url, h.Dest, chunkCount: 2, autoStart: false, authUser: "alice", authPassword: "pw");

        Assert.Null(await h.RunToEndAsync(task));
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
        Assert.True(target.GetCount > 0);
        Assert.All(target.SeenAuthorization, a => Assert.Null(a));
    }

    [Fact]
    public async Task Credential_IsNotSentToRefreshedLinkOnAnotherHost()
    {
        await using var origin = new RawHttpServer(Data, requiredAuth: Basic("alice", "pw"));
        await using var fresh = new RawHttpServer(Data);
        await using var h = new EngineHarness();
        var task = await h.Engine.AddDownloadAsync(origin.Url, h.Dest, chunkCount: 4, autoStart: false, authUser: "alice", authPassword: "pw");

        Assert.Null(await h.Engine.RefreshTaskUrlAsync(task.Id, fresh.Url));
        Assert.Null(await h.RunToEndAsync(task));
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
        Assert.True(fresh.GetCount > 0);
        Assert.All(fresh.SeenAuthorization, a => Assert.Null(a));
    }

    [Fact]
    public async Task LegacyTaskWithUserInfoInUrl_IsMigratedOnLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vortex_auth_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "tasks.json");
            await new StateStore(file).SaveTasksAsync([new Core.Models.DownloadTask { Url = "https://bob:old%21pw@nas.example.com/a.iso" }]);

            await using (var engine = new DownloadEngine(new StateStore(file), new HttpClient()) { ScanWithDefender = false })
            {
                await engine.InitializeAsync();
                var t = Assert.Single(engine.Tasks);
                Assert.Equal("https://nas.example.com/a.iso", t.Url);
                Assert.Equal("bob", t.AuthUser);
                Assert.Equal("old!pw", t.AuthPassword);
                Assert.Equal("https://nas.example.com", t.AuthOrigin);
            }
            string json = File.ReadAllText(file);
            Assert.DoesNotContain("old%21pw", json);
            Assert.DoesNotContain("bob:", json);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(false)] // wrong password on the primary
    [InlineData(true)]  // a mirror asks for a login we do not have for it
    public async Task Unauthorized_FailsWithAuthMessage_WithoutKillingSources(bool mirrorNeedsAuth)
    {
        await using var primary = new RawHttpServer(Data, requiredAuth: mirrorNeedsAuth ? null : Basic("alice", "right"));
        // A mirror that asks for a login only on GET: it passes the add-time probe, then answers 401 mid-download.
        await using var mirror = new RawHttpServer(Data, _ => new RawHttpServer.Plan(Status: mirrorNeedsAuth ? 401 : 0));
        await using var h = new EngineHarness();
        var meta = new UrlMetadata(primary.Url, "out.bin", Data.Length, SupportsRange: true, ETag: null);
        var task = await h.Engine.AddDownloadAsync(primary.Url, h.Dest, chunkCount: 4, autoStart: false, preloadedMetadata: meta,
            mirrorUrls: [mirror.Url], authUser: mirrorNeedsAuth ? null : "alice", authPassword: mirrorNeedsAuth ? null : "wrong");
        Assert.Single(task.MirrorUrls);

        Assert.NotNull(await h.RunToEndAsync(task));
        Assert.Equal(DownloadEngine.AuthFailedMessage, task.ErrorMessage);
        Assert.False(task.LinkExpired);
        Assert.Empty(task.DeadSources);
    }

    [Fact]
    public async Task Inspect_ReportsAuthRequired()
    {
        await using var server = new RawHttpServer(Data, requiredAuth: Basic("alice", "pw"));
        await using var h = new EngineHarness();
        Assert.True((await h.Engine.InspectUrlAsync(server.Url)).AuthRequired);
        var ok = await h.Engine.InspectUrlAsync(server.Url, authUser: "alice", authPassword: "pw");
        Assert.False(ok.AuthRequired);
        Assert.Equal(Data.Length, ok.ContentLength);
    }

    [Fact]
    public void SecretProtector_RoundTrips_AndRejectsGarbage()
    {
        string blob = SecretProtector.Protect("hunter2")!;
        Assert.DoesNotContain("hunter2", blob);
        Assert.Equal("hunter2", SecretProtector.Unprotect(blob));
        Assert.Null(SecretProtector.Protect(""));
        Assert.Null(SecretProtector.Unprotect("not base64!"));
        Assert.Null(SecretProtector.Unprotect(Convert.ToBase64String(new byte[64])));
    }

    [Fact]
    public void Proxy_SystemKeepsDefault_NoneGoesDirect()
    {
        Assert.Null(ProxyFactory.Create(ProxyMode.System, "ignored", 8080));
        var none = ProxyFactory.Create(ProxyMode.None, null, 0)!;
        Assert.True(none.IsBypassed(new Uri("https://example.com/file")));
    }

    [Fact]
    public void Proxy_Manual_UsesProxyButKeepsLocalAndLanDirect()
    {
        var proxy = ProxyFactory.Create(ProxyMode.Manual, " proxy.corp ", 3128, "pu", "ppw")!;
        Assert.Equal(new Uri("http://proxy.corp:3128/"), proxy.GetProxy(new Uri("https://example.com/a")));
        Assert.Equal("ppw", proxy.Credentials!.GetCredential(new Uri("http://proxy.corp:3128/"), "Basic")!.Password);

        foreach (string local in new[]
        {
            "http://127.0.0.1:19741/ping", "http://localhost:5000/", "http://[::1]:19741/", "http://nas/share.iso",
            "http://10.0.0.2:8080/f", "http://192.168.1.5/f", "http://172.16.3.4/f", "http://172.31.255.1/f", "http://169.254.1.1/"
        })
        {
            Assert.True(proxy.IsBypassed(new Uri(local)), local);
        }
        foreach (string remote in new[]
        {
            "https://example.com/", "http://110.1.2.3/", "http://172.32.0.1/", "http://10.example.com/", "http://192.168.1.5.evil.com/"
        })
        {
            Assert.False(proxy.IsBypassed(new Uri(remote)), remote);
        }
    }

    [Theory]
    [InlineData("", 8080)]
    [InlineData("proxy", 0)]
    [InlineData("proxy", 70000)]
    [InlineData("http://proxy.corp", 3128)]
    [InlineData("proxy.corp:3128", 3128)]
    [InlineData("user@proxy.corp", 3128)]
    public void Proxy_Manual_RejectsBadHostOrPort(string host, int port)
    {
        Assert.ThrowsAny<ArgumentException>(() => ProxyFactory.Create(ProxyMode.Manual, host, port));
    }
}
