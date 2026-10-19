// Screen Capture: a lightweight screen recorder.
// Copyright (C) 2026 Bakou
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenCapture;

/// <summary>Records one WASAPI endpoint (system loopback or microphone) to a WAV file.</summary>
internal sealed class AudioRecorder : IAudioTrack
{
    private readonly WasapiCapture _capture;
    private readonly WaveFileWriter _writer;
    private readonly WasapiOut? _silence;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytes;

    public string FilePath { get; }
    /// <summary>Time, on the recording clock, at which capture started.</summary>
    public TimeSpan StartedAt { get; private set; }
    public bool HasData => Interlocked.Read(ref _bytes) > 0;
    public Exception? Error { get; private set; }

    public AudioRecorder(MMDevice device, bool loopback, string filePath)
    {
        FilePath = filePath;
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);
        _writer = new WaveFileWriter(filePath, _capture.WaveFormat);

        if (loopback)
        {
            // WASAPI loopback delivers no data while nothing is playing, which would make the audio
            // track shorter than the video and out of sync. Playing silence keeps the stream continuous.
            _silence = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _silence.Init(new SilenceProvider(_capture.WaveFormat));
        }

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref _bytes, e.BytesRecorded);
        };
        _capture.RecordingStopped += (_, e) =>
        {
            Error = e.Exception;
            _writer.Dispose();
            _stopped.TrySetResult();
        };
    }

    public void Start(System.Diagnostics.Stopwatch clock)
    {
        _silence?.Play();
        StartedAt = clock.Elapsed;
        _capture.StartRecording();
    }

    public Task StopAsync()
    {
        _capture.StopRecording();
        _silence?.Stop();
        return _stopped.Task;
    }

    public void Dispose()
    {
        _capture.Dispose();
        _silence?.Dispose();
        _writer.Dispose();
    }
}
