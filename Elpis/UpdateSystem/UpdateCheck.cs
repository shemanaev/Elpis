/*
 * Copyright 2012 - Adam Haile
 * http://adamhaile.net
 *
 * This file is part of Elpis.
 * Elpis is free software: you can redistribute it and/or modify 
 * it under the terms of the GNU General Public License as published by 
 * the Free Software Foundation, either version 3 of the License, or 
 * (at your option) any later version.
 * 
 * Elpis is distributed in the hope that it will be useful, 
 * but WITHOUT ANY WARRANTY; without even the implied warranty of 
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the 
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License 
 * along with Elpis. If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Util;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Elpis.UpdateSystem
{
    public class UpdateCheck
    {
        private const string InstallerNameStartsWith = "Elpis_";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/64.0.3282.186 Safari/537.36";

        #region Delegates

        public delegate void UpdateDataLoadedEventHandler(bool foundUpdate);

        public delegate void DownloadProgressHandler(int prog);
        public event DownloadProgressHandler DownloadProgress;
        public delegate void DownloadCompleteHandler(bool error, Exception ex);
        public event DownloadCompleteHandler DownloadComplete;

        #endregion

        private bool _downloadComplete;
        private string _downloadString = string.Empty;

        public Version CurrentVersion => Assembly.GetEntryAssembly().GetName().Version;

        public Version NewVersion { get; set; }
        public string DownloadUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public bool UpdateNeeded { get; set; }
        public string UpdatePath { get; set; }
        public event UpdateDataLoadedEventHandler UpdateDataLoadedEvent;
      

        private void SendUpdateEvent(bool foundUpdate)
        {
            UpdateDataLoadedEvent?.Invoke(foundUpdate);
        }

        private string DownloadString(string url, int timeoutSec = 10)
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", UserAgent);
                wc.DownloadStringCompleted += wc_DownloadStringCompleted;

                _downloadComplete = false;
                _downloadString = string.Empty;
                if (PRequest.Proxy != null)
                    wc.Proxy = PRequest.Proxy;

                wc.DownloadStringAsync(new Uri(url));

                DateTime start = DateTime.Now;
                while (!_downloadComplete && ((DateTime.Now - start).TotalMilliseconds < (timeoutSec * 1000)))
                    Thread.Sleep(25);

                if (_downloadComplete)
                    return _downloadString;

                wc.CancelAsync();

                throw new Exception($"Timeout waiting for {url} to download.");
            }
        }

        private void wc_DownloadStringCompleted(object sender, DownloadStringCompletedEventArgs e)
        {
            try { _downloadString = e.Result; }
            catch { _downloadString = string.Empty; }

            _downloadComplete = true;
        }

        public bool CheckForUpdate(bool beta = false)
        {
            try
            {
                Log.O($"Checking for {(beta ? "beta " : "")}updates...");
                var updateUrl = "";

#if APP_RELEASE
                updateUrl = $"{ReleaseData.UpdateBaseUrl}?r={DateTime.UtcNow.ToEpochTime()}"; //Because WebClient won't let you disable caching :(
#else
                updateUrl = $"http://localhost:9080/test.json?r={DateTime.UtcNow.ToEpochTime()}";
#endif
                Log.O($"Downloading update file: {updateUrl}");

                var data = DownloadString(updateUrl);

                ReleaseNotes = string.Empty;

                var jsonSettings = new JsonSerializerSettings
                {
                    ContractResolver = new DefaultContractResolver
                    {
                        NamingStrategy = new SnakeCaseNamingStrategy()
                    }
                };
                var releases = JsonConvert.DeserializeObject<List<Release>>(data , jsonSettings);
                if (!beta)
                {
                    releases = releases.Where(r => !r.Prerelease).ToList();
                }

                var latest = releases.OrderByDescending(r => r.Name).First();

                DownloadUrl = latest.Assets.First(a => a.Name.StartsWith(InstallerNameStartsWith)).BrowserDownloadUrl;
                ReleaseNotes = latest.Body;
                if (!Version.TryParse(latest.Name, out var ver))
                {
                    SendUpdateEvent(false);
                    return false;
                }

                NewVersion = ver;

                var result = NewVersion > CurrentVersion;

                UpdateNeeded = result;
                SendUpdateEvent(result);
                return result;
            }
            catch (Exception e)
            {
                Log.O("Error checking for updates: {0}", e);
                UpdateNeeded = false;
                SendUpdateEvent(false);
                return false;
            }
        }

        public void CheckForUpdateAsync()
        {
            Task.Factory.StartNew(() => CheckForUpdate());
        }

        public void DownloadUpdateAsync(string outputPath)
        {
            UpdatePath = outputPath;
            Log.O("Download Elpis Update...");
            Task.Factory.StartNew(() => PRequest.FileRequestAsync(DownloadUrl, outputPath, 
                DownloadProgressChanged, DownloadFileCompleted));
        }

        private void DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Error == null)
                Log.O("Update Download Complete.");
            else
                Log.O("Update Download Error: " + e.Error);

            DownloadComplete?.Invoke(e.Error != null, e.Error);
        }

        private void DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            DownloadProgress?.Invoke(e.ProgressPercentage);
        }
    }

    internal class Release
    {
        public string Name { get; set; }
        public string Body { get; set; }
        public bool Prerelease { get; set; }
        public List<Asset> Assets { get; set; }
    }

    internal class Asset
    {
        public string Name { get; set; }
        public string BrowserDownloadUrl { get; set; }
    }
}