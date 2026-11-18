using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;

enum ProbeState { Offline, Online, Portal }

sealed class ProbeResult
{
    public readonly ProbeState State;
    public readonly Uri Target;

    public ProbeResult(ProbeState state, Uri target = null)
    {
        State = state;
        Target = target;
    }
}

static class PortalProbe
{
    public static readonly Uri Address = new Uri("http://www.msftconnecttest.com/connecttest.txt");
    const string Expected = "Microsoft Connect Test";

    // A null address list is used only to check whether another network already provides access.
    public static ProbeResult Check(Uri address, IPAddress[] localAddresses)
    {
        var offline = new ProbeResult(ProbeState.Offline);
        if (localAddresses != null && localAddresses.Length == 0) return offline;
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.Proxy = null;
            request.KeepAlive = false;
            request.AllowAutoRedirect = false;
            request.Timeout = request.ReadWriteTimeout = 4000;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            var point = request.ServicePoint;
            point.BindIPEndPointDelegate = localAddresses == null ? (BindIPEndPoint)null :
                (servicePoint, remote, retry) =>
                {
                    var local = localAddresses.FirstOrDefault(ip => ip.AddressFamily == remote.AddressFamily);
                    if (local == null || retry > 0) throw new SocketException((int)SocketError.AddressNotAvailable);
                    return new IPEndPoint(local, 0);
                };
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                    return Read(address, response);
            }
            catch (WebException error)
            {
                using (var response = error.Response as HttpWebResponse)
                    return response == null ? offline : Read(address, response);
            }
            finally { point.BindIPEndPointDelegate = null; }
        }
        catch (IOException) { return offline; }
        catch (SocketException) { return offline; }
    }

    static ProbeResult Read(Uri address, HttpWebResponse response)
    {
        int code = (int)response.StatusCode;
        if (code == 302 || code == 303 || code == 301 || code == 307 || code == 308)
        {
            string location = response.Headers[HttpResponseHeader.Location];
            Uri target;
            if (!string.IsNullOrWhiteSpace(location) && Uri.TryCreate(address, location, out target) &&
                (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) &&
                target.UserInfo.Length == 0 && !address.Equals(target))
                return new ProbeResult(ProbeState.Portal, target);
        }
        if (code == 511) return new ProbeResult(ProbeState.Portal, address);
        if (code == 200)
        {
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var buffer = new char[Expected.Length + 1];
                int count = reader.ReadBlock(buffer, 0, buffer.Length);
                if (new string(buffer, 0, count) == Expected) return new ProbeResult(ProbeState.Online);
            }
            if (response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                return new ProbeResult(ProbeState.Portal, address);
        }
        return new ProbeResult(ProbeState.Offline);
    }
}
