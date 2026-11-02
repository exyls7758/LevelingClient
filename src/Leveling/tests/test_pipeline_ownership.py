from pathlib import Path
import re, unittest
root=Path(__file__).resolve().parents[1]
class Ownership(unittest.TestCase):
    def test_pages_and_runtime_only_send_encoder_intentions(self):
        for name in ['Views/DevicesPage.xaml.cs','Services/RemoteStreamingRuntime.cs']:
            s=(root/name).read_text(encoding='utf-8-sig')
            self.assertFalse(re.search(r'\b(?:_capture|capture|captureSession)\??\.(?:StartEncoder|StopEncoder|RequestIdr|Dispose)\b',s),name)
            self.assertNotIn('NativeCoreService.OpenCapture(',s)
    def test_manual_page_does_not_create_host_sender(self):
        s=(root/'Views/DevicesPage.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertNotIn('new RemoteHostVideoTransport(',s)
        self.assertNotIn('new LoopbackVideoTransport(',s)
    def test_no_native_fatal_autorecreate(self):
        s=(root/'Multiverse.Core/MultiverseCore.cpp').read_text(encoding='utf-8-sig')
        self.assertNotIn('RecycleFatalEncoderIfNeeded',s)
if __name__=='__main__': unittest.main()
