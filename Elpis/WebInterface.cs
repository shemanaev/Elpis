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
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Net;
using Kayak;
using Kayak.Http;
using System.Web.Script.Serialization;

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
                    { "/volume",            new VolumeRoute() },
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

    class BufferedProducer : IDataProducer
    {
        ArraySegment<byte> data;
        string mime;

        public BufferedProducer(string data, string mime = "text/plain") : this(data, Encoding.UTF8, mime) { }
        public BufferedProducer(string data, Encoding encoding, string mime = "text/plain") : this(encoding.GetBytes(data), mime) { }
        public BufferedProducer(byte[] data, string mime = "text/plain") : this(new ArraySegment<byte>(data), mime) { }
        public BufferedProducer(ArraySegment<byte> data, string mime = "text/plain")
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

    class BufferedConsumer : IDataConsumer
    {
        List<ArraySegment<byte>> buffer = new List<ArraySegment<byte>>();
        Action<string> resultCallback;
        Action<Exception> errorCallback;

        public BufferedConsumer(Action<string> resultCallback, Action<Exception> errorCallback)
        {
            this.resultCallback = resultCallback;
            this.errorCallback = errorCallback;
        }

        public bool OnData(ArraySegment<byte> data, Action continuation)
        {
            // since we're just buffering, ignore the continuation.
            // TODO: place an upper limit on the size of the buffer.
            // don't want a client to take up all the RAM on our server!
            buffer.Add(data);
            return false;
        }

        public void OnError(Exception error)
        {
            errorCallback(error);
        }

        public void OnEnd()
        {
            // turn the buffer into a string.
            //
            // (if this isn't what you want, you could skip
            // this step and make the result callback accept
            // List<ArraySegment<byte>> or whatever)
            //
            var str = "";
            if (buffer.Count > 0)
            {
                str = buffer
                .Select(b => Encoding.UTF8.GetString(b.Array, b.Offset, b.Count))
                .Aggregate((result, next) => result + next);
            }

            resultCallback(str);
        }
    }
}
