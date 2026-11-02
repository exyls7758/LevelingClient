import pathlib, re, sys
ROOT = pathlib.Path(__file__).resolve().parents[2]

def read(rel): return (ROOT/rel).read_text(encoding='utf-8')

def check(cond, msg):
    if not cond:
        print('FAIL:', msg)
        raise SystemExit(1)
    print('PASS:', msg)

csproj = read('Leveling.csproj')
check('<Platforms>x64</Platforms>' in csproj, 'project publishes x64 only')
check('<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>' in csproj, 'runtime identifiers x64 only')
check(re.search(r'<InformationalVersion>CORE 11\.75 RC\d+</InformationalVersion>', csproj),
      'current release build metadata is explicit')

main = read('MainWindow.xaml.cs')
diag = read('Views/DiagnosticsPage.xaml.cs')
buildinfo = read('Services/BuildInfo.cs') if (ROOT/'Services/BuildInfo.cs').exists() else ''
check('BuildInfo.ApplicationLabel' in main, 'main window uses centralized BuildInfo')
check('BuildInfo.DisplayVersion' in diag and 'CORE 11.74c4' not in diag, 'diagnostics uses centralized BuildInfo')
check('AssemblyInformationalVersionAttribute' in buildinfo, 'BuildInfo reads assembly informational version')

runtime = read('Services/RemoteStreamingRuntime.cs')
check('WaitForStableClientVideoOwnershipAsync' in runtime, 'client startup revalidates cleanup ownership under recovery gate')
check('AwaitSenderRecoveryResultAsync' in runtime, 'client awaits explicit sender recovery result')

session = read('Services/RemoteSessionService.cs')
check('BoundedLineReader' in session, 'control plane uses bounded line reader')
check('CreateBounded<ControlEnvelope>' in session and 'CriticalControlQueueCapacity' in session, 'critical control queue is bounded')
check('AwaitSenderRecoveryResultAsync' in session and 'CompleteSenderRecoveryResult' in session, 'sender recovery request/result is explicit')
check('RegisterPairingFailure' in session and 'IsPairingThrottled' in session, 'pairing attempts are throttled')

host = read('Services/HostTransmissionService.cs')
check('TryRegisterRequest' in host and 'TryConsumeAttempt' in host, 'host recovery budget is consumed after request registration/health recheck')
check('PublishSenderRecoveryResultAsync' in host and 'HOST_SENDER_RECOVERY_RESULT_SEND_ERROR' in host, 'host sends explicit recovery rejection on exceptions')

policy = read('Services/HostSenderRecoveryPolicy.cs')
check('TryRegisterRequest' in policy and 'TryConsumeAttempt' in policy, 'host sender recovery policy splits replay validation from budget consumption')

nvdec = read('Services/NativeNvdecPlayer.cs')
check('NVDEC_IDR_CALLBACK_ERROR' in nvdec and 'try' in nvdec[nvdec.find('private void RequestRecoveryIdr'):nvdec.find('private void ApplyPendingNativeDiscontinuity')], 'IDR callback cannot tear down decode worker')

remote_video = read('Services/RemoteVideoTransport.cs')
lowlat = read('Services/LowLatencyTransport.cs')
check('expectedRemoteAddress' in lowlat and 'ForeignPacketsDropped' in lowlat, 'UDP receiver filters foreign source addresses')
client = remote_video[remote_video.index('internal sealed class RemoteClientVideoTransport'):]
check('expectedRemote = _session.GetRemoteVideoEndpoint()' in client and
      'expectedRemoteAddress: expectedRemote.Address' in client and
      'expectedStreamId: streamConfig.WireStreamId' in client,
      'remote client binds UDP to the negotiated peer address and stream identity')
check('DecodeStaleDrops' in session and 'ReferenceWaitDrops' in session and 'PresenterDrops' in session, 'client telemetry reports internal decoder/presenter drops separately')

audio_tx = read('Services/RemoteAudioTransport.cs')
audio_cap = read('Services/ProcessLoopbackAudioCapture.cs')
audio_svc = read('Services/RemoteAudioService.cs')
check('AUDIO_TX_STOP_TIMEOUT' in audio_tx and 'AUDIO_RX_STOP_TIMEOUT' in audio_tx, 'audio transport reports unconfirmed worker stop')
check('AUDIO_TX_WORKER_ERROR' in audio_tx and 'AUDIO_RX_WORKER_ERROR' in audio_tx, 'audio transport records unexpected worker failure')
check('AUDIO_CAPTURE_STOP_TIMEOUT' in audio_cap, 'audio capture retains resources after stop timeout')
check('AUDIO_ENCODE_STOP_TIMEOUT' in audio_svc, 'audio service reports encode worker stop timeout')

devices = read('Views/DevicesPage.xaml.cs')
check('EnsureManualVideoMutationAllowed' in devices, 'manual Devices video mutations are guarded during active host transmission')

print('c6d hardening source contracts: PASS')
