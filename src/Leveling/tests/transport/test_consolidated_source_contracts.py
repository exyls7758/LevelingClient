"""Source-level safety gates. These are NOT C# compilation or integration tests."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[2]
def text(p): return (root/p).read_text(encoding='utf-8-sig')
def check(value,message):
    if not value: raise AssertionError(message)
    print('PASS:',message)
def section(s,start,end): return s[s.index(start):s.index(end,s.index(start)+len(start))]
core=text('Services/HostTransmissionService.cs')
rt=text('Services/RemoteStreamingRuntime.cs')
transport=text('Services/RemoteVideoTransport.cs')
page=text('Views/DevicesPage.xaml.cs')
# The historical blanket restart disable was replaced by an ownership-checked
# recovery transaction. Runtime behavior is covered by the integration suite.
recovery=text('Services/HostVideoPipelineController.Recovery.cs')
check('EncoderStallDetector.HasEncoderFault(fresh)' in recovery and
      '_encoderStopTask is not null || _captureDisposeTask is not null' in recovery,
      'encoder recovery requires fault evidence and confirmed old-resource cleanup')
check('HostTransmissionService.Instance.ObserveEncoderHealth(session, c, e)' in rt and
      'NativeCoreService.OpenCapture(' not in rt,
      'watchdog delegates encoder ownership to the controller')
check('!h.IsCurrentStream || !h.RemoteEndpoint.Equals(endpoint)' in rt,
      'watchdog detects a stale sender even when the endpoint has not changed')
check('ConfirmedThread.JoinOrThrow(thread, 1500' in text('Services/LowLatencyTransport.cs') and
      'ConfirmedThread.JoinOrThrow(thread, 750' in text('Services/LowLatencyTransport.cs') and
      'ConfirmedThread.JoinOrThrow(thread, 1000' in text('Services/NativeNvdecPlayer.cs'),
      'all three worker shutdowns require a confirmed join')
check('ConcurrentQueue<ReassembledVideoFrame>' not in text('Services/NativeNvdecPlayer.cs') and
      'CodedFrameQueue<ReassembledVideoFrame>' in text('Services/NativeNvdecPlayer.cs'),
      'decoder queue uses coherent depth bookkeeping')
check('CanUseManualPipeline(_capture)' in page and 'OwnsCapture(capture)' in page and
      'MANUAL_CAPTURE_STOP_INCOMPLETE' in page,
      'manual page guards service ownership and retains failed stop resources')
for name,begin,end in [('host','internal sealed class RemoteHostVideoTransport','internal sealed class RemoteClientVideoTransport'),
                       ('client','internal sealed class RemoteClientVideoTransport','\n    }\n\n}')]:
    body=section(transport,begin,end)
    dispose=body[body.index('public void Dispose()'):]
    check(dispose.index('Stop();') < dispose.index('_disposed = true;'),
          name+' wrapper does not mark disposed before successful Stop')
print('Consolidated source contracts: PASS (not a compiler)')
