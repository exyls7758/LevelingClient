using NAudio.Wave;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    /// <summary>
    /// Minimal process-specific WASAPI loopback capture built directly on the
    /// documented ActivateAudioInterfaceAsync PROCESS_LOOPBACK contract.
    /// It deliberately never opens a capture/microphone endpoint.
    /// </summary>
    internal sealed class ProcessLoopbackAudioCapture : IDisposable
    {
        private const string ProcessLoopbackDevice = @"VAD\Process_Loopback";
        private const ushort VariantTypeBlob = 65; // VT_BLOB
        private const long HundredNanosecondsPerMillisecond = 10_000;
        private static readonly Guid AudioClientInterfaceId =
            new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private static readonly Guid AudioCaptureClientInterfaceId =
            new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        public static readonly WaveFormat Format = new WaveFormat(48000, 16, 2);

        private readonly int _processId;
        private IProcessAudioClient? _audioClient;
        private IProcessAudioCaptureClient? _captureClient;
        private EventWaitHandle? _sampleEvent;
        private Thread? _captureThread;
        private volatile bool _running;
        private bool _disposed;

        public ProcessLoopbackAudioCapture(int processId)
        {
            if (processId <= 0)
                throw new ArgumentOutOfRangeException(nameof(processId));
            _processId = processId;
        }

        public event Action<ReadOnlyMemory<byte>>? PcmAvailable;
        public event Action<Exception>? CaptureFailed;

        public bool Running => _running;

        public async Task StartAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ProcessLoopbackAudioCapture));
            if (_running)
                return;

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                throw new PlatformNotSupportedException(
                    "Áudio por aplicativo requer Windows 10 build 20348 ou superior.");
            }

            // Activate from the thread pool. Some Windows process-loopback
            // activation paths can deadlock if the caller blocks an STA/UI
            // apartment waiting for ActivateCompleted. Audio activation is
            // therefore kept completely off the UI and video workers.
            IProcessAudioClient audioClient = await Task.Run(
                () => Activate(_processId, TimeSpan.FromSeconds(5)))
                .ConfigureAwait(false);

            IntPtr formatPointer = IntPtr.Zero;
            IntPtr capturePointer = IntPtr.Zero;
            try
            {
                formatPointer = WaveFormat.MarshalToPtr(Format);
                Guid sessionGuid = Guid.Empty;
                int hr = audioClient.Initialize(
                    AudioClientShareMode.Shared,
                    AudioClientStreamFlags.Loopback |
                        AudioClientStreamFlags.EventCallback |
                        AudioClientStreamFlags.AutoConvertPcm |
                        AudioClientStreamFlags.SrcDefaultQuality,
                    40 * HundredNanosecondsPerMillisecond,
                    0,
                    formatPointer,
                    ref sessionGuid);
                Marshal.ThrowExceptionForHR(hr);

                _sampleEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset);
                hr = audioClient.SetEventHandle(
                    _sampleEvent.SafeWaitHandle.DangerousGetHandle());
                Marshal.ThrowExceptionForHR(hr);

                Guid captureId = AudioCaptureClientInterfaceId;
                hr = audioClient.GetService(ref captureId, out capturePointer);
                Marshal.ThrowExceptionForHR(hr);

                _captureClient =
                    (IProcessAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePointer);
                _audioClient = audioClient;
                audioClient = null!;

                _running = true;
                _captureThread = new Thread(CaptureThreadMain)
                {
                    IsBackground = true,
                    Name = "Multiverse.ProcessAudio"
                };
                _captureThread.Start();
                return;
            }
            catch
            {
                ReleaseComObject(audioClient);
                StopAndRelease();
                throw;
            }
            finally
            {
                if (capturePointer != IntPtr.Zero)
                    Marshal.Release(capturePointer);
                if (formatPointer != IntPtr.Zero)
                    Marshal.FreeHGlobal(formatPointer);
            }
        }

        private void CaptureThreadMain()
        {
            IProcessAudioClient? client = _audioClient;
            IProcessAudioCaptureClient? capture = _captureClient;
            EventWaitHandle? sampleEvent = _sampleEvent;
            if (client is null || capture is null || sampleEvent is null)
                return;

            try
            {
                Marshal.ThrowExceptionForHR(client.Start());

                while (_running)
                {
                    sampleEvent.WaitOne(60);
                    if (!_running)
                        break;
                    DrainAvailablePackets(capture);
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _running = false;
                    try { CaptureFailed?.Invoke(ex); } catch { }
                }
            }
            finally
            {
                try { client.Stop(); } catch { }
                try { client.Reset(); } catch { }
            }
        }

        private void DrainAvailablePackets(IProcessAudioCaptureClient capture)
        {
            while (_running)
            {
                Marshal.ThrowExceptionForHR(
                    capture.GetNextPacketSize(out uint nextFrames));
                if (nextFrames == 0)
                    return;

                IntPtr dataPointer = IntPtr.Zero;
                uint frames = 0;
                try
                {
                    Marshal.ThrowExceptionForHR(
                        capture.GetBuffer(
                            out dataPointer,
                            out frames,
                            out AudioBufferFlags flags,
                            out _,
                            out _));

                    if (frames == 0)
                        continue;

                    int byteCount = checked((int)frames * Format.BlockAlign);
                    byte[] pcm = new byte[byteCount];
                    if ((flags & AudioBufferFlags.Silent) == 0 &&
                        dataPointer != IntPtr.Zero)
                    {
                        Marshal.Copy(dataPointer, pcm, 0, byteCount);
                    }

                    try { PcmAvailable?.Invoke(pcm); } catch { }
                }
                finally
                {
                    if (frames > 0)
                    {
                        try { capture.ReleaseBuffer(frames); } catch { }
                    }
                }
            }
        }

        public void StopAudioOnly() => StopAndRelease();

        private void StopAndRelease()
        {
            _running = false;
            try { _sampleEvent?.Set(); } catch { }

            Thread? captureThread = _captureThread;
            if (captureThread is not null)
            {
                try
                {
                    ConfirmedThread.JoinOrThrow(captureThread, 1000, "Audio capture");
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("AUDIO_CAPTURE_STOP_TIMEOUT", ex);
                    throw;
                }
            }
            _captureThread = null;

            try { _sampleEvent?.Dispose(); } catch { }
            _sampleEvent = null;

            ReleaseComObject(_captureClient);
            _captureClient = null;
            ReleaseComObject(_audioClient);
            _audioClient = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            StopAndRelease();
            _disposed = true;
        }

        private static IProcessAudioClient Activate(
            int processId,
            TimeSpan timeout)
        {
            AudioClientActivationParams activation = new()
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = checked((uint)processId),
                    ProcessLoopbackMode = ProcessLoopbackMode.IncludeProcessTree
                }
            };

            IntPtr activationMemory = IntPtr.Zero;
            IntPtr variantMemory = IntPtr.Zero;
            IActivateAudioInterfaceAsyncOperation? operation = null;
            try
            {
                int activationSize = Marshal.SizeOf<AudioClientActivationParams>();
                activationMemory = Marshal.AllocHGlobal(activationSize);
                Marshal.StructureToPtr(activation, activationMemory, false);

                PropVariantBlob variant = new()
                {
                    VariantType = VariantTypeBlob,
                    BlobSize = checked((uint)activationSize),
                    BlobData = activationMemory
                };
                variantMemory = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
                Marshal.StructureToPtr(variant, variantMemory, false);

                ActivationCompletionHandler completion = new();
                Guid iid = AudioClientInterfaceId;
                int hr = ActivateAudioInterfaceAsync(
                    ProcessLoopbackDevice,
                    ref iid,
                    variantMemory,
                    completion,
                    out operation);
                Marshal.ThrowExceptionForHR(hr);

                if (!completion.Wait(timeout))
                    throw new TimeoutException(
                        "O Windows demorou demais para abrir o áudio do aplicativo selecionado.");

                return completion.TakeAudioClient();
            }
            finally
            {
                ReleaseComObject(operation);
                if (variantMemory != IntPtr.Zero)
                    Marshal.FreeHGlobal(variantMemory);
                if (activationMemory != IntPtr.Zero)
                    Marshal.FreeHGlobal(activationMemory);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is null || !Marshal.IsComObject(value))
                return;
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [ComImport]
        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig]
            int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig]
            int GetActivateResult(
                out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        private sealed class ActivationCompletionHandler :
            IActivateAudioInterfaceCompletionHandler
        {
            private readonly ManualResetEventSlim _completed = new(false);
            private IProcessAudioClient? _audioClient;
            private Exception? _error;
            private int _activationResult;

            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try
                {
                    int hr = operation.GetActivateResult(
                        out _activationResult,
                        out object activatedInterface);
                    if (hr < 0)
                        _activationResult = hr;

                    if (_activationResult >= 0)
                    {
                        _audioClient = activatedInterface as IProcessAudioClient;
                        if (_audioClient is null)
                        {
                            _error = new InvalidCastException(
                                "O Windows não retornou IAudioClient para o áudio por processo.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _error = ex;
                }
                finally
                {
                    _completed.Set();
                }
                return 0;
            }

            public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);

            public IProcessAudioClient TakeAudioClient()
            {
                if (_error is not null)
                    throw _error;
                Marshal.ThrowExceptionForHR(_activationResult);
                return _audioClient ?? throw new InvalidOperationException(
                    "O Windows não retornou um cliente de áudio para o aplicativo selecionado.");
            }
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IProcessAudioClient
        {
            [PreserveSig]
            int Initialize(
                AudioClientShareMode shareMode,
                AudioClientStreamFlags streamFlags,
                long bufferDuration,
                long periodicity,
                IntPtr waveFormat,
                ref Guid audioSessionGuid);

            [PreserveSig] int GetBufferSize(out uint bufferSize);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(
                AudioClientShareMode shareMode,
                IntPtr waveFormat,
                out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IProcessAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(
                out IntPtr data,
                out uint framesToRead,
                out AudioBufferFlags flags,
                out ulong devicePosition,
                out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint framesRead);
            [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
        }

        private enum AudioClientShareMode
        {
            Shared = 0,
            Exclusive = 1
        }

        [Flags]
        private enum AudioClientStreamFlags : uint
        {
            None = 0,
            Loopback = 0x00020000,
            EventCallback = 0x00040000,
            SrcDefaultQuality = 0x08000000,
            AutoConvertPcm = 0x80000000
        }

        [Flags]
        private enum AudioBufferFlags : uint
        {
            None = 0,
            DataDiscontinuity = 0x1,
            Silent = 0x2,
            TimestampError = 0x4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariantBlob
        {
            public ushort VariantType;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public uint BlobSize;
            public IntPtr BlobData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParams ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParams
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        private enum AudioClientActivationType
        {
            Default = 0,
            ProcessLoopback = 1
        }

        private enum ProcessLoopbackMode
        {
            IncludeProcessTree = 0,
            ExcludeProcessTree = 1
        }
    }
}
