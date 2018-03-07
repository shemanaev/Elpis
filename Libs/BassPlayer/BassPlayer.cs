/*
 * Copyright 2012 - Adam Haile / Media Portal
 * http://adamhaile.net
 *
 * This file is part of BassPlayer.
 * BassPlayer is free software: you can redistribute it and/or modify 
 * it under the terms of the GNU General Public License as published by 
 * the Free Software Foundation, either version 3 of the License, or 
 * (at your option) any later version.
 * 
 * BassPlayer is distributed in the hope that it will be useful, 
 * but WITHOUT ANY WARRANTY; without even the implied warranty of 
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the 
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License 
 * along with BassPlayer. If not, see http://www.gnu.org/licenses/.
 * 
 * Note: Below is a heavily modified version of BassAudio.cs from
 * http://sources.team-mediaportal.com/websvn/filedetails.php?repname=MediaPortal&path=%2Ftrunk%2Fmediaportal%2FCore%2FMusicPlayer%2FBASS%2FBassAudio.cs
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;

namespace BassPlayer
{
    public class BassException : Exception
    {
        public BassException()
        {
        }

        public BassException(string msg) : base(msg)
        {
        }
    }

    public class BassStreamException : Exception
    {
        public BassStreamException()
        {
        }

        public BassStreamException(string msg) : base(msg)
        {
        }

        public BassStreamException(string msg, Errors error) : base(msg)
        {
            ErrorCode = error;
        }

        public Errors ErrorCode { get; set; }
    }

    /// <summary>
    /// Handles playback of Audio files and Internet streams via the BASS Audio Engine.
    /// </summary>
    public class BassAudioEngine : IDisposable // : IPlayer
    {
        #region Enums

        /// <summary>
        /// The various States for Playback
        /// </summary>
        public enum PlayState
        {
            Init,
            Playing,
            Paused,
            Ended,
            Stopped
        }

        #region Nested type: PlayBackType

        /// <summary>
        /// States, how the Playback is handled
        /// </summary>
        private enum PlayBackType
        {
            NORMAL = 0,
            GAPLESS = 1,
            CROSSFADE = 2
        }

        #endregion

        #region Nested type: Progress

        public class Progress
        {
            public TimeSpan TotalTime { get; set; }
            public TimeSpan ElapsedTime { get; set; }

            public TimeSpan RemainingTime => TotalTime - ElapsedTime;

            public double Percent
            {
                get
                {
                    if (TotalTime.Ticks == 0)
                        return 0.0;

                    return ((ElapsedTime.TotalSeconds/TotalTime.TotalSeconds)*100);
                }
            }
        }

        #endregion

        #endregion

        #region Delegates

        public delegate void CrossFadeHandler(object sender, string filePath);

        public delegate void DownloadCanceledHandler(object sender, string downloadFile);

        public delegate void DownloadCompleteHandler(object sender, string downloadFile);

        public delegate void InternetStreamSongChangedHandler(object sender);

        public delegate void PlaybackProgressHandler(object sender, Progress prog);

        public delegate void PlaybackStartHandler(object sender, double duration);

        public delegate void PlaybackStateChangedHandler(object sender, PlayState oldState, PlayState newState);

        public delegate void PlaybackStopHandler(object sender);

        public delegate void TrackPlaybackCompletedHandler(object sender, string filePath);

        #endregion

        private DownloadProcedure DownloadProcDelegate;
        private SyncProcedure MetaTagSyncProcDelegate;
        private SyncProcedure PlaybackEndProcDelegate;
        private SyncProcedure PlaybackFadeOutProcDelegate;
        private SyncProcedure PlaybackStreamFreedProcDelegate;

        #region Variables

        private const int MAXSTREAMS = 1;
        private readonly List<int> DecoderPluginHandles = new List<int>();
        private readonly List<List<int>> StreamEventSyncHandles = new List<List<int>>();
        private readonly List<int> Streams = new List<int>(MAXSTREAMS);
        private readonly System.Timers.Timer UpdateTimer = new System.Timers.Timer();

        private int CurrentStreamIndex;

        private string FilePath = string.Empty;
        private bool NeedUpdate = true;
        private bool NotifyPlaying = true;
        private bool _BassFreed;
        private int _BufferingMS = 5000;

        private int _CrossFadeIntervalMS = 4000;
        private bool _CrossFading; // true if crossfading has started
        private bool _Initialized;

        private bool _SoftStop = true;
        private string _SoundDevice = "";

        private PlayState _State = PlayState.Init;
        private int _StreamVolume = 100;

        private string _downloadFile = string.Empty;
        private bool _downloadFileComplete;
        private FileStream _downloadStream;
        private GainDsp _gain;

        private bool _isRadio;
        private int _mixer;
        private int _playBackType;
        private int _progUpdateInterval = 500; //update every 500 ms
        private int _speed = 1;
        //private TAG_INFO _tagInfo;

        #endregion

        #region Properties

        public string SoundDevice
        {
            get => _SoundDevice;
            set => ChangeOutputDevice(value);
        }

        /// <summary>
        /// Returns, if the player is in initialising stage
        /// </summary>
        public bool Initializing => (_State == PlayState.Init);

        /// <summary>
        /// Returns the Duration of an Audio Stream
        /// </summary>
        public double Duration
        {
            get
            {
                int stream = GetCurrentStream();

                if (stream == 0)
                {
                    return 0;
                }

                double duration = GetTotalStreamSeconds(stream);

                return duration;
            }
        }

        /// <summary>
        /// Returns the Current Position in the Stream
        /// </summary>
        public double CurrentPosition
        {
            get
            {
                int stream = GetCurrentStream();

                if (stream == 0)
                {
                    return 0;
                }

                long pos = Bass.ChannelGetPosition(stream); // position in bytes

                double curPosition = Bass.ChannelBytes2Seconds(stream, pos); // the elapsed time length

                return curPosition;
            }
        }

        public int ProgressUpdateInterval
        {
            get => _progUpdateInterval;
            set
            {
                _progUpdateInterval = value;
                UpdateTimer.Interval = _progUpdateInterval;
            }
        }

        /// <summary>
        /// Returns the Current Play State
        /// </summary>
        public PlayState State => _State;

        /// <summary>
        /// Has the Playback Ended?
        /// </summary>
        public bool Ended => _State == PlayState.Ended;

        /// <summary>
        /// Is Playback Paused?
        /// </summary>
        public bool Paused => (_State == PlayState.Paused);

        /// <summary>
        /// Is the Player Playing?
        /// </summary>
        public bool Playing => (_State == PlayState.Playing || _State == PlayState.Paused);

        /// <summary>
        /// Is Player Stopped?
        /// </summary>
        public bool Stopped => (_State == PlayState.Init || _State == PlayState.Stopped);

        /// <summary>
        /// Returns the File, currently played
        /// </summary>
        public string CurrentFile => FilePath;

        /// <summary>
        /// Gets/Sets the Playback Volume
        /// </summary>
        public int Volume
        {
            get => _StreamVolume;
            set
            {
                if (_StreamVolume != value)
                {
                    if (value > 100)
                    {
                        value = 100;
                    }

                    if (value < 0)
                    {
                        value = 0;
                    }

                    _StreamVolume = value;
                    _StreamVolume = value;
                    Bass.GlobalStreamVolume = _StreamVolume * 100;
                }
            }
        }

        /// <summary>
        /// Returns the Playback Speed
        /// </summary>
        public int Speed
        {
            get => _speed;
            set => _speed = value;
        }

        /// <summary>
        /// Gets/Sets the Crossfading Interval
        /// </summary>
        public int CrossFadeIntervalMS
        {
            get => _CrossFadeIntervalMS;
            set => _CrossFadeIntervalMS = value;
        }

        /// <summary>
        /// Gets/Sets the Buffering of BASS Streams
        /// </summary>
        public int BufferingMS
        {
            get => _BufferingMS;
            set
            {
                if (_BufferingMS == value)
                {
                    return;
                }

                _BufferingMS = value;
                Bass.PlaybackBufferLength = _BufferingMS;
            }
        }

        /// <summary>
        /// Returns the instance of the Visualisation Manager
        /// </summary>
        /// 
        //R//
        //public IVisualizationManager IVizManager
        //{
        //  get { return VizManager; }
        //}
        public bool IsRadio => _isRadio;

        /// <summary>
        /// Returns the Playback Type
        /// </summary>
        public int PlaybackType => _playBackType;

        /// <summary>
        /// Returns the instance of the Video Window
        /// </summary>
        //R//public VisualizationWindow VisualizationWindow
        //{
        //  get { return VizWindow; }
        //}
        /// <summary>
        /// Is the Audio Engine initialised
        /// </summary>
        public bool Initialized => _Initialized;

        /// <summary>
        /// Is Crossfading enabled
        /// </summary>
        public bool CrossFading => _CrossFading;

        /// <summary>
        /// Is Crossfading enabled
        /// </summary>
        public bool CrossFadingEnabled => _CrossFadeIntervalMS > 0;

        /// <summary>
        /// Is BASS freed?
        /// </summary>
        public bool BassFreed => _BassFreed;

        /// <summary>
        /// Returns the Stream, currently played
        /// </summary>
        public int CurrentAudioStream => GetCurrentVizStream();

        #endregion

        #region Constructors/Destructors

        public BassAudioEngine(string email = "", string key = "")
        {
            Initialize();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Release the Player
        /// </summary>
        public void Dispose()
        {
            if (!Stopped) // Check if stopped already to avoid that Stop() is called two or three times
            {
                Stop(true);
            }
        }

        /// <summary>
        /// Dispose the BASS Audio engine. Free all BASS and Visualisation related resources
        /// </summary>
        public void DisposeAndCleanUp()
        {
            Dispose();
            // Clean up BASS Resources
            if (_mixer != 0)
            {
                Bass.ChannelStop(_mixer);
            }

            Bass.Stop();
            Bass.Free();

            foreach (var stream in Streams)
            {
                FreeStream(stream);
            }

            foreach (var pluginHandle in DecoderPluginHandles)
            {
                Bass.PluginFree(pluginHandle);
            }
        }

        /// <summary>
        /// The BASS engine itself is not initialised at this stage, since it may cause S/PDIF for Movies not working on some systems.
        /// </summary>
        private void Initialize()
        {
            try
            {
                Log.Info("BASS: Initialize BASS environment ...");
                LoadSettings();

                // Set the Global Volume. 0 = silent, 10000 = Full
                // We get 0 - 100 from Configuration, so multiply by 100
                Bass.GlobalStreamVolume = _StreamVolume*100;

                Bass.PlaybackBufferLength = _BufferingMS;

                for (int i = 0; i < MAXSTREAMS; i++)
                {
                    Streams.Add(0);
                }

                PlaybackFadeOutProcDelegate = PlaybackFadeOutProc;
                PlaybackEndProcDelegate = PlaybackEndProc;
                PlaybackStreamFreedProcDelegate = PlaybackStreamFreedProc;
                //MetaTagSyncProcDelegate = MetaTagSyncProc;

                DownloadProcDelegate = DownloadProc;


                StreamEventSyncHandles.Add(new List<int>());
                StreamEventSyncHandles.Add(new List<int>());

                LoadAudioDecoderPlugins();

                Log.Info("BASS: Initializing BASS environment done.");

                _Initialized = true;
                _BassFreed = true;
            }

            catch (Exception ex)
            {
                Log.Error("BASS: Initialize thread failed.  Reason: {0}", ex.Message);
                throw new BassException("BASS: Initialize thread failed.  Reason: " + ex);
            }
        }

        /// <summary>
        /// Free BASS, when not playing Audio content, as it might cause S/PDIF output stop working
        /// </summary>
        public void FreeBass()
        {
            if (!_BassFreed)
            {
                Log.Info("BASS: Freeing BASS. Non-audio media playback requested.");

                if (_mixer != 0)
                {
                    Bass.ChannelStop(_mixer);
                    _mixer = 0;
                }

                Bass.Free();
                _BassFreed = true;
            }
        }

        /// <summary>
        /// Init BASS, when a Audio file is to be played
        /// </summary>
        public void InitBass()
        {
            try
            {
                Log.Info("BASS: Initializing BASS audio engine...");
                bool initOK = false;

                Bass.IncludeDefaultDevice = true; //Allows following Default device (Win 7 Only)
                int soundDevice = GetSoundDevice();

                initOK = Bass.Init(soundDevice, 44100, DeviceInitFlags.Default | DeviceInitFlags.Latency);
                if (initOK)
                {
                    UpdateTimer.AutoReset = true;
                    UpdateTimer.Interval = _progUpdateInterval;
                    UpdateTimer.Elapsed += OnUpdateTimerTick;

                    Log.Info("BASS: Initialization done.");
                    _Initialized = true;
                    _BassFreed = false;
                }
                else
                {
                    var error = Bass.LastError;
                    Log.Error("BASS: Error initializing BASS audio engine {0}",
                              Enum.GetName(typeof (Errors), error));
                    throw new Exception("Init Error: " + error);
                }
            }
            catch (Exception ex)
            {
                Log.Error("BASS: Initialize failed. Reason: {0}", ex.Message);
                throw new BassException("BASS: Initialize failed. Reason: }" + ex.Message);
            }
        }

        public void SetProxy(string address, int port, string user = "", string password = "")
        {
            var proxy = $"{address}:{port}";
            if(user != "")
                proxy = $"{user}:{password}@{proxy}";

            Bass.NetProxy = proxy;
        }

        /// <summary>
        /// Get the Sound devive as set in the Configuartion
        /// </summary>
        /// <returns></returns>
        private int GetSoundDevice()
        {
            int sounddevice = -1;
            // Check if the specified Sounddevice still exists
            if (_SoundDevice == "Default")
            {
                Log.Info("BASS: Using default Sound Device");
                sounddevice = -1;
            }
            else
            {
                var foundDevice = false;
                for (var i = 0; i < Bass.DeviceCount; i++)
                {
                    if (Bass.GetDeviceInfo(i, out var device) && device.Name == _SoundDevice)
                    {
                        foundDevice = true;
                        sounddevice = i;
                        break;
                    }
                }

                if (!foundDevice)
                {
                    Log.Warn("BASS: specified Sound device does not exist. Using default Sound Device");
                    sounddevice = -1;
                }
                else
                {
                    Log.Info("BASS: Using Sound Device {0}", _SoundDevice);
                }
            }
            return sounddevice;
        }

        /// <summary>
        /// Load Settings 
        /// </summary>
        private void LoadSettings()
        {
            _SoundDevice = "Default";

            _StreamVolume = 100;
            _BufferingMS = 5000;

            if (_BufferingMS <= 0)
            {
                _BufferingMS = 1000;
            }

            else if (_BufferingMS > 8000)
            {
                _BufferingMS = 8000;
            }

            _CrossFadeIntervalMS = 0;

            if (_CrossFadeIntervalMS < 0)
            {
                _CrossFadeIntervalMS = 0;
            }

            else if (_CrossFadeIntervalMS > 16000)
            {
                _CrossFadeIntervalMS = 16000;
            }

            _SoftStop = true;

            if (_CrossFadeIntervalMS == 0)
            {
                _playBackType = (int) PlayBackType.NORMAL;
                _CrossFadeIntervalMS = 100;
            }
            else
            {
                _playBackType = (int) PlayBackType.CROSSFADE;
            }
        }

        /// <summary>
        /// Return the BASS Stream to be used for Visualisation purposes.
        /// We will extract the WAVE and FFT data to be provided to the Visualisation Plugins
        /// In case of Mixer active, we need to return the Mixer Stream. 
        /// In all other cases the current actove stream is used.
        /// </summary>
        /// <returns></returns>
        internal int GetCurrentVizStream()
        {
            if (Streams.Count == 0)
            {
                return -1;
            }

            return GetCurrentStream();
        }

        /// <summary>
        /// Returns the Current Stream 
        /// </summary>
        /// <returns></returns>
        internal int GetCurrentStream()
        {
            if (Streams.Count == 0)
            {
                return -1;
            }

            if (CurrentStreamIndex < 0)
            {
                CurrentStreamIndex = 0;
            }

            else if (CurrentStreamIndex >= Streams.Count)
            {
                CurrentStreamIndex = Streams.Count - 1;
            }

            return Streams[CurrentStreamIndex];
        }

        /// <summary>
        /// Returns the Next Stream
        /// </summary>
        /// <returns></returns>
        private int GetNextStream()
        {
            int currentStream = GetCurrentStream();

            if (currentStream == -1)
            {
                return -1;
            }

            if (currentStream == 0 || Bass.ChannelIsActive(currentStream) == PlaybackState.Stopped)
            {
                return currentStream;
            }

            CurrentStreamIndex++;

            if (CurrentStreamIndex >= Streams.Count)
            {
                CurrentStreamIndex = 0;
            }

            return Streams[CurrentStreamIndex];
        }

        private void UpdateProgress(int stream)
        {
            if (PlaybackProgress != null)
            {
                var totaltime = new TimeSpan(0, 0, (int) GetTotalStreamSeconds(stream));
                var elapsedtime = new TimeSpan(0, 0, (int) GetStreamElapsedTime(stream));
                PlaybackProgress?.Invoke(this, new Progress {TotalTime = totaltime, ElapsedTime = elapsedtime});
            }
        }

        private void GetProgressInternal()
        {
            int stream = GetCurrentStream();

            if (StreamIsPlaying(stream))
            {
                UpdateProgress(stream);
            }
            else
            {
                UpdateTimer.Stop();
            }
        }

        public void GetProgress()
        {
            Task.Factory.StartNew(GetProgressInternal);
        }

        /// <summary>
        /// Timer to update the Playback Process
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnUpdateTimerTick(object sender, EventArgs e)
        {
            GetProgressInternal();
        }

        /// <summary>
        /// Load External BASS Audio Decoder Plugins
        /// </summary>
        private void LoadAudioDecoderPlugins()
        {
            //In this case, only load AAC to save load time
            Log.Info("BASS: Loading AAC Decoder");

            string decoderFolderPath = Path.GetDirectoryName(Assembly.GetAssembly(typeof (BassAudioEngine)).Location);
            if(decoderFolderPath == null)
            {
                Log.Error(@"BASS: Unable to load AAC decoder.");
                throw new BassException(@"BASS: Unable to load AAC decoder.");
            }

            string aacDecoder = Path.Combine(decoderFolderPath, "bass_aac.dll");

            int pluginHandle = 0;
            if ((pluginHandle = Bass.PluginLoad(aacDecoder)) != 0)
            {
                DecoderPluginHandles.Add(pluginHandle);
                Log.Debug("BASS: Added DecoderPlugin: {0}", aacDecoder);
            }
            else
            {
                Log.Error(@"BASS: Unable to load AAC decoder.");
                throw new BassException(@"BASS: Unable to load AAC decoder.");
            }
        }

        public void SetGain(double gainDb)
        {
            if (_gain == null)
            {
                _gain = new GainDsp();
            }

            if (Math.Abs(gainDb) < 0.0001)
            {
                _gain.Bypass = true;
            }
            else
            {
                _gain.Bypass = false;
                _gain.Gain = gainDb;
            }
        }

        private void FinalizeDownloadStream()
        {
            if (_downloadStream != null)
            {
                lock (_downloadStream)
                {
                    if (!_downloadFileComplete)
                    {
                        DownloadCanceled?.Invoke(this, _downloadFile);
                    }

                    _downloadStream.Flush();
                    _downloadStream.Close();
                    _downloadStream = null;

                    _downloadFile = string.Empty;
                    _downloadFileComplete = false;
                }
            }
        }

        private void SetupDownloadStream(string outputFile)
        {
            FinalizeDownloadStream();
            _downloadFile = outputFile;
            _downloadFileComplete = false;
            _downloadStream = new FileStream(outputFile, FileMode.Create);
        }

        public bool PlayStreamWithDownload(string url, string outputFile, double gainDb)
        {
            SetGain(gainDb);
            return PlayStreamWithDownload(url, outputFile);
        }

        public bool PlayStreamWithDownload(string url, string outputFile)
        {
            FinalizeDownloadStream();
            SetupDownloadStream(outputFile);
            return Play(url);
        }

        public bool Play(string filePath, double gainDb)
        {
            SetGain(gainDb);
            return Play(filePath);
        }

        /// <summary>
        /// Starts Playback of the given file
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public bool Play(string filePath)
        {
            if (!_Initialized)
            {
                return false;
            }

            try
            {
                UpdateTimer.Stop();
            }
            catch
            {
                throw new BassStreamException("Bass Error: Update Timer Error");
            }
            int stream = GetCurrentStream();

            bool doFade = false;
            bool result = true;
            Speed = 1; // Set playback Speed to normal speed

            try
            {
                if (Paused || (filePath.ToLower().CompareTo(FilePath.ToLower()) == 0 && stream != 0))
                {
                    bool doReturn = !Paused;
                    // Selected file is equal to current stream
                    if (_State == PlayState.Paused)
                    {
                        // Resume paused stream
                        if (_SoftStop)
                        {
                            Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, 1, 500);
                        }
                        else
                        {
                            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, 1);
                        }

                        result = Bass.Start();

                        if (result)
                        {
                            _State = PlayState.Playing;
                            UpdateTimer.Start();
                            PlaybackStateChanged?.Invoke(this, PlayState.Paused, _State);
                        }

                        if (doReturn)
                            return result;
                    }
                }

                if (stream != 0 && StreamIsPlaying(stream))
                {
                    int oldStream = stream;
                    double oldStreamDuration = GetTotalStreamSeconds(oldStream);
                    double oldStreamElapsedSeconds = GetStreamElapsedTime(oldStream);
                    double crossFadeSeconds = _CrossFadeIntervalMS;

                    if (crossFadeSeconds > 0)
                        crossFadeSeconds = crossFadeSeconds/1000.0;

                    if ((oldStreamDuration - (oldStreamElapsedSeconds + crossFadeSeconds) > -1))
                    {
                        FadeOutStop(oldStream);
                    }
                    else
                    {
                        Bass.ChannelStop(oldStream);
                    }

                    doFade = true;
                    stream = GetNextStream();

                    if (stream != 0 || StreamIsPlaying(stream))
                    {
                        _gain.Detach(stream);
                        FreeStream(stream);
                    }
                }

                if (stream != 0)
                {
                    if (!Stopped) // Check if stopped already to avoid that Stop() is called two or three times
                    {
                        Stop(true);
                    }
                    _gain.Detach(stream);
                    FreeStream(stream);
                }

                _State = PlayState.Init;

                // Make sure Bass is ready to begin playing again
                Bass.Start();

                if (filePath != string.Empty)
                {
                    // Turn on parsing of ASX files
                    Bass.NetPlaylist = 2;

                    var streamFlags = BassFlags.Float | BassFlags.AutoFree;

                    FilePath = filePath;

                    _isRadio = false;

                    if (filePath.ToLower().Contains(@"http://") || filePath.ToLower().Contains(@"https://") ||
                        filePath.ToLower().StartsWith("mms") || filePath.ToLower().StartsWith("rtsp"))
                    {
                        _isRadio = true; // We're playing Internet Radio Stream

                        stream = Bass.CreateStream(filePath, 0, streamFlags, DownloadProcDelegate, IntPtr.Zero);

                        if (stream != 0)
                        {
                            // Get the Tags and set the Meta Tag SyncProc
                            /*_tagInfo = new TAG_INFO(filePath);
                            SetStreamTags(stream);

                            if (BassTags.BASS_TAG_GetFromURL(stream, _tagInfo))
                            {
                                GetMetaTags();
                            }*/

                            Bass.ChannelSetSync(stream, SyncFlags.MetadataReceived, 0, MetaTagSyncProcDelegate, IntPtr.Zero);
                        }
                        Log.Debug("BASSAudio: Webstream found - trying to fetch stream {0}", Convert.ToString(stream));
                    }
                    else
                    {
                        // Create a Standard Stream
                        stream = Bass.CreateStream(filePath, 0, 0, streamFlags);
                    }

                    Streams[CurrentStreamIndex] = stream;

                    if (stream != 0)
                    {
                        StreamEventSyncHandles[CurrentStreamIndex] = RegisterPlaybackEvents(stream, CurrentStreamIndex);

                        if (doFade && _CrossFadeIntervalMS > 0)
                        {
                            _CrossFading = true;

                            // Reduce the stream volume to zero so we can fade it in...
                            Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, 0);

                            // Fade in from 0 to 1 over the _CrossFadeIntervalMS duration 
                            Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, 1,
                                                            _CrossFadeIntervalMS);
                        }
                    }
                    else
                    {
                        var error = Bass.LastError;
                        Log.Error("BASS: Unable to create Stream for {0}.  Reason: {1}.", filePath,
                                  Enum.GetName(typeof(Errors), error));
                        throw new BassStreamException("Bass Error: Unable to create stream - " +
                                                      Enum.GetName(typeof(Errors), error), error);
                    }

                    _gain.Attach(stream);

                    bool playbackStarted = false;

                    playbackStarted = Bass.ChannelPlay(stream);

                    if (stream != 0 && playbackStarted)
                    {
                        Log.Info("BASS: playback started");

                        PlayState oldState = _State;
                        _State = PlayState.Playing;

                        UpdateTimer.Start();

                        if (oldState != _State && PlaybackStateChanged != null)
                        {
                            PlaybackStateChanged(this, oldState, _State);
                        }

                        PlaybackStart?.Invoke(this, GetTotalStreamSeconds(stream));
                    }

                    else
                    {
                        var error = Bass.LastError;
                        Log.Error("BASS: Unable to play {0}.  Reason: {1}.", filePath,
                                  Enum.GetName(typeof(Errors), error));
                        throw new BassStreamException("Bass Error: Unable to play - " +
                                                      Enum.GetName(typeof(Errors), error), error);
                    }
                }
            }
            catch (Exception ex)
            {
                result = false;
                Log.Error("BASS: Play caused an exception:  {0}.", ex);

                if (ex.GetType() == typeof (BassStreamException))
                    throw;

                throw new BassException("BASS: Play caused an exception: " + ex);
            }

            return result;
        }

        /// <summary>
        /// Register the various Playback Events
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="streamIndex"></param>
        /// <returns></returns>
        private List<int> RegisterPlaybackEvents(int stream, int streamIndex)
        {
            if (stream == 0)
            {
                return null;
            }

            var syncHandles = new List<int>();

            // Don't register the fade out event for last.fm radio, as it causes problems
            // if (!_isLastFMRadio)
            syncHandles.Add(RegisterPlaybackFadeOutEvent(stream, streamIndex, _CrossFadeIntervalMS));

            syncHandles.Add(RegisterPlaybackEndEvent(stream, streamIndex));
            syncHandles.Add(RegisterStreamFreedEvent(stream));

            return syncHandles;
        }

        /// <summary>
        /// Register the Fade out Event
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="streamIndex"></param>
        /// <param name="fadeOutMS"></param>
        /// <returns></returns>
        private int RegisterPlaybackFadeOutEvent(int stream, int streamIndex, int fadeOutMS)
        {
            int syncHandle = 0;
            long len = Bass.ChannelGetLength(stream); // length in bytes
            double totaltime = Bass.ChannelBytes2Seconds(stream, len); // the total time length
            double fadeOutSeconds = 0;

            if (fadeOutMS > 0)
                fadeOutSeconds = fadeOutMS/1000.0;

            long bytePos = Bass.ChannelSeconds2Bytes(stream, totaltime - fadeOutSeconds);

            syncHandle = Bass.ChannelSetSync(stream,
                                             SyncFlags.Onetime | SyncFlags.Position,
                                             bytePos, PlaybackFadeOutProcDelegate,
                                             IntPtr.Zero);

            if (syncHandle == 0)
            {
                Log.Debug("BASS: RegisterPlaybackFadeOutEvent of stream {0} failed with error {1}", stream,
                          Enum.GetName(typeof (Errors), Bass.LastError));
            }

            return syncHandle;
        }

        /// <summary>
        /// Register the Playback end Event
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="streamIndex"></param>
        /// <returns></returns>
        private int RegisterPlaybackEndEvent(int stream, int streamIndex)
        {
            int syncHandle = 0;

            syncHandle = Bass.ChannelSetSync(stream,
                                             SyncFlags.Onetime | SyncFlags.End,
                                             0, PlaybackEndProcDelegate,
                                             IntPtr.Zero);

            if (syncHandle == 0)
            {
                Log.Debug("BASS: RegisterPlaybackEndEvent of stream {0} failed with error {1}", stream,
                          Enum.GetName(typeof (Errors), Bass.LastError));
            }

            return syncHandle;
        }

        /// <summary>
        /// Register Stream Free Event
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        private int RegisterStreamFreedEvent(int stream)
        {
            int syncHandle = 0;

            syncHandle = Bass.ChannelSetSync(stream, SyncFlags.Free,
                                             0, PlaybackStreamFreedProcDelegate,
                                             IntPtr.Zero);

            if (syncHandle == 0)
            {
                Log.Debug("BASS: RegisterStreamFreedEvent of stream {0} failed with error {1}", stream,
                          Enum.GetName(typeof (Errors), Bass.LastError));
            }

            return syncHandle;
        }


        /// <summary>
        /// Unregister the Playback Events
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="syncHandles"></param>
        /// <returns></returns>
        private bool UnregisterPlaybackEvents(int stream, List<int> syncHandles)
        {
            try
            {
                foreach (int syncHandle in syncHandles)
                {
                    if (syncHandle != 0)
                    {
                        Bass.ChannelRemoveSync(stream, syncHandle);
                    }
                }
            }

            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Free a Stream
        /// </summary>
        /// <param name="stream"></param>
        private void FreeStream(int stream)
        {
            int streamIndex = -1;

            for (int i = 0; i < Streams.Count; i++)
            {
                if (Streams[i] == stream)
                {
                    streamIndex = i;
                    break;
                }
            }

            if (streamIndex != -1)
            {
                List<int> eventSyncHandles = StreamEventSyncHandles[streamIndex];

                foreach (int syncHandle in eventSyncHandles)
                {
                    Bass.ChannelRemoveSync(stream, syncHandle);
                }
            }

            Bass.StreamFree(stream);

            _CrossFading = false; // Set crossfading to false, Play() will update it when the next song starts
        }

        /// <summary>
        /// Is stream Playing?
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        private bool StreamIsPlaying(int stream)
        {
            return stream != 0 && (Bass.ChannelIsActive(stream) == PlaybackState.Playing);
        }

        /// <summary>
        /// Get Total Seconds of the Stream
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        private double GetTotalStreamSeconds(int stream)
        {
            if (stream == 0)
            {
                return 0;
            }

            // length in bytes
            long len = Bass.ChannelGetLength(stream);

            // the total time length
            double totaltime = Bass.ChannelBytes2Seconds(stream, len);
            return totaltime;
        }

        /// <summary>
        /// Retrieve the elapsed time
        /// </summary>
        /// <returns></returns>
        private double GetStreamElapsedTime()
        {
            return GetStreamElapsedTime(GetCurrentStream());
        }

        /// <summary>
        /// Retrieve the elapsed time
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        private double GetStreamElapsedTime(int stream)
        {
            if (stream == 0)
            {
                return 0;
            }

            // position in bytes
            long pos = Bass.ChannelGetPosition(stream);

            // the elapsed time length
            double elapsedtime = Bass.ChannelBytes2Seconds(stream, pos);
            return elapsedtime;
        }

        private void DownloadProc(IntPtr buffer, int length, IntPtr user)
        {
            if (_downloadStream == null)
                return;

            Log.Debug("DownloadProc: " + length);
            try
            {
                if (buffer != IntPtr.Zero)
                {
                    var managedBuffer = new byte[length];
                    Marshal.Copy(buffer, managedBuffer, 0, length);
                    _downloadStream.Write(managedBuffer, 0, length);
                    _downloadStream.Flush();
                }
                else
                {
                    _downloadFileComplete = true;
                    string file = _downloadFile;

                    FinalizeDownloadStream();

                    DownloadComplete?.Invoke(this, file);
                }
            }
            catch (Exception ex)
            {
                Log.Error("BASS: Exception in DownloadProc: {0} {1}", ex.Message, ex.StackTrace);
            }
        }

        /// <summary>
        /// Fade Out  Procedure
        /// </summary>
        /// <param name="handle"></param>
        /// <param name="stream"></param>
        /// <param name="data"></param>
        /// <param name="userData"></param>
        private void PlaybackFadeOutProc(int handle, int stream, int data, IntPtr userData)
        {
            Log.Debug("BASS: PlaybackFadeOutProc of stream {0}", stream);

            CrossFade?.Invoke(this, FilePath);

            Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, -1, _CrossFadeIntervalMS);
            bool removed = Bass.ChannelRemoveSync(stream, handle);
            if (removed)
            {
                Log.Debug("BassAudio: *** BASS_ChannelRemoveSync in PlaybackFadeOutProc");
            }
        }

        /// <summary>
        /// Playback end Procedure
        /// </summary>
        /// <param name="handle"></param>
        /// <param name="stream"></param>
        /// <param name="data"></param>
        /// <param name="userData"></param>
        private void PlaybackEndProc(int handle, int stream, int data, IntPtr userData)
        {
            Log.Debug("BASS: PlaybackEndProc of stream {0}", stream);

            TrackPlaybackCompleted?.Invoke(this, FilePath);

            bool removed = Bass.ChannelRemoveSync(stream, handle);
            if (removed)
            {
                Log.Debug("BassAudio: *** BASS_ChannelRemoveSync in PlaybackEndProc");
            }
        }

        /// <summary>
        /// Stream Freed Proc
        /// </summary>
        /// <param name="handle"></param>
        /// <param name="stream"></param>
        /// <param name="data"></param>
        /// <param name="userData"></param>
        private void PlaybackStreamFreedProc(int handle, int stream, int data, IntPtr userData)
        {
            //Util.Log.O("PlaybackStreamFreedProc");
            Log.Debug("BASS: PlaybackStreamFreedProc of stream {0}", stream);

            HandleSongEnded(false);

            for (int i = 0; i < Streams.Count; i++)
            {
                if (stream == Streams[i])
                {
                    Streams[i] = 0;
                    break;
                }
            }
        }

        /// <summary>
        /// Gets the tags from the Internet Stream.
        /// </summary>
        /// <param name="stream"></param>
        private void SetStreamTags(int stream)
        {
            //TODO - Make this output to something useful??
            var tags = Bass.ChannelGetTags(stream, TagType.ICY);
            if (tags == IntPtr.Zero)
                tags = Bass.ChannelGetTags(stream, TagType.HTTP);
            if (tags != IntPtr.Zero)
            {
                foreach (var tag in Extensions.ExtractMultiStringAnsi(tags))
                {
                    if (tag.ToLower().StartsWith("icy-name:"))
                    {
                        //GUIPropertyManager.SetProperty("#Play.Current.Album", item.Substring(9));
                    }

                    if (tag.ToLower().StartsWith("icy-genre:"))
                    {
                        //GUIPropertyManager.SetProperty("#Play.Current.Genre", item.Substring(10));
                    }
                    Log.Info("BASS: Connection Information: {0}", tag);
                }
            }
        }
        
        /// <summary>
        /// This Callback Procedure is called by BASS, once a song changes.
        /// </summary>
        /// <param name="handle"></param>
        /// <param name="channel"></param>
        /// <param name="data"></param>
        /// <param name="user"></param>
        private void MetaTagSyncProc(int handle, int channel, int data, IntPtr user)
        {
            // BASS_SYNC_META is triggered on meta changes of SHOUTcast streams
            /*if (_tagInfo.UpdateFromMETA(Bass.ChannelGetTags(channel, TagType.META), false, false))
            {
                GetMetaTags();
            }*/
        }
        /*
        /// <summary>
        /// Set the Properties out of the Tags
        /// </summary>
        private void GetMetaTags()
        {
            // There seems to be an issue with setting correctly the title via taginfo
            // So let's filter it out ourself
            string title = _tagInfo.title;
            int streamUrlIndex = title.IndexOf("';StreamUrl=");
            if (streamUrlIndex > -1)
            {
                title = _tagInfo.title.Substring(0, streamUrlIndex);
            }

            Log.Info("BASS: Internet Stream. New Song: {0} - {1}", _tagInfo.artist, title);

            InternetStreamSongChanged?.Invoke(this);
        }
        */
        private void HandleSongEnded(bool bManualStop, bool songSkipped = false)
        {
            Log.Debug("BASS: HandleSongEnded - manualStop: {0}, CrossFading: {1}", bManualStop, _CrossFading);
            PlayState oldState = _State;

            if (!bManualStop)
            {
                FilePath = "";
                _State = PlayState.Ended;
            }
            else
            {
                _State = songSkipped ? PlayState.Init : PlayState.Stopped;
            }

            Util.Log.O("BASS: Playstate Changed - " + _State);

            if (oldState != _State)
            {
                PlaybackStateChanged?.Invoke(this, oldState, _State);
            }

            FinalizeDownloadStream();
            _CrossFading = false; // Set crossfading to false, Play() will update it when the next song starts
        }

        /// <summary>
        /// Fade out Song
        /// </summary>
        /// <param name="stream"></param>
        private void FadeOutStop(int stream)
        {
            Log.Debug("BASS: FadeOutStop of stream {0}", stream);

            if (!StreamIsPlaying(stream))
            {
                return;
            }

            //int level = Bass.BASS_ChannelGetLevel(stream);
            Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, -1, _CrossFadeIntervalMS);
        }

        /// <summary>
        /// Pause Playback
        /// </summary>
        public void PlayPause()
        {
            _CrossFading = false;
            int stream = GetCurrentStream();

            Log.Debug("BASS: Pause of stream {0}", stream);
            try
            {
                PlayState oldPlayState = _State;

                if (oldPlayState == PlayState.Ended || oldPlayState == PlayState.Init)
                {
                    return;
                }

                if (oldPlayState == PlayState.Paused)
                {
                    _State = PlayState.Playing;

                    if (_SoftStop)
                    {
                        // Fade-in over 500ms
                        Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, 1, 500);
                        Bass.Start();
                    }

                    else
                    {
                        Bass.ChannelSetAttribute(stream, ChannelAttribute.Volume, 1);
                        Bass.Start();
                    }

                    UpdateTimer.Start();
                }

                else
                {
                    _State = PlayState.Paused;
                    UpdateTimer.Stop();

                    if (_SoftStop)
                    {
                        // Fade-out over 500ms
                        Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, 0, 500);

                        // Wait until the slide is done
                        while (Bass.ChannelIsSliding(stream, ChannelAttribute.Volume))
                            Thread.Sleep(20);

                        Bass.Pause();
                    }

                    else
                    {
                        Bass.Pause();
                    }
                }

                if (oldPlayState != _State)
                {
                    PlaybackStateChanged?.Invoke(this, oldPlayState, _State);
                }
            }

            catch
            {
            }
        }

        /// <summary>
        /// Stopping Playback
        /// </summary>
        public void Stop(bool songSkipped = false)
        {
            _CrossFading = false;

            int stream = GetCurrentStream();
            Log.Debug("BASS: Stop of stream {0}", stream);
            try
            {
                UpdateTimer.Stop();
                if (_SoftStop)
                {
                    Bass.ChannelSlideAttribute(stream, ChannelAttribute.Volume, -1, 500);

                    // Wait until the slide is done
                    while (Bass.ChannelIsSliding(stream, ChannelAttribute.Volume))
                        Thread.Sleep(20);
                }
                Bass.ChannelStop(stream);

                PlaybackStop?.Invoke(this);

                HandleSongEnded(true, songSkipped);
            }

            catch (Exception ex)
            {
                Log.Error("BASS: Stop command caused an exception - {0}", ex.Message);
                throw new BassException("BASS: Stop command caused an exception - }" + ex.Message);
            }

            NotifyPlaying = false;
        }

        /// <summary>
        /// Is Seeking enabled 
        /// </summary>
        /// <returns></returns>
        public bool CanSeek()
        {
            return true;
        }

        /// <summary>
        /// Seek Forward in the Stream
        /// </summary>
        /// <param name="ms"></param>
        /// <returns></returns>
        public bool SeekForward(int ms)
        {
            if (_speed == 1) // not to exhaust log when ff
                Log.Debug("BASS: SeekForward for {0} ms", Convert.ToString(ms));
            _CrossFading = false;

            if (State != PlayState.Playing)
            {
                return false;
            }

            if (ms <= 0)
            {
                return false;
            }

            bool result = false;

            try
            {
                int stream = GetCurrentStream();
                long len = Bass.ChannelGetLength(stream); // length in bytes
                double totaltime = Bass.ChannelBytes2Seconds(stream, len); // the total time length

                var pos = Bass.ChannelGetPosition(stream); // position in bytes
                var timePos = Bass.ChannelBytes2Seconds(stream, pos);
                var offsetSecs = ms/1000.0;

                if (timePos + offsetSecs >= totaltime)
                {
                    return false;
                }

                result = Bass.ChannelSetPosition(stream, Bass.ChannelSeconds2Bytes(stream, timePos + offsetSecs)); // the elapsed time length
            }
            catch
            {
            }

            return result;
        }

        /// <summary>
        /// Seek Backwards within the stream
        /// </summary>
        /// <param name="ms"></param>
        /// <returns></returns>
        public bool SeekReverse(int ms)
        {
            if (_speed == 1) // not to exhaust log
                Log.Debug("BASS: SeekReverse for {0} ms", Convert.ToString(ms));
            _CrossFading = false;

            if (State != PlayState.Playing)
            {
                return false;
            }

            if (ms <= 0)
            {
                return false;
            }

            int stream = GetCurrentStream();
            bool result = false;

            try
            {
                var pos = Bass.ChannelGetPosition(stream); // position in bytes
                var timePos = Bass.ChannelBytes2Seconds(stream, pos);
                var offsetSecs = ms/1000.0;

                if (timePos - offsetSecs <= 0)
                {
                    return false;
                }

                result = Bass.ChannelSetPosition(stream, Bass.ChannelSeconds2Bytes(stream, timePos - offsetSecs)); // the elapsed time length
            }
            catch
            {
            }

            return result;
        }

        /// <summary>
        /// Seek to a specific position in the stream
        /// </summary>
        /// <param name="position"></param>
        /// <returns></returns>
        public bool SeekToTimePosition(int position)
        {
            Log.Debug("BASS: SeekToTimePosition: {0} ", Convert.ToString(position));
            _CrossFading = false;

            bool result = true;

            try
            {
                int stream = GetCurrentStream();

                if (StreamIsPlaying(stream))
                {
                    result = Bass.ChannelSetPosition(stream, Bass.ChannelSeconds2Bytes(stream, position));
                }
            }
            catch
            {
            }

            return result;
        }

        /// <summary>
        /// Seek Relative in the Stream
        /// </summary>
        /// <param name="dTime"></param>
        public void SeekRelative(double dTime)
        {
            _CrossFading = false;

            if (_State != PlayState.Init)
            {
                double dCurTime = GetStreamElapsedTime();

                dTime = dCurTime + dTime;

                if (dTime < 0.0d)
                {
                    dTime = 0.0d;
                }

                if (dTime < Duration)
                {
                    SeekToTimePosition((int) dTime);
                }
            }
        }

        /// <summary>
        /// Seek Absoluet in the Stream
        /// </summary>
        /// <param name="dTime"></param>
        public void SeekAbsolute(double dTime)
        {
            _CrossFading = false;

            if (_State != PlayState.Init)
            {
                if (dTime < 0.0d)
                {
                    dTime = 0.0d;
                }

                if (dTime < Duration)
                {
                    SeekToTimePosition((int) dTime);
                }
            }
        }

        /// <summary>
        /// Seek Relative Percentage
        /// </summary>
        /// <param name="iPercentage"></param>
        public void SeekRelativePercentage(int iPercentage)
        {
            _CrossFading = false;

            if (_State != PlayState.Init)
            {
                double dCurrentPos = GetStreamElapsedTime();
                double dDuration = Duration;
                double fOnePercentDuration = Duration/100.0d;

                double dSeekPercentageDuration = fOnePercentDuration*iPercentage;
                double dPositionMS = dDuration += dSeekPercentageDuration;

                if (dPositionMS < 0)
                {
                    dPositionMS = 0d;
                }

                if (dPositionMS > dDuration)
                {
                    dPositionMS = dDuration;
                }

                SeekToTimePosition((int) dDuration);
            }
        }

        /// <summary>
        /// Seek Absolute Percentage
        /// </summary>
        /// <param name="iPercentage"></param>
        public void SeekAsolutePercentage(int iPercentage)
        {
            _CrossFading = false;

            if (_State != PlayState.Init)
            {
                if (iPercentage < 0)
                {
                    iPercentage = 0;
                }

                if (iPercentage >= 100)
                {
                    iPercentage = 100;
                }

                if (iPercentage == 0)
                {
                    SeekToTimePosition(0);
                }

                else
                {
                    SeekToTimePosition((int) (Duration*(iPercentage/100d)));
                }
            }
        }

        public IList<string> GetOutputDevices()
        {
            var deviceList = new List<string>();

            for (var i = 0; i < Bass.DeviceCount; i++)
            {
                if (Bass.GetDeviceInfo(i, out var device))
                {
                    deviceList.Add(device.Name);
                }
            }

            return deviceList;
        }

        private void ChangeOutputDevice(string newOutputDevice)
        {
            if (newOutputDevice == null)
                throw new BassException("Null value provided to ChangeOutputDevice(string)");

            // Attempt to find the device number for the given string
            int oldDeviceId = Bass.CurrentDevice;
            int newDeviceId = -1;

            for (var i = 0; i < Bass.DeviceCount; i++)
            {
                if (Bass.GetDeviceInfo(i, out var device) && newOutputDevice.Equals(device.Name))
                {
                    newDeviceId = i;
                    break;
                }
            }
            if (newDeviceId == -1)
                throw new BassException("Cannot find an output device matching description [" + newOutputDevice + "]");

            Log.Info("BASS: Old device ID " + oldDeviceId);
            Log.Info("BASS: New device ID " + newDeviceId);

            // Make sure we're actually changing devices
            if (oldDeviceId == newDeviceId) return;

            // Initialize the new device
            bool initOK = false;
            Bass.GetDeviceInfo(newDeviceId, out var info);
            if (!info.IsInitialized)
            {
                Log.Info("BASS: Initializing new device ID " + newDeviceId);
                initOK = Bass.Init(newDeviceId, 44100, DeviceInitFlags.Default | DeviceInitFlags.Latency, IntPtr.Zero);
                if (!initOK)
                {
                    var error = Bass.LastError;
                    throw new BassException("Cannot initialize output device [" + newOutputDevice + "], error is [" + Enum.GetName(typeof(Errors), error) + "]");
                }
            }

            // If anything is playing, move the stream to the new output device
            if (State == PlayState.Playing)
            {
                Log.Info("BASS: Moving current stream to new device ID " + newDeviceId);
                int stream = GetCurrentStream();
                Bass.ChannelSetDevice(stream, newDeviceId);
            }

            // If the previous device was init'd, free it
            if (oldDeviceId >= 0)
            {
                Bass.GetDeviceInfo(oldDeviceId, out info);
                if (info.IsInitialized)
                {
                    Log.Info("BASS: Freeing device " + oldDeviceId);
                    Bass.CurrentDevice = oldDeviceId;
                    Bass.Free();
                    Bass.CurrentDevice = newDeviceId;
                }
            }

            _SoundDevice = newOutputDevice; 
        }

        #endregion

        public event PlaybackStartHandler PlaybackStart;

        public event PlaybackStopHandler PlaybackStop;

        public event PlaybackProgressHandler PlaybackProgress;

        public event TrackPlaybackCompletedHandler TrackPlaybackCompleted;

        public event CrossFadeHandler CrossFade;

        public event PlaybackStateChangedHandler PlaybackStateChanged;

        public event InternetStreamSongChangedHandler InternetStreamSongChanged;

        public event DownloadCompleteHandler DownloadComplete;

        public event DownloadCanceledHandler DownloadCanceled;
    }
}