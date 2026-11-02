from pathlib import Path
import re
root = Path(__file__).resolve().parents[2]
s = (root/'Multiverse.Core/MultiverseCore.cpp').read_text()
for name, next_name in [('SubmitDirectRingFrame','SubmitSameDeviceRingFrame'), ('SubmitSameDeviceRingFrame','SubmitFrame')]:
    body = s[s.index('HRESULT '+name+'('):s.index('HRESULT '+next_name+'(', s.index('HRESULT '+name+'(')+1)]
    assert body.index('ShouldDeferNewFrame()') < body.index('ConsumeForceIdr()'), name
    assert 'PreserveIdrOnFailedSubmit(status, forceIdr)' in body, name
body = s[s.index('HRESULT SubmitFrame('):s.index('\n#endif', s.index('HRESULT SubmitFrame('))]
assert body.index('ShouldDeferNewFrame()') < body.index('ConsumeForceIdr()')
assert 'PreserveIdrOnFailedSubmit(status, forceIdr)' in body
assert 'm_encoder->RestoreForceIdr()' in s
assert 'm_queueAdmissionPolicy.ShouldDefer(' in s
assert s.index('const NVENCSTATUS progress = Pump();', s.index('bool ShouldDeferNewFrame()')) < s.index('m_queueAdmissionPolicy.ShouldDefer(')
admission = s[s.index('bool ShouldDeferNewFrame()'):s.index('void RestoreForceIdr()', s.index('bool ShouldDeferNewFrame()'))]
assert admission.index('const NVENCSTATUS progress = Pump();') < admission.index('const auto queue = m_sharedPacketQueue->GetState();'), 'admission must observe the queue after retry progress'

print('native submission contracts: PASS')
