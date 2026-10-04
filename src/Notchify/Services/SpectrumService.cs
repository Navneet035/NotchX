using NAudio.Dsp;
using NAudio.Wave;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Real FFT of whatever the system is playing, via WASAPI loopback capture.
/// Reference-counted: capture only runs while at least one visualizer is on screen.
/// </summary>
public sealed class SpectrumService
{
    public const int Bands = 24;
    private const int FftSize = 2048;

    private WasapiLoopbackCapture? _capture;
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private int _pos;
    private int _users;
    private int _channels = 2;
    private int _sampleRate = 48000;

    /// <summary>0..1 levels per band, smoothed. Read from the UI thread.</summary>
    public float[] Levels { get; } = new float[Bands];
    public bool Available { get; private set; } = true;

    public void Acquire()
    {
        if (Interlocked.Increment(ref _users) == 1) StartCapture();
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _users) <= 0)
        {
            _users = 0;
            StopCapture();
        }
    }

    private void StartCapture()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _channels = _capture.WaveFormat.Channels;
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            // Some exclusive-mode / virtual devices don't support loopback; the UI falls back to a synthetic wobble.
            Available = false;
            Log.Info("spectrum unavailable: " + ex.Message);
        }
    }

    private void StopCapture()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
        Array.Clear(Levels);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;
        var bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
        if (bytesPerSample != 4) return; // loopback is IEEE float on every modern device
        var frame = bytesPerSample * _channels;
        for (var i = 0; i + frame <= e.BytesRecorded; i += frame)
        {
            float sum = 0;
            for (var c = 0; c < _channels; c++) sum += BitConverter.ToSingle(e.Buffer, i + c * 4);
            _fft[_pos].X = sum / _channels * (float)FastFourierTransform.HannWindow(_pos, FftSize);
            _fft[_pos].Y = 0;
            _pos++;
            if (_pos >= FftSize)
            {
                _pos = 0;
                Compute();
            }
        }
    }

    private void Compute()
    {
        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), _fft);
        var binHz = (double)_sampleRate / FftSize;
        // Log-spaced bands from 40 Hz to 16 kHz.
        for (var b = 0; b < Bands; b++)
        {
            var lo = 40 * Math.Pow(16000.0 / 40, (double)b / Bands);
            var hi = 40 * Math.Pow(16000.0 / 40, (double)(b + 1) / Bands);
            int i0 = Math.Max(1, (int)(lo / binHz)), i1 = Math.Max(i0 + 1, (int)(hi / binHz));
            double mag = 0;
            for (var i = i0; i < i1 && i < FftSize / 2; i++)
                mag = Math.Max(mag, Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y));
            var db = 20 * Math.Log10(mag + 1e-9);
            var level = (float)Math.Clamp((db + 70) / 60, 0, 1);
            // Fast attack, slow decay.
            Levels[b] = level > Levels[b] ? level : Levels[b] * 0.82f + level * 0.18f;
        }
    }
}
