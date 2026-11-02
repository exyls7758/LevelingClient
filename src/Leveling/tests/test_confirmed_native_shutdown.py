"""Source guard for native teardown ordering; real join behavior is tested in managed/native harnesses."""
from pathlib import Path
import unittest
root = Path(__file__).resolve().parents[1]
class ShutdownGuards(unittest.TestCase):
    def test_wgc_never_ignores_callback_timeout_or_join_failure(self):
        s = (root / 'Multiverse.Core/Core10WgcCapture.cpp').read_text(encoding='utf-8-sig')
        drain = s[s.index('m_impl->SetStopStage(6)'):s.index('m_impl->SetStopStage(8)')]
        self.assertIn('while (!m_impl->CallbackDrainCv.wait_for(', drain)
        self.assertNotIn('catch (...) {}', drain.split('winrt::Windows')[0])
    def test_submissions_can_be_blocked_before_sender_stop(self):
        s = (root / 'Multiverse.Core/MultiverseCore.cpp').read_text(encoding='utf-8-sig')
        self.assertIn('MV_EncoderBlockSubmissions', s)
if __name__ == '__main__': unittest.main()
