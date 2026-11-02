"""Compile the exact protocol/receiver source, excluding only the NVENC sender.
Run this before dotnet run; Receiver.generated.cs is derived, never manually edited.
"""
from pathlib import Path
p=Path(__file__).resolve().parent
s=(p/'../../Services/LowLatencyTransport.cs').resolve().read_text(encoding='utf-8-sig')
head=s[:s.index('    internal sealed class TransportSnapshot')]
tail=s[s.index('    internal sealed class VideoUdpReceiverSnapshot'):]
(p/'Receiver.generated.cs').write_text('#nullable enable\n'+head+tail,encoding='utf-8')
print('Extracted exact current protocol + receiver for live UDP tests')
