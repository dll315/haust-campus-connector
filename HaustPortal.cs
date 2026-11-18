using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Web.Script.Serialization;

enum AuthState { Unavailable, AlreadyOnline, Accepted, Rejected, InteractionRequired, Uncertain }

sealed class PortalReply
{
    public readonly int Status;
    public readonly string Body, Location;

    public PortalReply(int status, string body, string location = null)
    {
        Status = status;
        Body = body;
        Location = location;
    }
}

sealed class PortalSession : IDisposable
{
    readonly IPAddress[] local;
    readonly CancellationToken cancellation;
    readonly CookieContainer cookies = new CookieContainer();
    readonly string group = Guid.NewGuid().ToString("N");
    readonly HashSet<ServicePoint> points = new HashSet<ServicePoint>();

    public PortalSession(IPAddress[] local, CancellationToken cancellation)
    {
        this.local = local;
        this.cancellation = cancellation;
    }

    public PortalReply Request(Uri uri, Uri referer, string body, string token)
    {
        cancellation.ThrowIfCancellationRequested();
        if (uri.Scheme != Uri.UriSchemeHttps || HaustPortal.Candidate(uri) == null)
            return new PortalReply(0, null);
        var request = (HttpWebRequest)WebRequest.Create(uri);
        IPAddress address;
        // The captive gateway advertises an IP but presents the university's DNS certificate.
        // This sets the expected TLS name, while the URI still selects the discovered gateway.
        if (IPAddress.TryParse(uri.Host, out address)) request.Host = "haust.edu.cn";
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.KeepAlive = true;
        request.Timeout = request.ReadWriteTimeout = 4000;
        request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
        request.ConnectionGroupName = group;
        request.CookieContainer = cookies;
        request.Accept = "application/json, */*";
        request.Headers["X-Requested-With"] = "XMLHttpRequest";
        if (referer != null) request.Referer = new UriBuilder(referer) { Host = request.Host }.Uri.AbsoluteUri;
        if (token != null) request.Headers["X-CSRF-Token"] = token;
        var point = request.ServicePoint;
        points.Add(point);
        point.BindIPEndPointDelegate = (unused, remote, retry) =>
        {
            var source = local.FirstOrDefault(ip => ip.AddressFamily == remote.AddressFamily);
            if (source == null || retry > 0) throw new SocketException((int)SocketError.AddressNotAvailable);
            return new IPEndPoint(source, 0);
        };
        try
        {
            using (cancellation.Register(request.Abort))
            {
                if (body != null)
                {
                    request.Method = "POST";
                    request.AllowWriteStreamBuffering = false;
                    request.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
                    request.Headers["Origin"] = "https://" + request.Host;
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    try
                    {
                        request.ContentLength = bytes.Length;
                        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                    }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    string payload = null;
                    if (response.StatusCode == HttpStatusCode.OK &&
                        response.ContentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        using (var stream = response.GetResponseStream())
                        using (var reader = new StreamReader(stream))
                        {
                            var buffer = new char[32769];
                            int length = reader.ReadBlock(buffer, 0, buffer.Length);
                            if (length < buffer.Length) payload = new string(buffer, 0, length);
                        }
                    }
                    return new PortalReply((int)response.StatusCode, payload, response.Headers["Location"]);
                }
            }
        }
        catch (WebException error)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var response = error.Response as HttpWebResponse)
                return new PortalReply(response == null ? 0 : (int)response.StatusCode, null);
        }
        catch (IOException)
        {
            cancellation.ThrowIfCancellationRequested();
            return new PortalReply(0, null);
        }
        catch (SocketException) { return new PortalReply(0, null); }
    }

    public void Dispose()
    {
        foreach (var point in points)
        {
            point.CloseConnectionGroup(group);
            point.BindIPEndPointDelegate = null;
        }
    }
}

static class HaustPortal
{
    public static readonly Uri DefaultEntry = new Uri("https://10.100.51.1/api/r/default");
    static readonly Regex LoginPath = new Regex(@"^/tpl/[a-zA-Z0-9_-]{1,60}/login(_account)?\.html$");
    static readonly Regex MacFormat = new Regex(@"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$");

    internal static Uri Candidate(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        IPAddress ip;
        if (IPAddress.TryParse(uri.Host, out ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return null;
            var b = ip.GetAddressBytes();
            if (!(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                (b[0] == 192 && b[1] == 168))) return null;
        }
        else if (!uri.Host.Equals("haust.edu.cn", StringComparison.OrdinalIgnoreCase)) return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1, Fragment = "" }.Uri;
    }

    public static AuthState Login(Uri observed, IPAddress[] local, string adapterMac, CampusAccount account, CancellationToken cancellation)
    {
        var entry = observed == null || observed == PortalProbe.Address ? DefaultEntry : Candidate(observed);
        if (entry == null || !local.Any(ip => ip.AddressFamily == AddressFamily.InterNetwork)) return AuthState.Unavailable;
        using (var session = new PortalSession(local, cancellation))
            return Authenticate(entry, local, account, session.Request, cancellation, adapterMac);
    }

    public static bool VerifyBrowserPortal(Uri observed, IPAddress[] local, CancellationToken cancellation)
    {
        var entry = Candidate(observed);
        if (entry == null || entry.Host != "haust.edu.cn" || local.Length == 0) return false;
        using (var session = new PortalSession(local, cancellation))
        {
            var page = FindPage(entry, session.Request, cancellation);
            return page != null && page.Host == entry.Host &&
                ReadToken(session.Request(new Uri(page, "/api/csrf-token"), page, null, null)) != null;
        }
    }

    internal static Uri FindPage(Uri entry, Func<Uri, Uri, string, string, PortalReply> send, CancellationToken cancellation)
    {
        var page = Candidate(entry);
        if (page != null && page.AbsolutePath == "/") page = new Uri(page, "/api/r/default" + page.Query);
        var visited = new HashSet<Uri>();
        while (page != null && visited.Count < 6 && visited.Add(page))
        {
            cancellation.ThrowIfCancellationRequested();
            if (LoginPath.IsMatch(page.AbsolutePath))
                return new Uri(page, page.AbsolutePath.Replace("/login.html", "/login_account.html") + page.Query);
            if (!page.AbsolutePath.StartsWith("/api/r/", StringComparison.Ordinal)) break;
            var response = send(page, null, null, null);
            Uri redirected;
            if (response.Status < 300 || response.Status >= 400 ||
                string.IsNullOrWhiteSpace(response.Location) || !Uri.TryCreate(page, response.Location, out redirected)) break;
            var next = Candidate(redirected);
            IPAddress gateway;
            // The authenticated gateway may generate an absolute URL from our TLS host name.
            // Continue using the already discovered route when that name is absent from captive DNS.
            if (next != null && next.Host == "haust.edu.cn" && IPAddress.TryParse(page.Host, out gateway))
                next = new UriBuilder(next) { Host = page.Host }.Uri;
            page = next;
        }
        return null;
    }

    internal static AuthState Authenticate(Uri entry, IPAddress[] local, CampusAccount account,
        Func<Uri, Uri, string, string, PortalReply> send, CancellationToken cancellation, string adapterMac = null)
    {
        var page = FindPage(entry, send, cancellation);
        if (page == null) return AuthState.Unavailable;
        var query = HttpUtility.ParseQueryString(page.Query);
        IPAddress ip;
        int nas;
        string mac = string.IsNullOrWhiteSpace(query["mac"]) ? adapterMac : query["mac"];
        if (!IPAddress.TryParse(query["wlanuserip"] ?? query["ip"], out ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork || !local.Contains(ip) ||
            !int.TryParse(query["nasId"], NumberStyles.None, CultureInfo.InvariantCulture, out nas) || nas <= 0 ||
            mac == null || !MacFormat.IsMatch(mac) ||
            (adapterMac != null && !string.Equals(mac, adapterMac, StringComparison.OrdinalIgnoreCase)))
            return AuthState.Unavailable;

        IPAddress switchAddress;
        var switchIp = IPAddress.TryParse(query["switchip"], out switchAddress) ? switchAddress.ToString() : "";
        var device = "switchip=" + Encode(switchIp) + "&nasId=" + nas.ToString(CultureInfo.InvariantCulture) +
            "&userIpv4=" + Encode(ip.ToString()) + "&userMac=" + Encode(mac) + "&captcha=&captchaId=";
        var token = ReadToken(send(new Uri(page, "/api/csrf-token"), page, null, null));
        if (token == null) return AuthState.Unavailable;
        var status = Json(send(new Uri(page, "/api/account/status?username=&password=&" + device + "&isp=&timeLimit="), page, null, token));
        int state = Number(status, "code");
        if (state == 0) return AuthState.AlreadyOnline;
        if (state != 1) return AuthState.Unavailable;

        cancellation.ThrowIfCancellationRequested();
        var form = "username=" + Encode(account.User) + "&password=" + Encode(account.Password) + "&" + device +
            "&isp=" + Encode(account.Isp) + "&timeLimit=";
        var check = Json(send(new Uri(page, "/api/account/check"), page, form, token));
        int checkCode = Number(check, "code");
        if (checkCode == 1) return AuthState.Rejected;
        if (checkCode == 2 || Number(check, "isChangePwd") == 1) return AuthState.InteractionRequired;
        if (checkCode != 0) return AuthState.Uncertain;
        cancellation.ThrowIfCancellationRequested();
        var login = Json(send(new Uri(page, "/api/account/login"), page, form, token));
        switch (Number(login, "code"))
        {
            case 0: return AuthState.Accepted;
            case 1: return AuthState.Rejected;
            case 2: return AuthState.InteractionRequired;
            default: return AuthState.Uncertain;
        }
    }

    static string Encode(string value) { return Uri.EscapeDataString(value); }

    static string ReadToken(PortalReply reply)
    {
        object value;
        var token = Json(reply).TryGetValue("csrf_token", out value) ? value as string : null;
        return string.IsNullOrWhiteSpace(token) || token.Length > 2048 || token.Any(char.IsControl) ? null : token;
    }

    static Dictionary<string, object> Json(PortalReply reply)
    {
        if (reply.Status == 200 && reply.Body != null)
        {
            try
            {
                var value = new JavaScriptSerializer { MaxJsonLength = 32768, RecursionLimit = 24 }
                    .Deserialize<Dictionary<string, object>>(reply.Body);
                if (value != null) return value;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return new Dictionary<string, object>();
    }

    static int Number(Dictionary<string, object> reply, string key)
    {
        object value;
        int number;
        if (!reply.TryGetValue(key, out value) || value == null) return -1;
        if (value is bool && key == "isChangePwd") return (bool)value ? 1 : 0;
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number) ? number : -1;
    }
}
