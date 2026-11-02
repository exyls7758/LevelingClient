# Native GPU smoke harness

This Windows CLI test loads `Multiverse.Core.dll` from the absolute path passed
on the command line. It uses the ABI declarations from
`Multiverse.Core/MultiverseCore.h` and resolves the real DLL exports with
`GetProcAddress`, so it does not depend on copying the DLL beside the test.

From an x64 Visual Studio developer command prompt at the repository root:

```bat
cl /nologo /std:c++20 /EHsc /W4 /WX /I candidate candidate\tests\native-smoke\native_smoke.cpp /Fe:candidate\tests\native-smoke\native_smoke.exe /Fo:candidate\tests\native-smoke\native_smoke.obj
candidate\tests\native-smoke\native_smoke.exe "C:\absolute\path\to\Multiverse.Core.dll"
```

The executable tests real adapter and monitor enumeration, D3D11 and Desktop
Duplication availability, capture creation, and 20 mode-10 WGC/NVENC
start/IDR/packet/stop cycles. Cycle 1 is the warmed resource baseline; cycles
2-20 are checked for linear handle or thread growth. A mode-3 synthetic cycle
runs only as a diagnostic if mode 10 cannot start and never converts that
failure into a pass.

Packet payloads are discarded in memory immediately after their size, frame
ID, and key-frame flag are recorded. The harness does not copy, save, or log
captured pixels or encoded payload bytes. Run it from a parent process with a
timeout because the DLL stop functions are synchronous.
