# ADR 0006: Agent proposals use device commands and receipts

18 November 2026. Item 4 of the version 2 run plan.

Only the installed personal-plan and career-plan workflows may propose device actions. Their code-owned specialist descriptors reference installed device capabilities; a domain definition does not grant these permissions. A planner sees only explicitly owner-granted run capabilities combined with existing audited device grants and their argument constraints. A model output can request up to three proposals. It cannot install a capability or grant itself one.

Each proposal becomes a durable child task with its own claim, bounded by the run deadline, outside the ordinary claim queue. AwaitingApproval and AwaitingReceipt are appended enum values. The run cannot succeed while either remains. Approval hashes include the invocation parameters and device binding. Execution enters AgentToolGateway, then the existing DeviceCommandService. The latter rechecks both grants and the gateway permit before confirming the device proposal. Both grants must preapprove a reversible capability for automatic dispatch; otherwise exact owner approval is required. Device dispatch and the Home Assistant adapter recheck current run/source/grant authority. A successful proposal is not reported as an executed action; only a signed, non-late success receipt completes its task. Cancellation prevents later dispatch and publication, while receipts remain factual records of any action already performed.

The dashboard can hold a new run for setup, add or revoke scoped grants with the separate owner credential, release the configured run, review exact proposed arguments and approve them. Setup expires after one day; released work retains the existing five-minute deadline. Device allow-lists remain configured through the existing Tools flow. No device is enrolled or granted permissions by this implementation run.

core_runs is unavailable while locked. A numbered spoken approval resolves against a short-lived Core-generated list, not a model-selected ID. Core requires a separate current captured user utterance, current audio and an unlocked, non-retired session; restricted speech is refused. Only reversible capabilities accept voice approval. Sensitive/irreversible approvals remain in the dashboard. Long argument sets require dashboard review. Voice cancellation uses the existing run-cancellation path.

Manual retry creates a fresh run idempotently with the original cloud consent and current workflow definition. It never replays external-action runs or unreconciled spending. Retry-After delta/date headers are bounded to 1–86,400 seconds, recorded as cooldown and returned to clients; they do not trigger automatic paid replay or release an uncertain reservation. Existing rates and routing remain unchanged.

Proposal arguments use the existing runtime persistence and privacy lifecycle. Export includes them; run deletion, retention and erasure remove them while preserving financial records. Voice review lists expire after two minutes and are pruned on use/session retention. No additional database engine or package was added.

Hardware behavior, live Home Assistant acceptance and installation are outside this local fake-provider verification. Source review is required before release integration.
