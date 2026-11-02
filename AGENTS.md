# Leveling release process

The application source is src/Leveling; preserve the public landing page at index.html.
For an authorized Leveling update: implement, run scripts/Test.ps1, increment the version using scripts/Bump-Version.ps1, update RELEASE_NOTES.md, build Release x64 with NVENC and NVDEC enabled, refresh scripts/NativeArtifact.ps1 -Refresh, package, review the staged diff, commit and push. scripts/Release.ps1 -Summary implements the local release sequence. GitHub Actions validates/tests/builds/packages and publishes when main changes. Confirm the workflow and actual release assets before declaring publication complete.
Never commit diagnostic archives, credentials, runtime settings, SDK source packages, or build intermediates. Never publish a native DLL against changed source hashes. GPU/WAN/manual checks cannot be claimed by the hosted runner. Keep release notes specific and report unvalidated scenarios.
