# JARVIS

JARVIS is a personal assistant with a .NET Core API, a browser companion and an audio-only Android app.

| Directory | Purpose |
|---|---|
| [JARVIS.CORE](JARVIS.CORE) | API, orchestration, PostgreSQL persistence and the hosted browser bundle |
| [JARVIS.FRONTEND](JARVIS.FRONTEND) | Source for the browser modules packaged with Core |
| [JARVIS.MOBILE](JARVIS.MOBILE) | Android companion for voice, earbuds and secure pairing |

Start with the component READMEs. From the repository root, run the two packaging checks when changing browser modules or companion contracts.

~~~powershell
./JARVIS.CORE/scripts/sync-frontend.ps1 -Check
./JARVIS.CORE/scripts/check-mobile-contracts.ps1
~~~

GitHub Actions checks the affected components and uses disposable test databases, fake providers and a debug Android build. Keep local settings, credentials, signing material and backups outside Git.

Use `development` for ongoing work and `master` for stable releases. CI checks pull requests and pushes to `master`.
