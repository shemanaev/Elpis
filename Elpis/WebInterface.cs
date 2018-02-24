/*
 * Copyright 2015 - Alexey Seliverstov
 * email: alexey.seliverstov.dev@gmail.com
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
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Net;
using Kayak;
using Kayak.Http;
using System.Web.Script.Serialization;
using System.IO;

namespace Elpis
{
    class WebInterface
    {
        private IScheduler _scheduler;

        public void StartInterface()
        {
#if DEBUG
            Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
            Debug.AutoFlush = true;
#endif

            _scheduler = KayakScheduler.Factory.Create(new SchedulerDelegate());
            var server = KayakServer.Factory.CreateHttp(new RequestDelegate(), _scheduler);

            using (server.Listen(new IPEndPoint(IPAddress.Any, 35747)))
            {
                // runs scheduler on calling thread. this method will block until
                // someone calls Stop() on the scheduler.
                _scheduler.Start();
            }
        }

        public void StopInterface()
        {
            _scheduler.Stop();
        }

        class SchedulerDelegate : ISchedulerDelegate
        {
            public void OnException(IScheduler scheduler, Exception e)
            {
                Debug.WriteLine("Error on scheduler.");
                e.DebugStackTrace();
            }

            public void OnStop(IScheduler scheduler)
            {

            }
        }

        class RequestDelegate : IHttpRequestDelegate
        {
            private Dictionary<string, IHttpRoute> _routes;

            public RequestDelegate()
            {
                _routes = new Dictionary<string, IHttpRoute>
                {
                    { "/connect",           new ConnectRoute() },
                    { "/next",              new NextRoute() },
                    { "/play",              new PlayRoute() },
                    { "/pause",             new PauseRoute() },
                    { "/toggleplaypause",   new TogglePlayPauseRoute() },
                    { "/like",              new LikeRoute() },
                    { "/dislike",           new DislikeRoute() },
                    { "/currentsong",       new CurrentSongRoute() },
                    { "/albumcover",        new AlbumCoverRoute() },
                    { "/isplaying",         new IsPlayingRoute() },
                    { "/volume",            new VolumeRoute() },
                    { "/",                  new IndexRoute() },
                    { "/assets",            new AssetsRoute() },
                };
            }

            public void OnRequest(HttpRequestHead request, IDataProducer requestBody, IHttpResponseDelegate response)
            {
                var status = "404 Not Found";
                var body = new BufferedProducer($"The resource you requested ('{request.Uri}') could not be found.");

                try
                {
                    foreach (var route in _routes)
                    {
                        if (request.Path == route.Key)
                        {
                            body = route.Value.Execute(request.QueryString);
                            status = "200 OK";
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    status = "500 Internal Server Error";
                    var responseBody = $"The resource you requested ('{request.Uri}') produced an error: {e.Message}";
                    body = new BufferedProducer(responseBody);
                }

                var headers = new HttpResponseHead()
                {
                    Status = status,
                    Headers = new Dictionary<string, string>()
                    {
                        { "Content-Type", body.MimeType },
                        { "Content-Length", body.Size.ToString() },
                    }
                };
                response.OnResponse(headers, body);
            }
        }
    }

    interface IHttpRoute
    {
        BufferedProducer Execute(string query);
    }

    class ConnectRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            return new BufferedProducer("true");
        }
    }

    class NextRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            bool ret = MainWindow.Next();
            var body = ret ? "Successfully skipped." : "You have to wait for 20 seconds to skip again.";
            return new BufferedProducer(body);
        }
    }

    class PlayRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            MainWindow.Play();
            return new BufferedProducer("Playing.");
        }
    }

    class PauseRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            MainWindow.Pause();
            return new BufferedProducer("Paused.");
        }
    }

    class TogglePlayPauseRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            var body = "";
            if (MainWindow._player.Playing)
            {
                body = "Paused.";
            }
            else
            {
                body = "Playing.";
            }
            MainWindow.PlayPauseToggle();

            return new BufferedProducer(body);
        }
    }

    class LikeRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            MainWindow.Like();
            var body = "Like";
            if (MainWindow.GetCurrentSong().Loved)
                body = "Liked";
            return new BufferedProducer(body);
        }
    }

    class DislikeRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            MainWindow.Dislike();
            return new BufferedProducer("Disliked.");
        }
    }

    class CurrentSongRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            var s = MainWindow.GetCurrentSong();
            var body = new JavaScriptSerializer().Serialize(s);
            return new BufferedProducer(body, "application/json");
        }
    }

    class AlbumCoverRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            var s = MainWindow.GetCurrentSong();
            return new BufferedProducer(s.AlbumImage, "image/jpeg");
        }
    }

    class IsPlayingRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            var body = "";
            if (MainWindow._player.Playing)
            {
                body = "yes";
            }
            else
            {
                body = "no";
            }

            return new BufferedProducer(body);
        }
    }

    class VolumeRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            int volume = MainWindow.GetVolume();
            if (!string.IsNullOrEmpty(query))
            {
                int.TryParse(query, out volume);
                if (volume != MainWindow.GetVolume())
                {
                    MainWindow.SetVolume(volume);
                }
            }

            return new BufferedProducer(volume.ToString());
        }
    }

    class IndexRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            var file = RCUtils.GetFile("index.html");
            return new BufferedProducer(file, "text/html; charset=utf-8");
        }
    }

    class AssetsRoute : IHttpRoute
    {
        public BufferedProducer Execute(string query)
        {
            var file = RCUtils.GetFile(query);
            return new BufferedProducer(file, RCUtils.GetMimeByExtension(query));
        }
    }

    internal static class RCUtils
    {
        public const string DEFAULT_MIME = "text/plain; charset=utf-8";
        private static Dictionary<string, string> _mime = new Dictionary<string, string>
        {
            { "htm",  "text/html; charset=utf-8" },
            { "html", "text/html; charset=utf-8" },
            { "css",  "text/css" },
            { "js",   "application/javascript" },
            { "jpg",  "image/jpeg" },
            { "png",  "image/png" },
            { "svg",  "image/svg+xml" },
        };

        public static byte[] GetFile(string path)
        {
            var file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteControl", path);
            if (!File.Exists(file))
                throw new Exception($"File '{path}' not found.");
            return File.ReadAllBytes(file);
        }

        public static string GetMimeByExtension(string path)
        {
            var ext = Path.GetExtension(path).Substring(1).ToLower();
            string mime = DEFAULT_MIME;
            _mime.TryGetValue(ext, out mime);

            return mime;
        }
    }

    class BufferedProducer : IDataProducer
    {
        ArraySegment<byte> data;
        string mime;

        public BufferedProducer(string data, string mime = RCUtils.DEFAULT_MIME) : this(data, Encoding.UTF8, mime) { }
        public BufferedProducer(string data, Encoding encoding, string mime = RCUtils.DEFAULT_MIME) : this(encoding.GetBytes(data), mime) { }
        public BufferedProducer(byte[] data, string mime = RCUtils.DEFAULT_MIME) : this(new ArraySegment<byte>(data), mime) { }
        public BufferedProducer(ArraySegment<byte> data, string mime = RCUtils.DEFAULT_MIME)
        {
            this.data = data;
            this.mime = mime;
        }

        public IDisposable Connect(IDataConsumer channel)
        {
            // null continuation, consumer must swallow the data immediately.
            channel.OnData(data, null);
            channel.OnEnd();
            return null;
        }

        public int Size => data.Count;
        public string MimeType => mime;
    }
}
