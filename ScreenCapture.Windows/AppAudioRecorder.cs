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

using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace ScreenCapture;

/// <summary>
/// Records the sound of one process and its child processes to a WAV file,
/// using WASAPI process loopback (Windows 10 version 2004 or later).
/// </summary>
internal sealed class AppAudioRecorder : IAudioTrack
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int FrameBytes = Channels * 2; // 16-bit PCM
    // Frames the track may lag behind the clock before silence is inserted (covers normal buffering).
    private const long Slack = SampleRate / 10;

    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly WaveFileWriter _writer;
    private readonly AutoResetEvent _ready = new(false);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private Stopwatch? _clock;
    private volatile bool _stopRequested;
    private long _bytes;

    public string FilePath { get; }
    public TimeSpan StartedAt { get; private set; }
    public bool HasData => Interlocked.Read(ref _bytes) > 0;
    public Exception? Error { get; private set; }

    public AppAudioRecorder(int processId, string filePath)
    {
        FilePath = filePath;
        // The process-loopback client cannot be marshalled to another apartment, so it is only ever used from
        // MTA threads (thread pool, capture thread), never from the STA UI thread. Waiting off the UI thread
        // also avoids a deadlock, since activation completes on a COM worker thread.
        (_client, _capture) = Task.Run(() => OpenAsync(processId)).GetAwaiter().GetResult();
        _writer = new WaveFileWriter(filePath, new WaveFormat(SampleRate, 16, Channels));
    }

    private async Task<(IAudioClient, IAudioCaptureClient)> OpenAsync(int processId)
    {
        var client = await ActivateAsync(processId);
        var format = new WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * FrameBytes,
            nBlockAlign = FrameBytes,
            wBitsPerSample = 16,
        };
        client.Initialize(0 /* shared */, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
            200 * 10_000, 0, ref format, IntPtr.Zero);
        client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
        var iid = typeof(IAudioCaptureClient).GUID;
        client.GetService(ref iid, out var service);
        return (client, (IAudioCaptureClient)service);
    }

    public void Start(Stopwatch clock)
    {
        _clock = clock;
        StartedAt = clock.Elapsed;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "App audio capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void CaptureLoop()
    {
        var buffer = new byte[SampleRate * FrameBytes];
        long frames = 0;
        try
        {
            _client.Start();
            while (!_stopRequested)
            {
                _ready.WaitOne(50);
                while (true)
                {
                    _capture.GetNextPacketSize(out uint packet);
                    if (packet == 0) break;
                    _capture.GetBuffer(out var data, out uint count, out uint flags, out _, out _);
                    int bytes = (int)count * FrameBytes;
                    if (bytes > buffer.Length) buffer = new byte[bytes];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                        Array.Clear(buffer, 0, bytes);
                    else
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                        Interlocked.Add(ref _bytes, bytes);
                    }
                    _capture.ReleaseBuffer(count);
                    _writer.Write(buffer, 0, bytes);
                    frames += count;
                }

                // Nothing is delivered while the app is quiet: pad with silence so the track keeps pace with the video.
                long due = (long)((_clock!.Elapsed - StartedAt).TotalSeconds * SampleRate) - Slack;
                while (frames < due)
                {
                    int n = (int)Math.Min(due - frames, buffer.Length / FrameBytes);
                    Array.Clear(buffer, 0, n * FrameBytes);
                    _writer.Write(buffer, 0, n * FrameBytes);
                    frames += n;
                }
            }
        }
        catch (Exception ex)
        {
            Error = ex;
        }
        finally
        {
            try { _client.Stop(); } catch { /* already stopped */ }
            _writer.Dispose();
            _stopped.TrySetResult();
        }
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        if (_thread == null) _stopped.TrySetResult();
        return _stopped.Task;
    }

    public void Dispose()
    {
        _stopRequested = true;
        _thread?.Join();
        Task.Run(() =>
        {
            Marshal.ReleaseComObject(_capture);
            Marshal.ReleaseComObject(_client);
        }).GetAwaiter().GetResult();
        _writer.Dispose();
        _ready.Dispose();
    }

    // ---------------------------------------------------------------- interop

    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const ushort VT_BLOB = 65;

    private static async Task<IAudioClient> ActivateAsync(int processId)
    {
        var parameters = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = 1,      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };
        var handler = new ActivationHandler();
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(parameters, paramsPtr, false);
            Marshal.StructureToPtr(new PROPVARIANT_BLOB
            {
                vt = VT_BLOB,
                cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                pBlobData = paramsPtr,
            }, variantPtr, false);
            ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", typeof(IAudioClient).GUID, variantPtr, handler, out _);
            return await handler.Result.Task;
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT_BLOB
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out int hr, out var client);
                if (hr < 0) Result.TrySetException(Marshal.GetExceptionForHR(hr)!);
                else Result.TrySetResult((IAudioClient)client);
            }
            catch (Exception ex)
            {
                Result.TrySetException(ex);
            }
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
        void GetBufferSize(out uint bufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }
}
