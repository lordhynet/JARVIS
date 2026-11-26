# Retained verification settings

The isolated deployment rehearsal generates synthetic database/API/owner credentials under the OS temporary directory in `jarvis-verify-<10 hex characters>`. Successful cleanup removes both generated settings files and their nonsecret ownership marker. If Docker cleanup fails, the error names that directory and a cleanup-only command; credential values are never printed.

After removing that verification project's containers, run from Core:

```powershell
./scripts/test-deployment.ps1 -CleanupProject jarvis-verify-0123456789
```

Use the exact project from the failure. Cleanup inspects only that project label and removes only `appsettings.json`, `restored-appsettings.json` and its matching ownership marker under the temporary root. It refuses existing containers, Docker inspection failures, missing/mismatched ownership and linked paths/files. It does not start/stop containers, migrate, scan other temporary directories, read credential contents or delete backups/reports/unrelated files. Repeated cleanup is harmless once the settings are absent.

Older retained fixtures without an ownership marker are deliberately refused; inspect and remove their exact generated files manually after confirming ownership. This command is for synthetic verification fixtures, not live server configuration or recovery.
