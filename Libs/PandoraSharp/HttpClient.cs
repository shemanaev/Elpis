using DNS.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Util;

namespace PandoraSharp
{
    public class HttpClient
    {
        private static WebProxy _proxy;
        private static string _userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/64.0.3282.186 Safari/537.36";

        private static string _dns;
        private static Dictionary<string, string> _dnsCache = new Dictionary<string, string>();
        public static string Dns
        {
            get { return _dns; }
            set
            {
                if (IPAddress.TryParse(value, out IPAddress ip))
                    _dns = value;
                else
                    _dns = string.Empty;
                _dnsCache.Clear();
            }
        }

        public static WebProxy Proxy { get { return _proxy; } }

        public static void SetProxy(string address, string user = "", string password = "")
        {
            var p = new WebProxy(new Uri(address));

            if (user != "")
                p.Credentials = new NetworkCredential(user, password);

            _proxy = p;
        }

        public static void SetProxy(string address, int port, string user = "", string password = "")
        {
            ServicePointManager.Expect100Continue = false;
            var p = new WebProxy(address, port);

            if (user != "")
                p.Credentials = new NetworkCredential(user, password);

            _proxy = p;
        }

        public static string StringRequest(string url, string data)
        {
            var wc = new WebClient();
            if (_proxy != null)
                wc.Proxy = _proxy;

            wc.Encoding = System.Text.Encoding.UTF8;
            wc.Headers.Add("Content-Type", "text/plain; charset=utf8");
            wc.Headers.Add("User-Agent", _userAgent);

            var uri = new Uri(url);

            if (!string.IsNullOrEmpty(_dns))
            {
                var host = uri.Host;
                if (!_dnsCache.ContainsKey(host))
                {
                    var dns = new DnsClient(_dns);
                    var resolved = dns.Lookup(host);
                    resolved.Wait();
                    _dnsCache[host] = resolved.Result.FirstOrDefault().ToString();
                }

                uri = uri.ReplaceHost(_dnsCache[host]);
                wc.Headers.Add("Host", host);
            }

            string response = string.Empty;
            try
            {
                response = wc.UploadString(uri, "POST", data);
            }
            catch (WebException wex)
            {
                Log.O("StringRequest Error: " + wex.ToString());
                //Wait and Try again, just in case
                Thread.Sleep(500);
                response = wc.UploadString(uri, "POST", data);
            }

            return response;
        }

        public static byte[] ByteRequest(string url)
        {
            Log.O("Downloading: " + url);
            var wc = new WebClient();
            if (_proxy != null)
                wc.Proxy = _proxy;

            var uri = new Uri(url);

            if (!string.IsNullOrEmpty(_dns))
            {
                var host = uri.Host;
                if (!_dnsCache.ContainsKey(host))
                {
                    var dns = new DnsClient(_dns);
                    var resolved = dns.Lookup(host);
                    resolved.Wait();
                    _dnsCache[host] = resolved.Result.FirstOrDefault().ToString();
                }

                uri = uri.ReplaceHost(_dnsCache[host]);
                wc.Headers.Add("Host", host);
            }

            return wc.DownloadData(uri);
        }
    }
}
