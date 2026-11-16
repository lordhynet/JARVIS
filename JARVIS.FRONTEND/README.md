# JARVIS browser modules

`modules.js` is the canonical source for the browser's coaching, Story Bank, technical
practice, programme, workflows and module-status panels. The existing conversation,
device, learning and integration shell remains in Core's `wwwroot/app.js`.

Core hosts the complete application under the same origin and authentication policy.
The module receives the shell's in-memory authenticated API client; it does not copy
API keys into browser storage, maintain another session store or embed provider keys.
User/provider strings enter the DOM through textContent/value, never HTML interpolation.

After editing, run from JARVIS.CORE:

```powershell
.\scripts\sync-frontend.ps1
.\scripts\sync-frontend.ps1 -Check
npm run test:browser
```

`sync-frontend.ps1` packages the source into Core's static bundle. Keep both files in
the change so Docker builds do not depend on files outside Core's build context.
Browser tests exercise the real API with isolated in-memory test state and fixture keys.
