from pathlib import Path
import unittest

root = Path(__file__).resolve().parents[1]

class ClientWindowStartup(unittest.TestCase):
    def test_ui_owner_is_bound_before_connections_start(self):
        main = (root / 'MainWindow.xaml.cs').read_text(encoding='utf-8-sig')
        self.assertLess(main.index('RemoteStreamingRuntime.Instance.InitializeUi(DispatcherQueue)'),
                        main.index('NavigateTo(typeof(HomePage)'))

    def test_dispatch_is_observed_and_serialized_with_decoder_cleanup(self):
        runtime = (root / 'Services/RemoteStreamingRuntime.cs').read_text(encoding='utf-8-sig')
        method = runtime[runtime.index('public async Task ShowSessionWindowAsync()'):runtime.index('private void SessionWindow_Closed')]
        self.assertIn('await _recoveryGate.WaitAsync()', method)
        self.assertIn('await operation.RunAsync', method)
        self.assertIn('_recoveryGate.Release()', method)

    def test_attach_failures_are_reported_and_retried(self):
        window = (root / 'Services/RemoteSessionWindow.cs').read_text(encoding='utf-8-sig')
        self.assertIn('ReportPresentationFailure', window)
        self.assertIn('_presentationRetry.TrySchedule', window)

if __name__ == '__main__': unittest.main()
