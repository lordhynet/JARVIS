# ADR 0005: Scoped agent tool admission

Date: 18 November 2026. Status: implemented for review, Item 3 of the version 2 run plan.

## Existing mechanisms and gap

Core already has a code-owned domain capability catalog, an AgentRuntime with claim leases, device capability schemas/grants/confirmations/receipts, and a durable UsageBudget. Existing agent execution produces deterministic artifacts or invokes the existing reasoning/search service. It does not yet execute model-proposed external actions.

Extend the catalog and those execution paths. Do not install another orchestration engine, provider router, budget ledger, or device-command executor. `ICapabilityRegistry` and the compatibility `ICapabilityCatalog` resolve to the same instance. The registry includes existing voice tools, Home Assistant/device contracts and the installed artifact handlers. Descriptors preserve existing keys/risk identifiers and add input schemas, data classes, approval/budget requirements and detailed device risk. Metadata never installs a handler or grants authority.

## Admission

Code-owned specialist descriptors cover every existing AgentRole and retain its existing Agent/Research provider profile. Only deterministic/template artifacts, ordinary specialist reasoning and grounded research have executable agent handlers in this slice. No role receives Home Assistant or voice-action permissions merely because those contracts are registered.

AgentToolGateway requires the current canonical task claim, live run, unchanged source references, allowed specialist capability/data class, closed argument schema and a current grant. Grants bind a run to its goal/trigger revision, or to that run when no persistent goal exists. Installed baseline grants cover only existing read-only artifact paths. Explicit owner grants cannot override a specialist descriptor or irreversible approval requirement.

An invocation has a durable identity and argument hash. Owner confirmation binds the exact invocation, parameters, grant, capability/version and data class. A changed hash, revoked grant, different claim, changed goal, cancellation or expired run refuses dispatch. Preparing/executing/uncertain invocations cannot replay automatically. Only read-only completed work may reuse the existing provider cache, after renewed authority checks.

Preparation cannot call a provider or execute an action. The existing reasoning service now exposes an internal preparation step shared by ordinary callers and agent handlers; both retain the same rate-card calculation and cache identity. The gateway reserves through UsageBudget before executing a paid handler, then rechecks authority and reservation identity. Unknown outcomes retain their reservation. Free artifacts have an explicit zero-cost path and cannot supply a paid charge. No rates or model selections changed.

Installed handlers require a gateway-created execution scope. The scope cannot be created outside Orchestrator and is invalidated on disposal, including copies captured by child execution contexts. Direct agent reasoning is refused. The existing legacy interview workflow retains a narrow host-owned admission bridge, bound to its existing live workflow claim and exact operation/profile; it does not receive an AgentTask grant.

## Bounds and lifecycle

Current specialist limits are four distinct calls per task, 400,000 UTF-8 argument bytes, 64,000 UTF-8 result bytes and at most USD 2 reserved per invocation. Canonical task input and final artifact envelopes remain bounded; final artifacts retain the existing 16,000-character limit. These code limits supplement the existing reviewed token limits and central GBP/USD budget guards.

Grant and invocation metadata use the existing atomic runtime store, bounded to 2,000 grants and 10,000 invocation records. They do not duplicate arguments, prompts, credentials or output; audit retains hashes and reservation references. Full erasure removes this authority while preserving the existing financial ledger. Run deletion and retention remove associated metadata; cancellation/supersession revoke grants. Existing JSON defaults make this additive, with no relational migration. These collections should be considered in the already-planned hot-runtime persistence work.

## Scope and validation

This is admission infrastructure and wiring for current agent artifacts. Owner-facing action proposals, grant controls, `core_runs`, manual retries and external execution belong to Item 4. Device handlers must continue using the existing policy/grant/proposal/approval/execution/receipt chain; this registry is not a bypass.

Tests cover descriptor/schema/grant refusal, exact irreversible approvals, budget-before-handler/provider ordering, uncertain outcomes, revocation, goal/claim changes, direct-handler bypass, real agent reasoning/search with fake providers, completed-cache reuse, legacy workflow compatibility, export/erasure and PostgreSQL restart/deletion. No paid provider call, live integration, private configuration change, push, installation or deployment is authorized by this implementation.
