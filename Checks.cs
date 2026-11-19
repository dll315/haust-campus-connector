using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Threading;
using System.Text;
using System.Threading.Tasks;

static class Checks
{
    public static void Run()
    {
        Respond("200 OK", "text/plain", "Microsoft Connect Test", null, ProbeState.Online, null);
        Respond("302 Found", "text/plain", "", "/login?site=west", ProbeState.Portal, "/login?site=west");
        Respond("302 Found", "text/plain", "", "/login?site=east", ProbeState.Portal, "/login?site=east");
        Respond("302 Found", "text/plain", "", "file:///C:/Windows/win.ini", ProbeState.Offline, null);
        Respond("302 Found", "text/plain", "", "http://name:pass@example.test/", ProbeState.Offline, null);
        Respond("302 Found", "text/plain", "", "/probe", ProbeState.Offline, null);
        Respond("200 OK", "text/html", "<html>Campus login</html>", null, ProbeState.Portal, "/probe");
        Respond("511 Network Authentication Required", "text/plain", "", null, ProbeState.Portal, "/probe");
        Respond("500 Server Error", "text/html", "<html>Server error</html>", null, ProbeState.Offline, null);
        if (AppSettings.StartupCommand(@"C:\Program Files\CampusConnector.exe") !=
            "\"C:\\Program Files\\CampusConnector.exe\" --startup")
            throw new InvalidOperationException("Startup command failed to quote the executable path.");
        Authentication();
    }

    static void Authentication()
    {
        var account = new CampusAccount("test-student", "mock+& 密码", "local");
        var restored = AccountVault.Unprotect(AccountVault.Protect(account));
        Assert(restored.User == account.User && restored.Password == account.Password && restored.Isp == account.Isp, "Protected credential round trip.");
        var adapter = new[] { IPAddress.Parse("10.122.1.2") };
        var origin = HaustPortal.Candidate(new Uri("http://10.100.51.9/api/r/1"));
        Assert(origin.Scheme == "https" && origin.Host == "10.100.51.9", "Portal address change.");
        Assert(HaustPortal.Candidate(new Uri("https://haust.edu.cn.example.com/")) == null, "Reject untrusted domains.");
        Assert(HaustPortal.Candidate(new Uri("http://198.51.100.10/")) == null, "Reject public IPs.");
        Assert(HaustPortal.Candidate(new Uri("https://portal.haust.edu.cn:8443/")) == null, "Reject unknown ports.");
        Assert(!HaustPortal.VerifyBrowserPortal(origin, adapter, CancellationToken.None),
            "An IP URL validated with a DNS override cannot be opened as a verified browser URL.");
        var paths = new List<string>();
        var page = new Uri(origin, "/tpl/stu-xy/login.html?ip=10.122.1.2&mac=00%3A11%3A22%3A33%3A44%3A55&nasId=4");
        Func<Uri, Uri, string, string, PortalReply> mock = (uri, referer, body, token) =>
        {
            paths.Add(uri.AbsolutePath);
            if (uri.AbsolutePath == "/api/csrf-token") return new PortalReply(200, "{\"csrf_token\":\"fake\"}");
            Assert(token == "fake" && referer.AbsolutePath.EndsWith("/login_account.html"), "CSRF and referer.");
            if (uri.AbsolutePath == "/api/account/status")
            {
                Assert(body == null && !uri.Query.Contains("test-student") && uri.Query.Contains("nasId=4"), "No credentials in status URL.");
                return new PortalReply(200, "{\"code\":1}");
            }
            Assert(body.Contains("username=test-student") && body.Contains("password=mock%2B%26") && body.Contains("isp=local"), "Form encoding.");
            return new PortalReply(200, "{\"code\":0}");
        };
        Assert(HaustPortal.Authenticate(page, adapter, account, mock, CancellationToken.None) == AuthState.Accepted, "Account login.");
        Assert(string.Join(",", paths) == "/api/csrf-token,/api/account/status,/api/account/check,/api/account/login", "Request order.");
        paths.Clear();
        Func<Uri, Uri, string, string, PortalReply> changed = (uri, referer, body, token) =>
            uri.AbsolutePath == "/api/account/check" ? new PortalReply(200, "{\"code\":0,\"isChangePwd\":1}") : mock(uri, referer, body, token);
        Assert(HaustPortal.Authenticate(page, adapter, account, changed, CancellationToken.None) == AuthState.InteractionRequired &&
            !paths.Contains("/api/account/login"), "Honor password changes.");
        paths.Clear();
        Assert(HaustPortal.Authenticate(page, new[] { IPAddress.Parse("10.122.1.3") }, account, mock, CancellationToken.None) == AuthState.Unavailable &&
            paths.Count == 0, "Do not submit credentials for another IP.");
        Assert(HaustPortal.Authenticate(page, adapter, account, (uri, referer, body, token) =>
            uri.AbsolutePath == "/api/account/login" ? new PortalReply(200, "{\"code\":2}") : mock(uri, referer, body, token),
            CancellationToken.None) == AuthState.InteractionRequired, "Honor CAPTCHA.");
        Assert(HaustPortal.Authenticate(page, adapter, account, (uri, referer, body, token) =>
            uri.AbsolutePath == "/api/account/login" ? new PortalReply(307, null, "https://other.example/") : mock(uri, referer, body, token),
            CancellationToken.None) == AuthState.Uncertain, "Do not replay the password through redirects.");
        paths.Clear();
        Assert(HaustPortal.Authenticate(new Uri(origin, "/api/r/1"), adapter, account, (uri, referer, body, token) =>
            new PortalReply(302, null, "https://portal.haust.edu.cn.example.com" + page.PathAndQuery), CancellationToken.None) ==
            AuthState.Unavailable, "Do not follow untrusted portal redirects.");
        var routable = HaustPortal.FindPage(new Uri(origin, "/api/r/1"), (uri, referer, body, token) =>
            new PortalReply(302, null, "https://haust.edu.cn" + page.PathAndQuery), CancellationToken.None);
        Assert(routable != null && routable.Host == origin.Host, "Preserve the gateway route when it advertises the school TLS name.");
        var online = HaustPortal.Authenticate(page, adapter, account, (uri, referer, body, token) =>
        {
            Assert(body == null, "Do not submit credentials if already online.");
            return uri.AbsolutePath == "/api/csrf-token" ? new PortalReply(200, "{\"csrf_token\":\"fake\"}") :
                new PortalReply(200, "{\"code\":0}");
        }, CancellationToken.None);
        Assert(online == AuthState.AlreadyOnline, "Skip login when already online.");
        var ipOnly = new Uri(origin, "/tpl/stu-xy/login.html?ip=10.122.1.2&nasId=4");
        Assert(HaustPortal.Authenticate(ipOnly, adapter, account, (uri, referer, body, token) =>
        {
            if (uri.AbsolutePath == "/api/csrf-token") return new PortalReply(200, "{\"csrf_token\":\"fake\"}");
            var fields = body ?? uri.Query;
            Assert(!fields.Contains("userMac=") && !fields.Contains("switchip="), "Leave missing device fields for the gateway to resolve.");
            return body == null ? new PortalReply(200, "{\"code\":1}") : new PortalReply(200, "{\"code\":0}");
        }, CancellationToken.None, "02:11:22:33:44:55") == AuthState.Accepted, "Login from a portal URL with only IP and NAS ID.");
        Assert(HaustPortal.Authenticate(page, adapter, account, (uri, referer, body, token) =>
            uri.AbsolutePath == "/api/account/status" ? new PortalReply(200, "{\"code\":4,\"online\":null}") : mock(uri, referer, body, token),
            CancellationToken.None) == AuthState.Accepted, "A numeric status with no online session should allow login.");
        Assert(HaustPortal.Authenticate(page, adapter, account, (uri, referer, body, token) =>
        {
            Assert(body == null, "Do not submit credentials on a malformed status response.");
            return uri.AbsolutePath == "/api/account/status" ? new PortalReply(200, "{\"online\":null}") : mock(uri, referer, body, token);
        }, CancellationToken.None) == AuthState.Unavailable, "Require a recognized status response before login.");

        var attempts = new List<Uri>();
        var changedGateway = new Uri("http://10.100.51.9/splash");
        Assert(HaustPortal.LoginViaDiscovery(changedGateway, entry =>
        {
            attempts.Add(entry);
            return entry == HaustPortal.DefaultEntry ? AuthState.Accepted : AuthState.Unavailable;
        }) == AuthState.Accepted, "Use the default entry after discovery fails.");
        Assert(attempts.Count == 2 && attempts[0] == new Uri("https://10.100.51.9/api/r/default") &&
            attempts[1] == HaustPortal.DefaultEntry, "Try discovery on the new gateway before falling back.");
        Assert(HaustPortal.LoginViaDiscovery(changedGateway, entry =>
        {
            Assert(entry != HaustPortal.DefaultEntry, "Do not try a second gateway after sending credentials.");
            return AuthState.Rejected;
        }) == AuthState.Rejected, "Preserve the first portal's rejection.");
        Assert(HaustPortal.LoginViaDiscovery(new Uri("https://haust.edu.cn.example.com/"), entry =>
        {
            Assert(entry == HaustPortal.DefaultEntry, "Do not use an untrusted interception target.");
            return AuthState.Accepted;
        }) == AuthState.Accepted, "Use the school entry if the detected URL is unsupported.");
    }

    static void Assert(bool value, string behavior)
    {
        if (!value) throw new InvalidOperationException(behavior);
    }

    static void Respond(string code, string type, string body, string location, ProbeState state, string target)
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var probe = new Uri("http://" + server.LocalEndpoint + "/probe");
            var reply = Task.Run(() =>
            {
                using (var client = server.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 4000;
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) { }
                    var message = "HTTP/1.1 " + code + "\r\nContent-Type: " + type +
                        "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" +
                        (location == null ? "" : "Location: " + location + "\r\n") + "\r\n" + body;
                    var bytes = Encoding.ASCII.GetBytes(message);
                    stream.Write(bytes, 0, bytes.Length);
                }
            });
            var result = PortalProbe.Check(probe, new[] { IPAddress.Loopback });
            var expected = target == null ? null : new Uri(probe, target);
            if (result.State != state || !Equals(result.Target, expected))
                throw new InvalidOperationException("Unexpected detection for " + code + " " + type + " " + location);
            reply.GetAwaiter().GetResult();
        }
        finally { server.Stop(); }
    }
}
