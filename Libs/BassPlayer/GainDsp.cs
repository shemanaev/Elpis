using System;
using System.Collections.Generic;
using ManagedBass;

namespace BassPlayer
{
    internal class GainDsp
    {
        private double _gain = 1.0;
        private readonly Dictionary<int, int> _dsp = new Dictionary<int, int>();

        public bool Bypass { get; set; }

        public GainDsp()
        {
            Bass.FloatingPointDSP = true; // make samples 32 bits to simplify code
        }

        /// <summary>
        /// Level in dB
        /// </summary>
        public double Gain
        {
            get => GainToDb(_gain, 1.0);
            set
            {
                if (double.IsNegativeInfinity(value))
                {
                    _gain = 0.0;
                }
                else if (value > 60.0)
                {
                    _gain = DbToGain(60.0, 1.0);
                }
                else
                {
                    _gain = DbToGain(value, 1.0);
                }
            }
        }

        public void Attach(int stream)
        {
            var dsp = Bass.ChannelSetDSP(stream, Callback, Priority: 1);
            _dsp.Add(stream, dsp);
        }

        public void Detach(int stream)
        {
            if (_dsp.TryGetValue(stream, out var dsp))
            {
                Bass.ChannelRemoveDSP(stream, dsp);
                _dsp.Remove(stream);
            }
        }

        private unsafe void Callback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
        {
            if (Bypass || Math.Abs(_gain - 1.0) < 0.00001)
            {
                return;
            }

            var p = (float*)(void*)buffer;
            for (var i = 0; i < length / 4; i++)
            {
                p[i] = (float)(p[i] * _gain);
            }
        }

        // http://www.animations.physics.unsw.edu.au/jw/dB.htm#absolute
        private static double DbToGain(double db, double max)
        {
            return max * Math.Pow(10.0, db / 20.0);
        }

        private static double GainToDb(double gain, double max)
        {
            return 20.0 * Math.Log10(gain / max);
        }
    }
}
