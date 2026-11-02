using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Multiverse.Services;

// Explicit opt-in diagnostic: captures this PC and sends only to loopback.
// No remote session, friend authorization, or saved stream setting is changed.
internal static class VideoLocalSelfTest
{
    internal static async Task RunAsync(string output, bool qhd, int seconds = 15, uint fps = 60)
    {
        NativeCaptureSession? capture = null;
        NativeNvdecPlayer? decoder = null;
        VideoUdpReceiver? receiver = null;
        VideoUdpSender? sender = null;
        RemoteSessionWindow? window = null;
        Window? motionSource = null;
        int exit = 1;
        void Log(string line) => File.AppendAllText(output, DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            seconds = Math.Clamp(seconds, 15, 300);
            if (fps is not (30 or 60 or 80 or 90 or 120)) throw new ArgumentOutOfRangeException(nameof(fps));
            // WGC may legitimately publish fewer frames on a static desktop.
            // An opt-in compositor animation gives the extended test real motion.
            if (seconds > 15)
            {
                var tile = new Microsoft.UI.Xaml.Controls.Border { Width=60, Height=60,
                    Background=new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DeepPink),
                    HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Center };
                motionSource = new Window { Title="Leveling • Movimento do teste local", Content=tile };
                motionSource.Activate();
                motionSource.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(20,20,420,180));
                var visual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(tile);
                var animation=visual.Compositor.CreateVector3KeyFrameAnimation();
                animation.InsertKeyFrame(0,Vector3.Zero); animation.InsertKeyFrame(.5f,new Vector3(300,0,0)); animation.InsertKeyFrame(1,Vector3.Zero);
                animation.Duration=TimeSpan.FromSeconds(2);
                animation.IterationBehavior=Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                visual.StartAnimation("Offset",animation);
            }
            Log("START " + BuildInfo.DisplayVersion + " requested=" + (qhd ? "1440p" : "1080p") + fps);
            var hardware = await Task.Run(NativeCoreService.ReadHardware);
            var monitor = hardware.Monitors.FirstOrDefault(m => m.IsPrimary && m.DesktopDuplicationReady)
                ?? throw new InvalidOperationException("No primary capture monitor");
            var opened = await Task.Run(() => NativeCoreService.OpenCapture(monitor.DeviceName));
            capture = opened.Session ?? throw new InvalidOperationException(opened.Error);
            var config = new EncoderConfig { Fps = fps, MaxWidth = qhd ? 2560u : 1920u,
                MaxHeight = qhd ? 1440u : 1080u, BitrateKbps = 20000, Preset = 1, UltraLowLatency = true, LabInputMode = 10 };
            var started = await Task.Run(() => capture.StartEncoder(config));
            if (!started.Success) throw new InvalidOperationException(started.Message);
            var encoder = capture.GetEncoderStats() ?? throw new InvalidOperationException("No encoder stats");
            if (encoder.TargetFps != fps) throw new InvalidOperationException("Encoder FPS differs from requested target");
            Log($"ENCODER actual={encoder.Width}x{encoder.Height} fps={encoder.TargetFps}");
            decoder = new NativeNvdecPlayer(encoder.Width, encoder.Height, () => capture.RequestIdr());
            decoder.Start();
            window = new RemoteSessionWindow(decoder);
            window.ShowWindowed();
            const uint stream = 7812301;
            receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, false, IPAddress.Loopback, stream);
            receiver.FrameCompleted += decoder.PushFrame;
            receiver.ReferenceLost += () => capture.RequestIdr();
            receiver.Start();
            sender = new VideoUdpSender(capture, new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), 20000, fps, null, stream);
            sender.Start();
            capture.RequestIdr();
            ulong firstPresented = 0;
            long baseHandles=0, basePrivate=0;
            var elapsed=Stopwatch.StartNew();
            for (int second = 1; second <= seconds; second++)
            {
                await Task.Delay(1000);
                var d = decoder.GetSnapshot(); var rx = receiver.GetSnapshot();
                if (second == 5) firstPresented = d.D3D11PresentedFrames;
                var c=capture.GetCapturePipelineStatsDiagnostic(); var e=capture.GetEncoderStatsDiagnostic();
                using var process=Process.GetCurrentProcess(); process.Refresh();
                if(second==10){baseHandles=process.HandleCount; basePrivate=process.PrivateMemorySize64;}
                Log($"SAMPLE second={second} elapsedMs={elapsed.ElapsedMilliseconds} captured={c?.BackendFramesPublished} encoded={e?.EncodedFrames} received={d.NetworkFramesReceived} submitted={d.NativeFramesSubmitted} decoded={d.DecodedPictures} attached={d.D3D11Attached} presented={d.D3D11PresentedFrames} queueP95ms={d.LocalQueueAgeP95Ms:0.0} handles={process.HandleCount} privateMB={process.PrivateMemorySize64/1048576} error={d.LastError}");
                if(second==seconds) Log($"RESOURCES since10s handlesDelta={process.HandleCount-baseHandles} privateMBDelta={(process.PrivateMemorySize64-basePrivate)/1048576}");
            }
            var final = decoder.GetSnapshot();
            var sent = sender.GetSnapshot();
            var received = receiver.GetSnapshot();
            var encoded = capture.GetEncoderStats();
            Log($"COUNTERS encoded={encoded?.EncodedFrames ?? 0} sentPackets={sent.SentPackets} receivedPackets={received.ReceivedPackets} decoded={final.DecodedPictures} presented={final.D3D11PresentedFrames}");
            if (sent.SentPackets == 0 || received.ReceivedPackets == 0 || (encoded?.EncodedFrames ?? 0) == 0)
                throw new InvalidOperationException("Capture/encoder/UDP did not produce real traffic");
            if (!window.IsOpen || !final.D3D11Attached || final.DecodedPictures < 30 || final.D3D11PresentedFrames < firstPresented + 30)
                throw new InvalidOperationException("No sustained decoded/presented video (packet delivery alone is not PASS)");
            Log("PASS WGC -> NVENC -> UDP loopback -> NVDEC -> D3D11 window; WAN and input latency not tested");
            exit = 0;
        }
        catch (Exception ex) { Log("FAIL " + ex); }
        finally
        {
            try
            {
                sender?.Stop(); sender?.Dispose(); receiver?.Stop(); receiver?.Dispose();
                if (window is not null) await window.DetachRendererAsync();
                decoder?.Stop(); decoder?.Dispose();
                if (capture is not null) await Task.Run(() => { capture.StopEncoder(); capture.Dispose(); });
                Log("CLEANUP confirmed");
                if (window is not null) await window.CloseAsync();
                motionSource?.Close();
            }
            catch (Exception ex) { Log("CLEANUP_FAIL " + ex); exit = 1; }
            Environment.ExitCode = exit;
            Application.Current.Exit();
        }
    }
}
