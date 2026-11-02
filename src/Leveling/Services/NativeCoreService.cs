using System;
using System.Collections.Generic;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Runtime.InteropServices;
using System.Text;

namespace Multiverse.Services
{
    internal static class NativeCoreService
    {
        private const string DllName = "Multiverse.Core.dll";

        internal sealed class CaptureSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private CaptureSafeHandle()
                : base(ownsHandle: true)
            {
            }

            internal CaptureSafeHandle(IntPtr handle)
                : base(ownsHandle: true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                try
                {
                    MV_CaptureDestroy(handle);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeGpuInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Name;

            public ulong DedicatedVideoMemory;
            public uint VendorId;
            public uint DeviceId;
            public int IsSoftware;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeMonitorInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;

            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public uint Width;
            public uint Height;
            public uint RefreshRate;
            public int IsPrimary;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeEncoderConfig
        {
            public uint BitrateKbps;
            public uint Fps;
            public uint Preset;
            public uint UltraLowLatency;
            public uint MaxWidth;
            public uint MaxHeight;
            public uint LabInputMode;
            public uint ReservedLab;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeCapturePipelineStats
        {
            public ulong DesktopPresents;
            public ulong PointerOnlyUpdates;
            public ulong DxgiAccumulatedFrames;
            public ulong DxgiMissedFrames;
            public ulong PublishedToRing;
            public ulong ReplacedInRing;
            public ulong RingBusyDrops;
            public ulong ConsumedByEncoder;
            public ulong PublishPacingDrops;
            public uint LastAccumulatedFrames;
            public uint RingReadyFrames;
            public uint TargetPublishFps;
            public uint ReservedPacing;
            public double LastAcquireMs;
            public double AverageAcquireMs;
            public double LastRingCopyMs;
            public double AverageRingCopyMs;

            public ulong AcquireTimeouts;
            public ulong DuplicationResets;
            public ulong EncoderRestarts;
            public int LastCaptureStatus;
            public int Reserved0;

            public ulong SharedCaptureSyncTimeouts;
            public ulong SharedEncodeSyncTimeouts;
            public ulong SharedSyncAbandoned;
            public uint Core10IsolationActive;
            public uint SharedReadyFrames;
            public double SharedCaptureWaitMs;
            public double SharedAverageCaptureWaitMs;
            public double SharedEncodeWaitMs;
            public double SharedAverageEncodeWaitMs;

            public uint CaptureBackend;
            public uint ReservedBackend;
            public ulong BackendFramesArrived;
            public ulong BackendFramesPublished;
            public ulong BackendPacingDrops;
            public ulong BackendEmptyCallbacks;
            public ulong BackendRestarts;
            public double LastBackendCallbackMs;
            public double AverageBackendCallbackMs;
            public double LastBackendFrameAgeMs;
            public double AverageBackendFrameAgeMs;
            public int LastBackendStatus;
            public int ReservedBackendStatus;

            public uint BackendRunning;
            public uint BackendCallbacksInFlight;
            public uint BackendStopInProgress;
            public uint BackendStopStage;
            public double BackendLastCallbackAgoMs;
            public double BackendLastCallbackCompleteAgoMs;
            public double BackendStopStageAgeMs;
            public double LastPublishAgoMs;
            public double LastConsumeAgoMs;
            public uint EncoderWorkerAlive;
            public uint ReservedLiveness;

            public uint BackendCallbackStage;
            public uint BackendCallbackSubstage;
            public ulong BackendCurrentDrainFrames;
            public double BackendCallbackStageAgeMs;
            public uint CoreWgcStage;
            public uint RingPublishStage;
            public uint EncoderWorkerStage;
            public uint ReservedStageDiag;
            public double CoreWgcStageAgeMs;
            public double RingPublishStageAgeMs;
            public double EncoderWorkerStageAgeMs;

            public ulong RingAcquireCalls;
            public ulong RingAcquireNoReadyFrames;
            public ulong RingAcquireReadySelections;
            public ulong RingCaptureFenceReady;
            public ulong RingCaptureFencePending;
            public ulong RingPublishNoReusableSlots;
            public double RingAcquireWaitMs;
            public double RingAverageAcquireWaitMs;
            public double RingLeaseAgeMs;
            public double RingAverageLeaseAgeMs;
            public ulong RingCaptureFenceLag;
            public ulong RingMaxCaptureFenceLag;
            public uint RingReleaseFencePendingSlots;
            public uint RingProbeReserved0;

            public ulong BackendFramesDelivered;
            public ulong BackendPendingFramesReplaced;
            public uint BackendDeliveryWorkerAlive;
            public uint AdaptivePressureState;
            public uint AdaptiveTargetFps;
            public uint AdaptivePressureStreak;
            public uint AdaptiveHealthyStreak;
            public uint AdaptiveTransitions;
            public double BackendLastDeliveryDelayMs;
            public uint CaptureRingOccupiedFrames;
            public uint Core1117Reserved0;

            public ulong CopyGpuProbeSignals;
            public ulong CopyGpuProbeCompleted;
            public ulong CopyGpuReadyAtSubmit;
            public ulong CopyGpuNotReadyAtSubmit;
            public uint CopyGpuProbePending;
            public uint CopyGpuProbeMaxPending;
            public double CopyGpuLastLatencyMs;
            public double CopyGpuAverageLatencyMs;
            public double CopyGpuMaxLatencyMs;
            public double CopyGpuOldestPendingMs;
            public int CopyGpuProbeStatus;
            public uint Core1117cReserved0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeEncoderStats
        {
            public int Running;
            public int LastStatus;

            public uint Width;
            public uint Height;
            public uint TargetFps;
            public uint BitrateKbps;

            public ulong EncodedFrames;
            public ulong EncodedBytes;
            public ulong PacingDrops;
            public ulong QueueResets;

            public uint QueuedPackets;
            public uint QueuedBytes;

            public double LastEncodeMs;
            public double AverageEncodeMs;

            public ulong SubmittedFrames;
            public ulong BackpressureDrops;

            public uint InFlightFrames;
            public uint InputSlots;

            public double LastSubmitMs;
            public double AverageSubmitMs;

            public double LastOutputLatencyMs;
            public double AverageOutputLatencyMs;

            public int InputMode;
            public int RequestedInputMode;
            public ulong MapFailures;
            public ulong EncodeErrors;
            public ulong LockBusyRetries;
            public double LastPreprocessMs;
            public double AveragePreprocessMs;
            public double LastMapMs;
            public double AverageMapMs;
            public double LastEncodeCallMs;
            public double AverageEncodeCallMs;
            public double LastLockBitstreamMs;
            public double AverageLockBitstreamMs;
            public double StatsReadWaitMs;
            public uint PendingCommands;
            public uint SourceMode;
            public ulong NeedMoreInputAccepted;
            public ulong EncoderBusyRetries;
            public ulong NoFreeSlotDrops;
            public uint PendingOutputs;
            public ulong FlushTimeouts;
            public double OldestInFlightMs;
            public uint Core10PipelineMode;
            public uint CaptureDeviceIsolated;
            public uint Core11GpuPriority;
            public int CaptureGpuPriority;
            public int EncodeGpuPriority;
            public int GpuPrioritySetStatus;
            public int ProcessGpuPriorityRequested;
            public int ProcessGpuPriorityEffective;
            public int ProcessGpuPrioritySetStatus;
            public int ProcessGpuPriorityRestoreStatus;

            public uint EncoderWorkerAlive;
            public uint OutputWorkerAlive;
            public uint StopInProgress;
            public uint StopStage;
            public double LastSubmitAgoMs;
            public double LastOutputAgoMs;
            public double StopStageAgeMs;
            public uint NvencStopStage;
            public uint ReservedNvencStop;
            public double NvencStopStageAgeMs;

            public ulong PreprocessCopyFenceSignals;
            public ulong PreprocessCopyFenceCompleted;
            public uint PreprocessCopyFencePending;
            public uint PreprocessCopyFenceMaxPending;
            public double PreprocessCopyFenceOldestAgeMs;
            public ulong VideoProcessorFenceSignals;
            public ulong VideoProcessorFenceCompleted;
            public uint VideoProcessorFencePending;
            public uint VideoProcessorFenceMaxPending;
            public double VideoProcessorFenceOldestAgeMs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeDecoderStats
        {
            public int Running;
            public int FormatReady;
            public int LastStatus;
            public int Reserved0;

            public uint Width;
            public uint Height;
            public uint PreviewWidth;
            public uint PreviewHeight;

            public ulong SubmittedFrames;
            public ulong DecodedPictures;
            public ulong DisplayedFrames;
            public ulong PreviewFrames;

            public double LastDecodeLatencyMs;
            public double AverageDecodeLatencyMs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeD3D11RendererStats
        {
            public int Attached;
            public int LastStatus;

            public uint TargetWidth;
            public uint TargetHeight;
            public uint SourceWidth;
            public uint SourceHeight;

            public ulong SubmittedFrames;
            public ulong PresentedFrames;
            public ulong DroppedPresents;
            public ulong ReplacedPendingFrames;
            public ulong PresentWaitTimeouts;

            public ulong PresenterDequeuedFrames;
            public ulong FrameLatencySignals;
            public ulong FrameLatencyBypasses;
            public ulong FrameLatencyWaitFailures;
            public ulong PresentAttempts;
            public ulong PresentStillDrawing;
            public ulong PresentFailures;

            public uint ForegroundProcessId;
            public int ForegroundIsCurrentProcess;
            public uint PresenterStage;
            public int PresentInFlight;

            public double LastGpuCopyMs;
            public double AverageGpuCopyMs;
            public double LastPresentSubmitMs;
            public double AveragePresentSubmitMs;
            public double LastPresentWaitMs;
            public double AveragePresentWaitMs;

            public double LastQueueToPresenterMs;
            public double AverageQueueToPresenterMs;
            public double LastStateLockWaitMs;
            public double AverageStateLockWaitMs;
            public double LastPresentCallMs;
            public double AveragePresentCallMs;
            public double LastFrameAgeAtPresentMs;
            public double AverageFrameAgeAtPresentMs;
            public double PresenterActivityAgeMs;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_GetApiVersion();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_GetGpuCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_GetGpuInfo(
            int index,
            out NativeGpuInfo info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_GetMonitorCount();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_GetMonitorInfo(
            int index,
            out NativeMonitorInfo info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_TestD3D11Device();

        [DllImport(
            DllName,
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int MV_TestDesktopDuplication(
            string monitorDeviceName);

        [DllImport(
            DllName,
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int MV_CaptureCreate(
            string monitorDeviceName,
            out IntPtr handle,
            out uint width,
            out uint height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void MV_CaptureDestroy(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CaptureGetPreviewSize(
            IntPtr handle,
            out uint width,
            out uint height,
            out uint stride,
            out uint bytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CaptureGetFrameCount(
            IntPtr handle,
            out ulong count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CaptureGetPipelineStats(
            IntPtr handle,
            out NativeCapturePipelineStats stats);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CaptureGetLastError(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_CaptureCopyLatestPreview(
            IntPtr handle,
            [Out] byte[] destination,
            uint destinationBytes,
            out uint width,
            out uint height,
            out uint stride,
            out ulong frameId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_NvencIsCompiled();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_NvencRuntimeLibraryAvailable();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderStart(
            IntPtr handle,
            ref NativeEncoderConfig config);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void MV_EncoderBlockSubmissions(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void MV_EncoderStop(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderRequestIdr(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderReconfigureBitrate(
            IntPtr handle,
            uint bitrateKbps);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderSetPacketConsumerActive(
            IntPtr handle,
            int active);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderGetStats(
            IntPtr handle,
            out NativeEncoderStats stats);

        [DllImport(
            DllName,
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int MV_EncoderGetLastMessage(
            IntPtr handle,
            StringBuilder destination,
            uint chars);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderPeekPacket(
            IntPtr handle,
            out uint requiredBytes,
            out ulong frameId,
            out int keyFrame);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_EncoderPopPacket(
            IntPtr handle,
            [Out] byte[] destination,
            uint destinationBytes,
            out uint writtenBytes,
            out ulong frameId,
            out int keyFrame);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_NvdecIsCompiled();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_NvdecRuntimeLibraryAvailable();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderCreate(
            uint expectedWidth,
            uint expectedHeight,
            uint previewMaxWidth,
            out IntPtr decoderHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void MV_DecoderDestroy(
            IntPtr decoderHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderPushAnnexB(
            IntPtr decoderHandle,
            byte[] data,
            uint bytes,
            ulong frameId,
            int keyFrame);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderGetStats(
            IntPtr decoderHandle,
            out NativeDecoderStats stats);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderSignalDiscontinuity(
            IntPtr decoderHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderSetHeadlessOutput(
            IntPtr decoderHandle,
            int enabled);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderAttachSwapChainPanel(
            IntPtr decoderHandle,
            IntPtr panelUnknown,
            uint width,
            uint height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderResizeSwapChain(
            IntPtr decoderHandle,
            uint width,
            uint height);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderSetCompositionScale(
            IntPtr decoderHandle, float scaleX, float scaleY);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void MV_DecoderDetachSwapChainPanel(
            IntPtr decoderHandle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderGetD3D11RendererStats(
            IntPtr decoderHandle,
            out NativeD3D11RendererStats stats);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderGetPreviewSize(
            IntPtr decoderHandle,
            out uint width,
            out uint height,
            out uint stride,
            out uint requiredBytes);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int MV_DecoderCopyLatestPreview(
            IntPtr decoderHandle,
            [Out] byte[] destination,
            uint destinationBytes,
            out ulong previewFrameId);

        [DllImport(
            DllName,
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Unicode)]
        private static extern int MV_DecoderGetLastMessage(
            IntPtr decoderHandle,
            StringBuilder destination,
            uint destinationChars);

        public static HardwareSnapshot ReadHardware()
        {
            var snapshot = new HardwareSnapshot();

            try
            {
                snapshot.ApiVersion = MV_GetApiVersion();

                int d3d = MV_TestD3D11Device();
                snapshot.Direct3D11Ready = d3d >= 0;
                snapshot.Direct3D11HResult = d3d;

                var gpuKeys =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                int gpuCount =
                    Math.Max(0, MV_GetGpuCount());

                for (int i = 0; i < gpuCount; i++)
                {
                    if (MV_GetGpuInfo(i, out var gpu) == 0 ||
                        gpu.IsSoftware != 0)
                    {
                        continue;
                    }

                    string name =
                        string.IsNullOrWhiteSpace(gpu.Name)
                            ? $"GPU {i + 1}"
                            : gpu.Name.Trim();

                    string key =
                        $"{name}|{gpu.VendorId:X8}|{gpu.DeviceId:X8}";

                    if (!gpuKeys.Add(key))
                        continue;

                    snapshot.Gpus.Add(
                        new GpuInfo
                        {
                            Name = name,
                            DedicatedVideoMemory =
                                gpu.DedicatedVideoMemory,
                            VendorId = gpu.VendorId,
                            DeviceId = gpu.DeviceId
                        });
                }

                int monitorCount =
                    Math.Max(0, MV_GetMonitorCount());

                for (int i = 0; i < monitorCount; i++)
                {
                    if (MV_GetMonitorInfo(
                            i,
                            out var monitor) == 0)
                    {
                        continue;
                    }

                    int duplication =
                        MV_TestDesktopDuplication(
                            monitor.DeviceName);

                    snapshot.Monitors.Add(
                        new MonitorInfo
                        {
                            DeviceName =
                                monitor.DeviceName?.Trim()
                                ?? $"Monitor {i + 1}",

                            Left = monitor.Left,
                            Top = monitor.Top,
                            Width = monitor.Width,
                            Height = monitor.Height,
                            RefreshRate = monitor.RefreshRate,
                            IsPrimary = monitor.IsPrimary != 0,
                            DesktopDuplicationReady =
                                duplication >= 0,
                            DesktopDuplicationHResult =
                                duplication
                        });
                }

                snapshot.NvencCompiled =
                    MV_NvencIsCompiled() != 0;

                snapshot.NvencRuntimeAvailable =
                    MV_NvencRuntimeLibraryAvailable() != 0;

                snapshot.NativeCoreLoaded = true;
            }
            catch (Exception ex)
            {
                snapshot.Error = ex.Message;
            }

            return snapshot;
        }

        public static CaptureOpenResult OpenCapture(
            string monitorDeviceName)
        {
            try
            {
                int hr =
                    MV_CaptureCreate(
                        monitorDeviceName,
                        out IntPtr handle,
                        out uint sourceWidth,
                        out uint sourceHeight);

                if (hr < 0 ||
                    handle == IntPtr.Zero)
                {
                    return new CaptureOpenResult
                    {
                        HResult = hr,
                        Error = DescribeHResult(hr)
                    };
                }

                var safeHandle =
                    new CaptureSafeHandle(handle);

                try
                {
                    int previewHr =
                        MV_CaptureGetPreviewSize(
                            handle,
                            out uint previewWidth,
                            out uint previewHeight,
                            out uint previewStride,
                            out uint previewBytes);

                    if (previewHr < 0 ||
                        previewBytes == 0)
                    {
                        safeHandle.Dispose();

                        return new CaptureOpenResult
                        {
                            HResult = previewHr,
                            Error =
                                DescribeHResult(previewHr)
                        };
                    }

                    return new CaptureOpenResult
                    {
                        Session =
                            new NativeCaptureSession(
                                safeHandle,
                                sourceWidth,
                                sourceHeight,
                                previewWidth,
                                previewHeight,
                                previewStride,
                                previewBytes)
                    };
                }
                catch
                {
                    safeHandle.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CaptureOpenResult
                {
                    HResult =
                        Marshal.GetHRForException(ex),
                    Error = ex.Message
                };
            }
        }

        internal static int GetFrameCount(
            IntPtr handle,
            out ulong count) =>
            MV_CaptureGetFrameCount(
                handle,
                out count);

        internal static CapturePipelineStats? GetCapturePipelineStats(
            IntPtr handle)
        {
            int hr = MV_CaptureGetPipelineStats(handle, out var native);
            if (hr < 0)
                return null;

            return new CapturePipelineStats
            {
                DesktopPresents = native.DesktopPresents,
                PointerOnlyUpdates = native.PointerOnlyUpdates,
                DxgiAccumulatedFrames = native.DxgiAccumulatedFrames,
                DxgiMissedFrames = native.DxgiMissedFrames,
                PublishedToRing = native.PublishedToRing,
                ReplacedInRing = native.ReplacedInRing,
                RingBusyDrops = native.RingBusyDrops,
                ConsumedByEncoder = native.ConsumedByEncoder,
                PublishPacingDrops = native.PublishPacingDrops,
                LastAccumulatedFrames = native.LastAccumulatedFrames,
                RingReadyFrames = native.RingReadyFrames,
                TargetPublishFps = native.TargetPublishFps,
                FenceInitStage = native.ReservedPacing,
                LastFenceInitStatus = unchecked((int)native.ReservedStageDiag),
                LastAcquireMs = native.LastAcquireMs,
                AverageAcquireMs = native.AverageAcquireMs,
                LastRingCopyMs = native.LastRingCopyMs,
                AverageRingCopyMs = native.AverageRingCopyMs,
                AcquireTimeouts = native.AcquireTimeouts,
                DuplicationResets = native.DuplicationResets,
                EncoderRestarts = native.EncoderRestarts,
                LastCaptureStatus = native.LastCaptureStatus,
                IngressPublishCapFps = native.Reserved0 > 0 ? (uint)native.Reserved0 : 0u,
                SharedCaptureSyncTimeouts = native.SharedCaptureSyncTimeouts,
                SharedEncodeSyncTimeouts = native.SharedEncodeSyncTimeouts,
                SharedSyncAbandoned = native.SharedSyncAbandoned,
                Core10IsolationActive = native.Core10IsolationActive != 0,
                SharedReadyFrames = native.SharedReadyFrames,
                SharedCaptureWaitMs = native.SharedCaptureWaitMs,
                SharedAverageCaptureWaitMs = native.SharedAverageCaptureWaitMs,
                SharedEncodeWaitMs = native.SharedEncodeWaitMs,
                SharedAverageEncodeWaitMs = native.SharedAverageEncodeWaitMs,
                CaptureBackend = native.CaptureBackend,
                BackendFramesArrived = native.BackendFramesArrived,
                BackendFramesPublished = native.BackendFramesPublished,
                BackendPacingDrops = native.BackendPacingDrops,
                BackendEmptyCallbacks = native.BackendEmptyCallbacks,
                BackendRestarts = native.BackendRestarts,
                LastBackendCallbackMs = native.LastBackendCallbackMs,
                AverageBackendCallbackMs = native.AverageBackendCallbackMs,
                LastBackendFrameAgeMs = native.LastBackendFrameAgeMs,
                AverageBackendFrameAgeMs = native.AverageBackendFrameAgeMs,
                LastBackendStatus = native.LastBackendStatus,
                BackendStartStage = native.ReservedBackend,
                LastBackendStartStatus = native.ReservedBackendStatus,
                BackendRunning = native.BackendRunning != 0,
                BackendCallbacksInFlight = native.BackendCallbacksInFlight,
                BackendStopInProgress = native.BackendStopInProgress != 0,
                BackendStopStage = native.BackendStopStage,
                BackendLastCallbackAgoMs = native.BackendLastCallbackAgoMs,
                BackendLastCallbackCompleteAgoMs = native.BackendLastCallbackCompleteAgoMs,
                BackendStopStageAgeMs = native.BackendStopStageAgeMs,
                LastPublishAgoMs = native.LastPublishAgoMs,
                LastConsumeAgoMs = native.LastConsumeAgoMs,
                EncoderWorkerAlive = native.EncoderWorkerAlive != 0,
                RemoteClockLateTicks = native.ReservedLiveness,
                BackendCallbackStage = native.BackendCallbackStage,
                BackendCallbackSubstage = native.BackendCallbackSubstage,
                BackendCurrentDrainFrames = native.BackendCurrentDrainFrames,
                BackendCallbackStageAgeMs = native.BackendCallbackStageAgeMs,
                CoreWgcStage = native.CoreWgcStage,
                RingPublishStage = native.RingPublishStage,
                EncoderWorkerStage = native.EncoderWorkerStage,
                CoreWgcStageAgeMs = native.CoreWgcStageAgeMs,
                RingPublishStageAgeMs = native.RingPublishStageAgeMs,
                EncoderWorkerStageAgeMs = native.EncoderWorkerStageAgeMs,
                RingAcquireCalls = native.RingAcquireCalls,
                RingAcquireNoReadyFrames = native.RingAcquireNoReadyFrames,
                RingAcquireReadySelections = native.RingAcquireReadySelections,
                RingCaptureFenceReady = native.RingCaptureFenceReady,
                RingCaptureFencePending = native.RingCaptureFencePending,
                RingPublishNoReusableSlots = native.RingPublishNoReusableSlots,
                RingAcquireWaitMs = native.RingAcquireWaitMs,
                RingAverageAcquireWaitMs = native.RingAverageAcquireWaitMs,
                RingLeaseAgeMs = native.RingLeaseAgeMs,
                RingAverageLeaseAgeMs = native.RingAverageLeaseAgeMs,
                RingCaptureFenceLag = native.RingCaptureFenceLag,
                RingMaxCaptureFenceLag = native.RingMaxCaptureFenceLag,
                RingReleaseFencePendingSlots = native.RingReleaseFencePendingSlots,
                BackendFramesDelivered = native.BackendFramesDelivered,
                BackendPendingFramesReplaced = native.BackendPendingFramesReplaced,
                BackendDeliveryWorkerAlive = native.BackendDeliveryWorkerAlive != 0,
                AdaptivePressureState = native.AdaptivePressureState,
                AdaptiveTargetFps = native.AdaptiveTargetFps,
                AdaptivePressureStreak = native.AdaptivePressureStreak,
                AdaptiveHealthyStreak = native.AdaptiveHealthyStreak,
                AdaptiveTransitions = native.AdaptiveTransitions,
                BackendLastDeliveryDelayMs = native.BackendLastDeliveryDelayMs,
                CaptureRingOccupiedFrames = native.CaptureRingOccupiedFrames,
                CopyGpuProbeSignals = native.CopyGpuProbeSignals,
                CopyGpuProbeCompleted = native.CopyGpuProbeCompleted,
                CopyGpuReadyAtSubmit = native.CopyGpuReadyAtSubmit,
                CopyGpuNotReadyAtSubmit = native.CopyGpuNotReadyAtSubmit,
                CopyGpuProbePending = native.CopyGpuProbePending,
                CopyGpuProbeMaxPending = native.CopyGpuProbeMaxPending,
                CopyGpuLastLatencyMs = native.CopyGpuLastLatencyMs,
                CopyGpuAverageLatencyMs = native.CopyGpuAverageLatencyMs,
                CopyGpuMaxLatencyMs = native.CopyGpuMaxLatencyMs,
                CopyGpuOldestPendingMs = native.CopyGpuOldestPendingMs,
                CopyGpuProbeStatus = native.CopyGpuProbeStatus
            };
        }

        internal static int GetLastCaptureError(
            IntPtr handle) =>
            MV_CaptureGetLastError(handle);

        internal static int CopyPreview(
            IntPtr handle,
            byte[] buffer,
            out uint width,
            out uint height,
            out uint stride,
            out ulong frameId) =>
            MV_CaptureCopyLatestPreview(
                handle,
                buffer,
                (uint)buffer.Length,
                out width,
                out height,
                out stride,
                out frameId);

        internal static int StartEncoder(
            IntPtr handle,
            EncoderConfig config)
        {
            var native =
                new NativeEncoderConfig
                {
                    BitrateKbps =
                        config.BitrateKbps,

                    Fps = config.Fps,
                    Preset = config.Preset,

                    UltraLowLatency =
                        config.UltraLowLatency
                            ? 1u
                            : 0u,

                    MaxWidth =
                        config.MaxWidth,

                    MaxHeight =
                        config.MaxHeight,

                    LabInputMode =
                        config.LabInputMode,

                    ReservedLab = 0
                };

            return MV_EncoderStart(
                handle,
                ref native);
        }

        internal static void BlockEncoderSubmissions(IntPtr handle) => MV_EncoderBlockSubmissions(handle);

        internal static void StopEncoder(
            IntPtr handle) =>
            MV_EncoderStop(handle);

        internal static int RequestIdr(
            IntPtr handle) =>
            MV_EncoderRequestIdr(handle);

        internal static int ReconfigureEncoderBitrate(
            IntPtr handle,
            uint bitrateKbps) =>
            MV_EncoderReconfigureBitrate(
                handle,
                bitrateKbps);

        internal static int SetPacketConsumerActive(
            IntPtr handle,
            bool active) =>
            MV_EncoderSetPacketConsumerActive(
                handle,
                active ? 1 : 0);

        internal static EncoderStats? GetEncoderStats(
            IntPtr handle)
        {
            int hr =
                MV_EncoderGetStats(
                    handle,
                    out var native);

            if (hr < 0)
                return null;

            return new EncoderStats
            {
                Running = native.Running != 0,
                LastStatus = native.LastStatus,

                Width = native.Width,
                Height = native.Height,
                TargetFps = native.TargetFps,
                BitrateKbps = native.BitrateKbps,

                EncodedFrames =
                    native.EncodedFrames,

                EncodedBytes =
                    native.EncodedBytes,

                PacingDrops =
                    native.PacingDrops,

                QueueResets =
                    native.QueueResets,

                QueuedPackets =
                    native.QueuedPackets,

                QueuedBytes =
                    native.QueuedBytes,

                LastEncodeMs =
                    native.LastEncodeMs,

                AverageEncodeMs =
                    native.AverageEncodeMs,

                SubmittedFrames =
                    native.SubmittedFrames,

                BackpressureDrops =
                    native.BackpressureDrops,

                InFlightFrames =
                    native.InFlightFrames,

                InputSlots =
                    native.InputSlots,

                LastSubmitMs =
                    native.LastSubmitMs,

                AverageSubmitMs =
                    native.AverageSubmitMs,

                LastOutputLatencyMs =
                    native.LastOutputLatencyMs,

                AverageOutputLatencyMs =
                    native.AverageOutputLatencyMs,

                InputMode = native.InputMode,
                RequestedInputMode = native.RequestedInputMode,
                MapFailures = native.MapFailures,
                EncodeErrors = native.EncodeErrors,
                LockBusyRetries = native.LockBusyRetries,
                LastPreprocessMs = native.LastPreprocessMs,
                AveragePreprocessMs = native.AveragePreprocessMs,
                LastMapMs = native.LastMapMs,
                AverageMapMs = native.AverageMapMs,
                LastEncodeCallMs = native.LastEncodeCallMs,
                AverageEncodeCallMs = native.AverageEncodeCallMs,
                LastLockBitstreamMs = native.LastLockBitstreamMs,
                AverageLockBitstreamMs = native.AverageLockBitstreamMs,
                StatsReadWaitMs = native.StatsReadWaitMs,
                PendingCommands = native.PendingCommands,
                SourceMode = native.SourceMode,
                NeedMoreInputAccepted = native.NeedMoreInputAccepted,
                EncoderBusyRetries = native.EncoderBusyRetries,
                NoFreeSlotDrops = native.NoFreeSlotDrops,
                PendingOutputs = native.PendingOutputs,
                FlushTimeouts = native.FlushTimeouts,
                OldestInFlightMs = native.OldestInFlightMs,
                Core10PipelineMode = native.Core10PipelineMode,
                CaptureDeviceIsolated = native.CaptureDeviceIsolated != 0,
                Core11GpuPriority = native.Core11GpuPriority != 0,
                CaptureGpuPriority = native.CaptureGpuPriority,
                EncodeGpuPriority = native.EncodeGpuPriority,
                GpuPrioritySetStatus = native.GpuPrioritySetStatus,
                ProcessGpuPriorityRequested = native.ProcessGpuPriorityRequested,
                ProcessGpuPriorityEffective = native.ProcessGpuPriorityEffective,
                ProcessGpuPrioritySetStatus = native.ProcessGpuPrioritySetStatus,
                ProcessGpuPriorityRestoreStatus = native.ProcessGpuPriorityRestoreStatus,
                EncoderWorkerAlive = native.EncoderWorkerAlive != 0,
                OutputWorkerAlive = native.OutputWorkerAlive != 0,
                StopInProgress = native.StopInProgress != 0,
                StopStage = native.StopStage,
                LastSubmitAgoMs = native.LastSubmitAgoMs,
                LastOutputAgoMs = native.LastOutputAgoMs,
                StopStageAgeMs = native.StopStageAgeMs,
                NvencStopStage = native.NvencStopStage,
                NvencStopStageAgeMs = native.NvencStopStageAgeMs,
                PreprocessCopyFenceSignals = native.PreprocessCopyFenceSignals,
                PreprocessCopyFenceCompleted = native.PreprocessCopyFenceCompleted,
                PreprocessCopyFencePending = native.PreprocessCopyFencePending,
                PreprocessCopyFenceMaxPending = native.PreprocessCopyFenceMaxPending,
                PreprocessCopyFenceOldestAgeMs = native.PreprocessCopyFenceOldestAgeMs,
                VideoProcessorFenceSignals = native.VideoProcessorFenceSignals,
                VideoProcessorFenceCompleted = native.VideoProcessorFenceCompleted,
                VideoProcessorFencePending = native.VideoProcessorFencePending,
                VideoProcessorFenceMaxPending = native.VideoProcessorFenceMaxPending,
                VideoProcessorFenceOldestAgeMs = native.VideoProcessorFenceOldestAgeMs
            };
        }

        internal static string GetEncoderMessage(
            IntPtr handle)
        {
            var buffer =
                new StringBuilder(768);

            MV_EncoderGetLastMessage(
                handle,
                buffer,
                (uint)buffer.Capacity);

            return buffer.ToString();
        }

        internal static EncodedPacket? TryPopPacket(
            IntPtr handle)
        {
            int peek =
                MV_EncoderPeekPacket(
                    handle,
                    out uint required,
                    out _,
                    out _);

            if (peek != 0 ||
                required == 0)
            {
                return null;
            }

            var data =
                new byte[
                    checked((int)required)];

            int pop =
                MV_EncoderPopPacket(
                    handle,
                    data,
                    required,
                    out uint written,
                    out ulong frameId,
                    out int keyFrame);

            if (pop != 0)
                return null;

            if (written != data.Length)
                Array.Resize(
                    ref data,
                    checked((int)written));

            return new EncodedPacket
            {
                Data = data,
                FrameId = frameId,
                KeyFrame = keyFrame != 0
            };
        }

        public static bool NvdecCompiled =>
            MV_NvdecIsCompiled() != 0;

        public static bool NvdecRuntimeAvailable =>
            MV_NvdecRuntimeLibraryAvailable() != 0;

        public static NvdecOpenResult OpenNvdec(
            uint expectedWidth,
            uint expectedHeight,
            uint previewMaxWidth = 960)
        {
            try
            {
                int result =
                    MV_DecoderCreate(
                        expectedWidth,
                        expectedHeight,
                        previewMaxWidth,
                        out IntPtr handle);

                if (result < 0 ||
                    handle == IntPtr.Zero)
                {
                    return new NvdecOpenResult
                    {
                        Status = result,
                        Error =
                            $"Falha ao criar NVDEC ({result})."
                    };
                }

                int previewResult =
                    MV_DecoderGetPreviewSize(
                        handle,
                        out uint width,
                        out uint height,
                        out uint stride,
                        out uint bytes);

                return new NvdecOpenResult
                {
                    Status = previewResult,
                    Session =
                        new NativeNvdecSession(
                            handle,
                            width,
                            height,
                            stride,
                            bytes)
                };
            }
            catch (Exception ex)
            {
                return new NvdecOpenResult
                {
                    Status =
                        Marshal.GetHRForException(ex),
                    Error = ex.Message
                };
            }
        }

        internal static int DecoderPush(
            IntPtr handle,
            EncodedPacket packet) =>
            MV_DecoderPushAnnexB(
                handle,
                packet.Data,
                (uint)packet.Data.Length,
                packet.FrameId,
                packet.KeyFrame ? 1 : 0);

        internal static int DecoderSignalDiscontinuity(
            IntPtr handle) =>
            MV_DecoderSignalDiscontinuity(
                handle);

        internal static int DecoderSetHeadlessOutput(
            IntPtr handle,
            bool enabled) =>
            MV_DecoderSetHeadlessOutput(
                handle,
                enabled ? 1 : 0);

        internal static int DecoderAttachSwapChainPanel(
            IntPtr handle,
            IntPtr panelUnknown,
            uint width,
            uint height) =>
            MV_DecoderAttachSwapChainPanel(
                handle,
                panelUnknown,
                width,
                height);

        internal static int DecoderResizeSwapChain(
            IntPtr handle,
            uint width,
            uint height) =>
            MV_DecoderResizeSwapChain(
                handle,
                width,
                height);

        // Optional additive ABI: older native DLLs can still present using
        // logical-size buffers. Never use physical sizes without the matrix.
        internal static int DecoderSetCompositionScale(
            IntPtr handle, float scaleX, float scaleY)
        {
            try { return MV_DecoderSetCompositionScale(handle, scaleX, scaleY); }
            catch (EntryPointNotFoundException) { return -20022; }
        }

        internal static void DecoderDetachSwapChainPanel(
            IntPtr handle) =>
            MV_DecoderDetachSwapChainPanel(
                handle);

        internal static D3D11RendererStats? DecoderRendererStats(
            IntPtr handle)
        {
            int result =
                MV_DecoderGetD3D11RendererStats(
                    handle,
                    out NativeD3D11RendererStats native);

            if (result < 0)
                return null;

            return new D3D11RendererStats
            {
                Attached =
                    native.Attached != 0,

                LastStatus =
                    native.LastStatus,

                TargetWidth =
                    native.TargetWidth,

                TargetHeight =
                    native.TargetHeight,

                SourceWidth =
                    native.SourceWidth,

                SourceHeight =
                    native.SourceHeight,

                SubmittedFrames =
                    native.SubmittedFrames,

                PresentedFrames =
                    native.PresentedFrames,

                DroppedPresents =
                    native.DroppedPresents,

                ReplacedPendingFrames =
                    native.ReplacedPendingFrames,

                PresentWaitTimeouts =
                    native.PresentWaitTimeouts,

                PresenterDequeuedFrames =
                    native.PresenterDequeuedFrames,

                FrameLatencySignals =
                    native.FrameLatencySignals,

                FrameLatencyBypasses =
                    native.FrameLatencyBypasses,

                FrameLatencyWaitFailures =
                    native.FrameLatencyWaitFailures,

                PresentAttempts =
                    native.PresentAttempts,

                PresentStillDrawing =
                    native.PresentStillDrawing,

                PresentFailures =
                    native.PresentFailures,

                ForegroundProcessId =
                    native.ForegroundProcessId,

                ForegroundIsCurrentProcess =
                    native.ForegroundIsCurrentProcess != 0,

                PresenterStage =
                    native.PresenterStage,

                PresentInFlight =
                    native.PresentInFlight != 0,

                LastGpuCopyMs =
                    native.LastGpuCopyMs,

                AverageGpuCopyMs =
                    native.AverageGpuCopyMs,

                LastPresentSubmitMs =
                    native.LastPresentSubmitMs,

                AveragePresentSubmitMs =
                    native.AveragePresentSubmitMs,

                LastPresentWaitMs =
                    native.LastPresentWaitMs,

                AveragePresentWaitMs =
                    native.AveragePresentWaitMs,

                LastQueueToPresenterMs =
                    native.LastQueueToPresenterMs,

                AverageQueueToPresenterMs =
                    native.AverageQueueToPresenterMs,

                LastStateLockWaitMs =
                    native.LastStateLockWaitMs,

                AverageStateLockWaitMs =
                    native.AverageStateLockWaitMs,

                LastPresentCallMs =
                    native.LastPresentCallMs,

                AveragePresentCallMs =
                    native.AveragePresentCallMs,

                LastFrameAgeAtPresentMs =
                    native.LastFrameAgeAtPresentMs,

                AverageFrameAgeAtPresentMs =
                    native.AverageFrameAgeAtPresentMs,

                PresenterActivityAgeMs =
                    native.PresenterActivityAgeMs
            };
        }

        internal static NvdecStats? DecoderStats(
            IntPtr handle)
        {
            int result =
                MV_DecoderGetStats(
                    handle,
                    out NativeDecoderStats native);

            if (result < 0)
                return null;

            return new NvdecStats
            {
                Running = native.Running != 0,
                FormatReady = native.FormatReady != 0,
                LastStatus = native.LastStatus,
                Width = native.Width,
                Height = native.Height,
                PreviewWidth = native.PreviewWidth,
                PreviewHeight = native.PreviewHeight,
                SubmittedFrames = native.SubmittedFrames,
                DecodedPictures = native.DecodedPictures,
                DisplayedFrames = native.DisplayedFrames,
                PreviewFrames = native.PreviewFrames,
                LastDecodeLatencyMs = native.LastDecodeLatencyMs,
                AverageDecodeLatencyMs = native.AverageDecodeLatencyMs
            };
        }

        internal static int DecoderCopyPreview(
            IntPtr handle,
            byte[] buffer,
            out ulong frameId) =>
            MV_DecoderCopyLatestPreview(
                handle,
                buffer,
                (uint)buffer.Length,
                out frameId);

        internal static string DecoderMessage(
            IntPtr handle)
        {
            var text =
                new StringBuilder(768);

            MV_DecoderGetLastMessage(
                handle,
                text,
                (uint)text.Capacity);

            return text.ToString();
        }

        internal static void DestroyDecoder(
            IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                MV_DecoderDestroy(handle);
        }

        public static string DescribeHResult(
            int hresult)
        {
            if (hresult >= 0)
                return "OK";

            try
            {
                return Marshal
                    .GetExceptionForHR(hresult)?
                    .Message
                    ?? $"HRESULT 0x{hresult:X8}";
            }
            catch
            {
                return $"HRESULT 0x{hresult:X8}";
            }
        }
    }

    internal sealed class NativeCaptureSession :
        IDisposable
    {
        private readonly object _sync = new();
        private readonly ExclusivePacketConsumer<EncodedPacket> _packetConsumer;

        private NativeCoreService.CaptureSafeHandle? _handle;
        private ulong _lastPreviewFrame;

        public NativeCaptureSession(
            NativeCoreService.CaptureSafeHandle handle,
            uint sourceWidth,
            uint sourceHeight,
            uint previewWidth,
            uint previewHeight,
            uint previewStride,
            uint previewBytes)
        {
            _handle = handle;
            _packetConsumer = new ExclusivePacketConsumer<EncodedPacket>(
                active =>
                {
                    int hr = WithHandle(
                        h => NativeCoreService.SetPacketConsumerActive(h, active),
                        unchecked((int)0x80004005));
                    if (hr < 0 && active)
                        Marshal.ThrowExceptionForHR(hr);
                },
                () => WithHandle<EncodedPacket?>(NativeCoreService.TryPopPacket, null));

            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;

            PreviewWidth = previewWidth;
            PreviewHeight = previewHeight;
            PreviewStride = previewStride;

            Buffer =
                new byte[
                    checked((int)previewBytes)];
        }

        public uint SourceWidth { get; }
        public uint SourceHeight { get; }
        public uint PreviewWidth { get; }
        public uint PreviewHeight { get; }
        public uint PreviewStride { get; }
        public byte[] Buffer { get; }

        private TResult WithHandle<TResult>(
            Func<IntPtr, TResult> action,
            TResult unavailable)
        {
            NativeCoreService.CaptureSafeHandle? safe =
                Volatile.Read(ref _handle);

            if (safe is null || safe.IsInvalid || safe.IsClosed)
                return unavailable;

            bool addedRef = false;
            try
            {
                safe.DangerousAddRef(ref addedRef);
                if (!addedRef || safe.IsInvalid)
                    return unavailable;

                return action(safe.DangerousGetHandle());
            }
            catch (ObjectDisposedException)
            {
                return unavailable;
            }
            finally
            {
                if (addedRef)
                    safe.DangerousRelease();
            }
        }

        private void WithHandle(Action<IntPtr> action)
        {
            WithHandle(
                handle =>
                {
                    action(handle);
                    return true;
                },
                false);
        }

        public ulong GetFrameCount()
        {
            lock (_sync)
            {
                return WithHandle(
                    handle => NativeCoreService.GetFrameCount(handle, out ulong count) >= 0 ? count : 0UL,
                    0UL);
            }
        }

        public int GetLastError()
        {
            lock (_sync)
            {
                return WithHandle(
                    NativeCoreService.GetLastCaptureError,
                    unchecked((int)0x80004005));
            }
        }

        public CaptureReadResult ReadLatestPreview()
        {
            lock (_sync)
            {
                return WithHandle(
                    handle =>
                    {
                        int countResult = NativeCoreService.GetFrameCount(handle, out ulong count);
                        if (countResult < 0 || count == 0 || count == _lastPreviewFrame)
                            return new CaptureReadResult { HResult = 1 };

                        int hr = NativeCoreService.CopyPreview(
                            handle,
                            Buffer,
                            out uint width,
                            out uint height,
                            out uint stride,
                            out ulong frameId);

                        if (hr < 0)
                            return new CaptureReadResult { HResult = hr };

                        if (hr == 1 || frameId == _lastPreviewFrame)
                            return new CaptureReadResult { HResult = 1 };

                        _lastPreviewFrame = frameId;
                        return new CaptureReadResult
                        {
                            HResult = hr,
                            HasNewFrame = true,
                            Width = width,
                            Height = height,
                            Stride = stride,
                            FrameId = frameId
                        };
                    },
                    new CaptureReadResult { HResult = unchecked((int)0x80004005) });
            }
        }

        public CapturePipelineStats? GetCapturePipelineStats()
        {
            lock (_sync)
            {
                return WithHandle<CapturePipelineStats?>(
                    NativeCoreService.GetCapturePipelineStats,
                    null);
            }
        }

        public CapturePipelineStats? GetCapturePipelineStatsDiagnostic() =>
            WithHandle<CapturePipelineStats?>(
                NativeCoreService.GetCapturePipelineStats,
                null);

        public EncoderStartResult StartEncoder(EncoderConfig config)
        {
            // Consistent lock order: consumer gate -> capture state. Never
            // wait for the consumer gate while holding _sync.
            return _packetConsumer.RunInactive(() =>
            {
                lock (_sync)
                    return WithHandle(
                    handle =>
                    {
                        int hr = NativeCoreService.StartEncoder(handle, config);
                        return new EncoderStartResult
                        {
                            HResult = hr,
                            Message = NativeCoreService.GetEncoderMessage(handle)
                        };
                    },
                    new EncoderStartResult
                    {
                        HResult = unchecked((int)0x80004005),
                        Message = "Sessão de captura fechada."
                    });
            });
        }

        // The lease gate remains held across native stop. A new sender cannot
        // activate the same queue until the previous encoder is fully stopped.
        public void BlockEncoderSubmissions() => WithHandle(NativeCoreService.BlockEncoderSubmissions);

        public void StopEncoder() =>
            _packetConsumer.RunInactive(() => WithHandle(NativeCoreService.StopEncoder));

        public int RequestIdr()
        {
            lock (_sync)
            {
                return WithHandle(
                    NativeCoreService.RequestIdr,
                    unchecked((int)0x80004005));
            }
        }

        public int ReconfigureEncoderBitrate(uint bitrateKbps)
        {
            lock (_sync)
            {
                return WithHandle(
                    handle => NativeCoreService.ReconfigureEncoderBitrate(handle, bitrateKbps),
                    unchecked((int)0x80004005));
            }
        }

        public ExclusivePacketConsumer<EncodedPacket>.Lease AcquirePacketConsumer() =>
            _packetConsumer.Acquire();

        public EncoderStats? GetEncoderStats()
        {
            lock (_sync)
            {
                return WithHandle<EncoderStats?>(
                    NativeCoreService.GetEncoderStats,
                    null);
            }
        }

        public EncoderStats? GetEncoderStatsDiagnostic() =>
            WithHandle<EncoderStats?>(
                NativeCoreService.GetEncoderStats,
                null);

        public string GetEncoderMessage()
        {
            lock (_sync)
            {
                return WithHandle(
                    NativeCoreService.GetEncoderMessage,
                    "Sessão fechada.");
            }
        }

        public void Dispose()
        {
            _packetConsumer.RunInactive(() =>
            {
                NativeCoreService.CaptureSafeHandle? handle;
                lock (_sync)
                {
                    handle = _handle;
                    _handle = null;
                }
                handle?.Dispose();
            });
        }
    }

    internal sealed class CapturePipelineStats
    {
        public ulong DesktopPresents { get; set; }
        public ulong PointerOnlyUpdates { get; set; }
        public ulong DxgiAccumulatedFrames { get; set; }
        public ulong DxgiMissedFrames { get; set; }
        public ulong PublishedToRing { get; set; }
        public ulong ReplacedInRing { get; set; }
        public ulong RingBusyDrops { get; set; }
        public ulong ConsumedByEncoder { get; set; }
        public ulong PublishPacingDrops { get; set; }
        public uint LastAccumulatedFrames { get; set; }
        public uint RingReadyFrames { get; set; }
        public uint TargetPublishFps { get; set; }
        public uint FenceInitStage { get; set; }
        public int LastFenceInitStatus { get; set; }
        public double LastAcquireMs { get; set; }
        public double AverageAcquireMs { get; set; }
        public double LastRingCopyMs { get; set; }
        public double AverageRingCopyMs { get; set; }
        public ulong AcquireTimeouts { get; set; }
        public ulong DuplicationResets { get; set; }
        public ulong EncoderRestarts { get; set; }
        public int LastCaptureStatus { get; set; }
        public uint IngressPublishCapFps { get; set; }
        public ulong SharedCaptureSyncTimeouts { get; set; }
        public ulong SharedEncodeSyncTimeouts { get; set; }
        public ulong SharedSyncAbandoned { get; set; }
        public bool Core10IsolationActive { get; set; }
        public uint SharedReadyFrames { get; set; }
        public double SharedCaptureWaitMs { get; set; }
        public double SharedAverageCaptureWaitMs { get; set; }
        public double SharedEncodeWaitMs { get; set; }
        public double SharedAverageEncodeWaitMs { get; set; }
        public uint CaptureBackend { get; set; }
        public ulong BackendFramesArrived { get; set; }
        public ulong BackendFramesPublished { get; set; }
        public ulong BackendPacingDrops { get; set; }
        public ulong BackendEmptyCallbacks { get; set; }
        public ulong BackendRestarts { get; set; }
        public double LastBackendCallbackMs { get; set; }
        public double AverageBackendCallbackMs { get; set; }
        public double LastBackendFrameAgeMs { get; set; }
        public double AverageBackendFrameAgeMs { get; set; }
        public int LastBackendStatus { get; set; }
        public uint BackendStartStage { get; set; }
        public int LastBackendStartStatus { get; set; }
        public bool BackendRunning { get; set; }
        public uint BackendCallbacksInFlight { get; set; }
        public bool BackendStopInProgress { get; set; }
        public uint BackendStopStage { get; set; }
        public double BackendLastCallbackAgoMs { get; set; }
        public double BackendLastCallbackCompleteAgoMs { get; set; }
        public double BackendStopStageAgeMs { get; set; }
        public double LastPublishAgoMs { get; set; }
        public double LastConsumeAgoMs { get; set; }
        public bool EncoderWorkerAlive { get; set; }
        public uint RemoteClockLateTicks { get; set; }
        public uint BackendCallbackStage { get; set; }
        public uint BackendCallbackSubstage { get; set; }
        public ulong BackendCurrentDrainFrames { get; set; }
        public double BackendCallbackStageAgeMs { get; set; }
        public uint CoreWgcStage { get; set; }
        public uint RingPublishStage { get; set; }
        public uint EncoderWorkerStage { get; set; }
        public double CoreWgcStageAgeMs { get; set; }
        public double RingPublishStageAgeMs { get; set; }
        public double EncoderWorkerStageAgeMs { get; set; }
        public ulong RingAcquireCalls { get; set; }
        public ulong RingAcquireNoReadyFrames { get; set; }
        public ulong RingAcquireReadySelections { get; set; }
        public ulong RingCaptureFenceReady { get; set; }
        public ulong RingCaptureFencePending { get; set; }
        public ulong RingPublishNoReusableSlots { get; set; }
        public double RingAcquireWaitMs { get; set; }
        public double RingAverageAcquireWaitMs { get; set; }
        public double RingLeaseAgeMs { get; set; }
        public double RingAverageLeaseAgeMs { get; set; }
        public ulong RingCaptureFenceLag { get; set; }
        public ulong RingMaxCaptureFenceLag { get; set; }
        public uint RingReleaseFencePendingSlots { get; set; }

        public ulong BackendFramesDelivered { get; set; }
        public ulong BackendPendingFramesReplaced { get; set; }
        public bool BackendDeliveryWorkerAlive { get; set; }
        public uint AdaptivePressureState { get; set; }
        public uint AdaptiveTargetFps { get; set; }
        public uint AdaptivePressureStreak { get; set; }
        public uint AdaptiveHealthyStreak { get; set; }
        public uint AdaptiveTransitions { get; set; }
        public double BackendLastDeliveryDelayMs { get; set; }
        public uint CaptureRingOccupiedFrames { get; set; }
        public ulong CopyGpuProbeSignals { get; set; }
        public ulong CopyGpuProbeCompleted { get; set; }
        public ulong CopyGpuReadyAtSubmit { get; set; }
        public ulong CopyGpuNotReadyAtSubmit { get; set; }
        public uint CopyGpuProbePending { get; set; }
        public uint CopyGpuProbeMaxPending { get; set; }
        public double CopyGpuLastLatencyMs { get; set; }
        public double CopyGpuAverageLatencyMs { get; set; }
        public double CopyGpuMaxLatencyMs { get; set; }
        public double CopyGpuOldestPendingMs { get; set; }
        public int CopyGpuProbeStatus { get; set; }
    }

    internal sealed class EncoderConfig
    {
        public uint BitrateKbps { get; set; } = 50_000;
        public uint Fps { get; set; } = 60;

        // 0=Auto, 1=P1, 2=P2, 3=P3, 4=P4
        public uint Preset { get; set; } = 0;

        public bool UltraLowLatency { get; set; } = true;

        // Legacy/default cap used by fallback pipelines. CORE 11.15 mode 9
        // intentionally ignores this cap and encodes the host's native even
        // resolution; the existing client renderer performs presentation scale.
        public uint MaxWidth { get; set; } = 1920;
        public uint MaxHeight { get; set; } = 1080;

        // CORE 11.17 default: 10=WGC latest-worker + same-device ring/NVENC + adaptive relief.
        // Fallbacks: 9=native cross-device copy, 8=encode-side RGB scaler,
        // 7=direct-ring RGB, 6=NV12/VP; 5/4 diagnostics; 1/2 legacy; 3 synthetic.
        public uint LabInputMode { get; set; } = 10;
    }

    internal sealed class EncoderStats
    {
        public bool Running { get; set; }
        public int LastStatus { get; set; }

        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint TargetFps { get; set; }
        public uint BitrateKbps { get; set; }

        public ulong EncodedFrames { get; set; }
        public ulong EncodedBytes { get; set; }

        public ulong PacingDrops { get; set; }
        public ulong QueueResets { get; set; }

        public uint QueuedPackets { get; set; }
        public uint QueuedBytes { get; set; }

        public double LastEncodeMs { get; set; }
        public double AverageEncodeMs { get; set; }

        public ulong SubmittedFrames { get; set; }
        public ulong BackpressureDrops { get; set; }

        public uint InFlightFrames { get; set; }
        public uint InputSlots { get; set; }

        public double LastSubmitMs { get; set; }
        public double AverageSubmitMs { get; set; }

        public double LastOutputLatencyMs { get; set; }
        public double AverageOutputLatencyMs { get; set; }

        public int InputMode { get; set; }
        public int RequestedInputMode { get; set; }
        public ulong MapFailures { get; set; }
        public ulong EncodeErrors { get; set; }
        public ulong LockBusyRetries { get; set; }
        public double LastPreprocessMs { get; set; }
        public double AveragePreprocessMs { get; set; }
        public double LastMapMs { get; set; }
        public double AverageMapMs { get; set; }
        public double LastEncodeCallMs { get; set; }
        public double AverageEncodeCallMs { get; set; }
        public double LastLockBitstreamMs { get; set; }
        public double AverageLockBitstreamMs { get; set; }
        public double StatsReadWaitMs { get; set; }
        public uint PendingCommands { get; set; }
        public uint SourceMode { get; set; }
        public ulong NeedMoreInputAccepted { get; set; }
        public ulong EncoderBusyRetries { get; set; }
        public ulong NoFreeSlotDrops { get; set; }
        public uint PendingOutputs { get; set; }
        public ulong FlushTimeouts { get; set; }
        public double OldestInFlightMs { get; set; }
        public uint Core10PipelineMode { get; set; }
        public bool CaptureDeviceIsolated { get; set; }
        public bool Core11GpuPriority { get; set; }
        public int CaptureGpuPriority { get; set; }
        public int EncodeGpuPriority { get; set; }
        public int GpuPrioritySetStatus { get; set; }
        public int ProcessGpuPriorityRequested { get; set; }
        public int ProcessGpuPriorityEffective { get; set; }
        public int ProcessGpuPrioritySetStatus { get; set; }
        public int ProcessGpuPriorityRestoreStatus { get; set; }
        public ulong PreprocessCopyFenceSignals { get; set; }
        public ulong PreprocessCopyFenceCompleted { get; set; }
        public uint PreprocessCopyFencePending { get; set; }
        public uint PreprocessCopyFenceMaxPending { get; set; }
        public double PreprocessCopyFenceOldestAgeMs { get; set; }
        public ulong VideoProcessorFenceSignals { get; set; }
        public ulong VideoProcessorFenceCompleted { get; set; }
        public uint VideoProcessorFencePending { get; set; }
        public uint VideoProcessorFenceMaxPending { get; set; }
        public double VideoProcessorFenceOldestAgeMs { get; set; }
        public bool EncoderWorkerAlive { get; set; }
        public bool OutputWorkerAlive { get; set; }
        public bool StopInProgress { get; set; }
        public uint StopStage { get; set; }
        public double LastSubmitAgoMs { get; set; }
        public double LastOutputAgoMs { get; set; }
        public double StopStageAgeMs { get; set; }
        public uint NvencStopStage { get; set; }
        public double NvencStopStageAgeMs { get; set; }
    }

    internal sealed class EncoderStartResult
    {
        public int HResult { get; set; }
        public string Message { get; set; } = "";

        public bool Success =>
            HResult >= 0;
    }

    internal sealed class EncodedPacket
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public ulong FrameId { get; set; }
        public bool KeyFrame { get; set; }
    }

    internal sealed class NativeNvdecSession :
        IDisposable
    {
        private readonly object _sync =
            new();

        private IntPtr _handle;

        public NativeNvdecSession(
            IntPtr handle,
            uint previewWidth,
            uint previewHeight,
            uint previewStride,
            uint previewBytes)
        {
            _handle = handle;

            PreviewWidth = previewWidth;
            PreviewHeight = previewHeight;
            PreviewStride = previewStride;

            PreviewBuffer =
                previewBytes > 0
                    ? new byte[checked((int)previewBytes)]
                    : Array.Empty<byte>();
        }

        public uint PreviewWidth { get; private set; }
        public uint PreviewHeight { get; private set; }
        public uint PreviewStride { get; private set; }

        public byte[] PreviewBuffer { get; private set; }

        public int Push(
            EncodedPacket packet)
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? -1
                    : NativeCoreService.DecoderPush(
                        _handle,
                        packet);
            }
        }

        public int SetHeadlessOutput(
            bool enabled)
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? -1
                    : NativeCoreService.DecoderSetHeadlessOutput(
                        _handle,
                        enabled);
            }
        }

        public int AttachSwapChainPanel(
            IntPtr panelUnknown,
            uint width,
            uint height)
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? -1
                    : NativeCoreService.DecoderAttachSwapChainPanel(
                        _handle,
                        panelUnknown,
                        width,
                        height);
            }
        }

        public int ResizeSwapChain(
            uint width,
            uint height)
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? -1
                    : NativeCoreService.DecoderResizeSwapChain(
                        _handle,
                        width,
                        height);
            }
        }

        public int SetCompositionScale(float scaleX, float scaleY)
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero ? -1 :
                    NativeCoreService.DecoderSetCompositionScale(_handle, scaleX, scaleY);
            }
        }

        public void DetachSwapChainPanel()
        {
            lock (_sync)
            {
                if (_handle != IntPtr.Zero)
                {
                    NativeCoreService.DecoderDetachSwapChainPanel(
                        _handle);
                }
            }
        }

        public D3D11RendererStats? GetRendererStats()
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? null
                    : NativeCoreService.DecoderRendererStats(
                        _handle);
            }
        }

        public int SignalDiscontinuity()
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? -1
                    : NativeCoreService.DecoderSignalDiscontinuity(
                        _handle);
            }
        }

        public NvdecStats? GetStats()
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? null
                    : NativeCoreService.DecoderStats(
                        _handle);
            }
        }

        public NvdecPreviewResult ReadLatestPreview()
        {
            lock (_sync)
            {
                if (_handle == IntPtr.Zero)
                {
                    return new NvdecPreviewResult
                    {
                        Status = -1
                    };
                }

                NvdecStats? stats =
                    NativeCoreService.DecoderStats(
                        _handle);

                if (stats is not null &&
                    stats.PreviewWidth > 0 &&
                    stats.PreviewHeight > 0 &&
                    (
                        stats.PreviewWidth != PreviewWidth ||
                        stats.PreviewHeight != PreviewHeight
                    ))
                {
                    PreviewWidth =
                        stats.PreviewWidth;

                    PreviewHeight =
                        stats.PreviewHeight;

                    PreviewStride =
                        PreviewWidth * 4u;

                    PreviewBuffer =
                        new byte[
                            checked(
                                (int)(
                                    PreviewStride *
                                    PreviewHeight))];
                }

                if (PreviewBuffer.Length == 0)
                {
                    return new NvdecPreviewResult
                    {
                        Status = 1
                    };
                }

                int result =
                    NativeCoreService.DecoderCopyPreview(
                        _handle,
                        PreviewBuffer,
                        out ulong frameId);

                return new NvdecPreviewResult
                {
                    Status = result,
                    FrameId = frameId,
                    HasNewFrame = result == 0
                };
            }
        }

        public string GetMessage()
        {
            lock (_sync)
            {
                return _handle == IntPtr.Zero
                    ? "NVDEC fechado."
                    : NativeCoreService.DecoderMessage(
                        _handle);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                IntPtr handle =
                    _handle;

                _handle =
                    IntPtr.Zero;

                if (handle != IntPtr.Zero)
                    NativeCoreService.DestroyDecoder(
                        handle);
            }

            GC.SuppressFinalize(this);
        }

        ~NativeNvdecSession()
        {
            Dispose();
        }
    }

    internal sealed class NvdecOpenResult
    {
        public int Status { get; set; }
        public string Error { get; set; } = "";
        public NativeNvdecSession? Session { get; set; }

        public bool Success =>
            Status >= 0 &&
            Session is not null;
    }

    internal sealed class NvdecStats
    {
        public bool Running { get; set; }
        public bool FormatReady { get; set; }
        public int LastStatus { get; set; }

        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint PreviewWidth { get; set; }
        public uint PreviewHeight { get; set; }

        public ulong SubmittedFrames { get; set; }
        public ulong DecodedPictures { get; set; }
        public ulong DisplayedFrames { get; set; }
        public ulong PreviewFrames { get; set; }

        public double LastDecodeLatencyMs { get; set; }
        public double AverageDecodeLatencyMs { get; set; }
    }

    internal sealed class D3D11RendererStats
    {
        public bool Attached { get; set; }
        public int LastStatus { get; set; }

        public uint TargetWidth { get; set; }
        public uint TargetHeight { get; set; }
        public uint SourceWidth { get; set; }
        public uint SourceHeight { get; set; }

        public ulong SubmittedFrames { get; set; }
        public ulong PresentedFrames { get; set; }
        public ulong DroppedPresents { get; set; }
        public ulong ReplacedPendingFrames { get; set; }
        public ulong PresentWaitTimeouts { get; set; }

        public ulong PresenterDequeuedFrames { get; set; }
        public ulong FrameLatencySignals { get; set; }
        public ulong FrameLatencyBypasses { get; set; }
        public ulong FrameLatencyWaitFailures { get; set; }
        public ulong PresentAttempts { get; set; }
        public ulong PresentStillDrawing { get; set; }
        public ulong PresentFailures { get; set; }

        public uint ForegroundProcessId { get; set; }
        public bool ForegroundIsCurrentProcess { get; set; }
        public uint PresenterStage { get; set; }
        public bool PresentInFlight { get; set; }

        public double LastGpuCopyMs { get; set; }
        public double AverageGpuCopyMs { get; set; }

        public double LastPresentSubmitMs { get; set; }
        public double AveragePresentSubmitMs { get; set; }
        public double LastPresentWaitMs { get; set; }
        public double AveragePresentWaitMs { get; set; }

        public double LastQueueToPresenterMs { get; set; }
        public double AverageQueueToPresenterMs { get; set; }
        public double LastStateLockWaitMs { get; set; }
        public double AverageStateLockWaitMs { get; set; }
        public double LastPresentCallMs { get; set; }
        public double AveragePresentCallMs { get; set; }
        public double LastFrameAgeAtPresentMs { get; set; }
        public double AverageFrameAgeAtPresentMs { get; set; }
        public double PresenterActivityAgeMs { get; set; }
    }

    internal sealed class NvdecPreviewResult
    {
        public int Status { get; set; }
        public bool HasNewFrame { get; set; }
        public ulong FrameId { get; set; }
    }

    internal sealed class CaptureOpenResult
    {
        public int HResult { get; set; }
        public string? Error { get; set; }
        public NativeCaptureSession? Session { get; set; }
    }

    internal sealed class CaptureReadResult
    {
        public int HResult { get; set; }
        public bool HasNewFrame { get; set; }

        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint Stride { get; set; }

        public ulong FrameId { get; set; }
    }

    internal sealed class HardwareSnapshot
    {
        public bool NativeCoreLoaded { get; set; }
        public int ApiVersion { get; set; }

        public bool Direct3D11Ready { get; set; }
        public int Direct3D11HResult { get; set; }

        public bool NvencCompiled { get; set; }
        public bool NvencRuntimeAvailable { get; set; }

        public string? Error { get; set; }

        public List<GpuInfo> Gpus { get; } = new();
        public List<MonitorInfo> Monitors { get; } = new();
    }

    internal sealed class GpuInfo
    {
        public string Name { get; set; } = "";

        public ulong DedicatedVideoMemory { get; set; }

        public uint VendorId { get; set; }
        public uint DeviceId { get; set; }

        public double DedicatedMemoryGb =>
            DedicatedVideoMemory /
            1024d /
            1024d /
            1024d;
    }

    internal sealed class MonitorInfo
    {
        public string DeviceName { get; set; } = "";

        public int Left { get; set; }
        public int Top { get; set; }

        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint RefreshRate { get; set; }

        public bool IsPrimary { get; set; }

        public bool DesktopDuplicationReady { get; set; }
        public int DesktopDuplicationHResult { get; set; }

        public override string ToString()
        {
            return
                $"{(IsPrimary ? "Principal • " : "")}" +
                $"{Width} × {Height} • " +
                $"{(RefreshRate > 0 ? $"{RefreshRate} Hz" : "Hz ?")} • " +
                $"{DeviceName}";
        }
    }
}
