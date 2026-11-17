# JARVIS — Personal AI Operating System
## Consolidated Requirements vNext
**Date:** 12 November 2026  
**Status:** Working requirements baseline  
**Purpose:** Consolidate the October 2026 JARVIS handover with the November 2026 interview, technical-training, communication and lifestyle-coaching requirements.

---

## 1. Product Vision

JARVIS is a continuously improving, voice-first Personal AI Operating System that works across the user's Windows laptop, Samsung phone, room voice hardware, future mini-PC, smart-home devices and future Meta Quest client.

The system should support:

- Natural hands-free conversation using a wake phrase such as **“Hey Jarvis”**.
- Persistent memory, session continuity and cross-device handoff.
- Senior software-engineering interview preparation.
- Technical drills covering C#/.NET, distributed systems, system design, databases, APIs, security, DevOps, payments and coding.
- Interview storytelling and project defence.
- Communication improvement, including slower and clearer speech.
- Social confidence, networking, small talk and workplace communication.
- Personal planning, routines, household/life skills and smart-home assistance.
- Proactive but non-intrusive coaching based on recurring weaknesses and upcoming goals.

JARVIS should be useful throughout the day, including when the user is away from the laptop and only has a phone and earphones.

---

## 2. Core Architecture

### 2.1 JARVIS Core

Use an **ASP.NET Core modular monolith** initially.

JARVIS Core owns:

- Authentication and authorisation.
- Conversation orchestration.
- Session ownership.
- Permissions and tool execution.
- Planning and goal management.
- Coaching state.
- Persistent memory.
- Device registry.
- Cross-device coordination.
- Audit history.
- Learning progress.
- Interview-performance history.

Do not split into microservices unless a concrete operational requirement justifies it.

### 2.2 Main modules

- `Jarvis.Api`
- `Jarvis.Orchestrator`
- `Jarvis.Voice`
- `Jarvis.Memory`
- `Jarvis.Coaching`
- `Jarvis.InterviewCoach`
- `Jarvis.CommunicationCoach`
- `Jarvis.TechnicalTrainer`
- `Jarvis.Automation`
- `Jarvis.Integrations`
- `Jarvis.Contracts`
- `Jarvis.Worker`
- `Jarvis.WindowsAgent`
- `Jarvis.Tests`

Future clients:

- Android companion.
- Dedicated room voice endpoint.
- Meta Quest Unity/OpenXR client.
- Home Assistant adapter.

### 2.3 Data

Use PostgreSQL as the source of truth for:

- Sessions.
- Conversation transcripts.
- Story Bank.
- Interview attempts.
- Technical-question history.
- Communication metrics.
- Preferences.
- Schedules.
- Learning progress.
- Weakness history.
- Goals.
- Audit logs.

Optional:

- pgvector for semantic retrieval.
- Redis only for ephemeral state where justified.
- Encrypted object storage for explicitly consented audio.

---

## 3. Voice-First Operation

JARVIS must support:

- Local wake-word activation where practical.
- Cloud real-time speech-to-speech.
- Natural interruption / barge-in.
- Turn detection.
- Graceful reconnect.
- Hands-free use.
- Earphone-first mobile practice.
- Cross-device session continuity.
- One active conversational audio owner at a time.
- Room voice sessions without needing the phone or laptop screen.

Target voice/persona:

- Warm.
- Articulate.
- Natural.
- Modern Southern British English influence.
- Conversational rather than theatrical.
- Coaching should improve clarity, rhythm and confidence **without erasing the user's Nigerian accent or identity**.

---

## 4. P0 — Communication & Lifestyle Speech Coach

This is a core requirement, not an interview-only feature.

### 4.1 Goal

Improve the user's everyday spoken communication so that slower, clearer, more structured speech becomes automatic in:

- Interviews.
- Recruiter calls.
- Technical meetings.
- Social conversations.
- Networking.
- Dating/social situations.
- Phone calls.
- Presentations.
- Everyday workplace communication.

### 4.2 Metrics to track

For voice sessions, JARVIS should measure where technically possible:

- Approximate words per minute.
- Filler frequency: “um”, “uh”, “like”, “kind of”, “basically”, repeated starts, etc.
- False starts and sentence restarts.
- Repetition.
- Long run-on answers.
- Pausing behaviour.
- Answer duration.
- Clarity of opening sentence.
- Structure.
- Confidence/hesitation indicators.
- Overuse of “we” where personal ownership should be “I”.
- Whether the user answers before structuring the response.

### 4.3 Coaching behaviour

JARVIS should train the user to:

- Pause for 1–2 seconds before answering.
- Start with the point, not background.
- Use shorter sentences.
- Slow down deliberately.
- Replace fillers with silence.
- Finish one idea before starting another.
- Stop once the question has been answered.
- Recover cleanly using phrases such as:
  - “Let me structure that clearly.”
  - “At a high level…”
  - “The key point is…”
  - “Bringing that back to the question…”

### 4.4 Context-sensitive pace

The target speaking pace must depend on context.

Examples:

- Recruiter call: concise and conversational.
- Technical explanation: slower, deliberate, structured.
- System design: explain high-level architecture before detail.
- Social conversation: natural, not over-rehearsed.
- Presentation: controlled pacing and intentional pauses.

### 4.5 Daily lifestyle drills

JARVIS should provide short hands-free drills throughout the day:

- 2-minute slow-speech warm-up.
- 5-question rapid communication drill.
- Story retelling in 30 seconds.
- Story retelling in 60 seconds.
- Explain one technical concept to a non-technical listener.
- Social small-talk simulation.
- Workplace update simulation.
- “Say it again, 20% slower” repetition.
- End-of-day communication review.

The purpose is to make improved speaking style a lifestyle habit rather than something switched on only during interviews.

---

## 5. P0 — Interview Storytelling & Performance Coach

### 5.1 Story Bank

Maintain a persistent Story Bank containing approximately 12–15 strong real stories.

Each story stores:

- Company/project.
- Exact service/feature.
- Context.
- Problem.
- Constraints.
- Personal ownership.
- Technical decisions.
- Trade-offs.
- Implementation.
- Collaboration.
- Failure modes.
- Result.
- Metrics.
- What the user would change today.
- Likely follow-up questions.
- Relevant CV claims.

Initial stories include:

- Alternative Bank — FX Transaction Module.
- Alternative Bank — Digital Banking Platform.
- Alternative Bank — fraud/security incident.
- Alternative Bank — device-ID incident.
- Zedcrest — transaction reconciliation.
- Access Bank — maker-checker workflow.
- Deployment/release incident.
- Technical disagreement.
- Mentoring.
- Technical debt trade-off.
- Performance/scaling improvement.
- Third-party integration failure.

### 5.2 Core spoken-answer framework

JARVIS must coach answers toward:

**Frame → Problem → Personal Ownership → Decision/Action → Outcome → STOP**

For architecture questions:

**Requirement → Constraints → Architecture → Why → Trade-off → Failure Mode → Recovery → Scale**

### 5.3 Generic-answer detector

JARVIS should flag answers that only describe process, for example:

> “I gathered requirements, coded it, tested it, worked with QA and deployed it.”

It should push for:

- What exact feature/service?
- What problem?
- What did you personally own?
- What was technically difficult?
- What decision did you make?
- What trade-off did you accept?
- What changed because of your work?

### 5.4 Interview modes

- Recruiter Screen.
- Hiring Manager.
- Engineering Manager.
- Technical CV Deep Dive.
- Senior Engineer.
- Staff/Principal challenge mode.
- Behavioural.
- System Design.
- Code Review.
- Production Incident.
- Payments/Fintech.
- Mock Phone Screen.

### 5.5 CV Attack Mode

JARVIS should select claims from the user's CV and challenge them until they can be defended naturally.

Examples:

- “You said two million transactions per day. What exactly did your service own?”
- “You said you designed distributed services. What design decision did you personally make?”
- “Why RabbitMQ?”
- “How was 99.9% availability measured?”
- “What would happen on a provider timeout?”
- “What was your contribution versus the team's?”

### 5.6 Post-interview review

After each real or mock interview, JARVIS should record:

- Questions asked.
- User answer summary.
- Strong answers.
- Generic answers.
- Missed ownership.
- Missing architecture depth.
- Pace/filler issues.
- Questions that caused hesitation.
- Follow-up drills.
- Updated weakness priorities.

---

## 6. P0 — Technical Interview Trainer

Technical preparation and storytelling must share the same learning history.

### 6.1 Core technical domains

#### C# / .NET
- Async/await.
- Task and concurrency.
- Dependency injection lifetimes.
- Collections.
- Memory/performance.
- ASP.NET Core.
- EF Core.
- Dapper.
- LINQ.
- Exception handling.
- Testing.
- SOLID and design patterns.
- Clean architecture and pragmatic design.

#### Distributed systems
- Messaging.
- RabbitMQ.
- Kafka concepts.
- At-least-once delivery.
- Idempotency.
- Outbox/inbox.
- Retries.
- Exponential backoff/jitter.
- Circuit breakers.
- Backpressure.
- Eventual consistency.
- Saga/compensation concepts.
- Caching.
- Distributed tracing.

#### Databases
- Transactions.
- Isolation.
- Locking.
- Optimistic concurrency.
- Indexes.
- Query plans.
- SQL performance.
- Connection pools.
- Data modelling.
- Reconciliation.

#### APIs and security
- REST.
- gRPC.
- API versioning.
- Authentication.
- Authorisation.
- OAuth2/JWT.
- OWASP.
- Rate limiting.
- Input validation.
- Secrets.
- Audit trails.
- Sensitive-data logging.

#### Production / DevOps
- CI/CD.
- Docker.
- Kubernetes concepts.
- Safe deployment.
- Rollback.
- Expand-contract migrations.
- Feature flags.
- Observability.
- Metrics/logs/traces.
- Incident response.
- SLO/SLA concepts.
- Capacity and bottlenecks.

#### Payments / fintech
- Duplicate transaction prevention.
- Idempotency.
- Unknown/pending transaction state.
- Provider timeouts.
- Reconciliation.
- Settlement.
- Maker-checker.
- Transaction state machines.
- Fraud/security.
- Multi-currency/FX.
- Payment rails and external integrations.

#### System design
- Requirements clarification.
- Scale estimation.
- Service boundaries.
- Data ownership.
- Sync vs async.
- Consistency.
- Failure handling.
- Scaling.
- Observability.
- Security.
- Trade-offs.

#### Coding / algorithms
- Data structures.
- Algorithms.
- Complexity.
- Verbal reasoning while mobile.
- Actual coding and execution during laptop sessions.

### 6.2 Drill modes

- Rapid Drill: 20–40 second answers.
- Deep Dive: repeated follow-up questions on one topic.
- Explain Simply: teach a concept to a junior/non-technical listener.
- Architecture Challenge.
- Failure Injection.
- Production Incident.
- Code Review reasoning.
- Random Mixed Interview.
- Spaced Repetition Review.

---

## 7. Adaptive Learning & Spaced Repetition

Every skill/question/story should maintain a score.

Suggested dimensions:

- Technical accuracy.
- Specificity.
- Personal ownership.
- Architecture reasoning.
- Trade-off reasoning.
- Communication.
- Pace.
- Confidence.
- Recall stability.

Weak items return sooner.

Example:

- Weak idempotency answer → repeat tomorrow.
- Weak FX storytelling → repeat tomorrow.
- Medium DI lifetime recall → repeat in 3 days.
- Strong SOLID answer → repeat in 7–14 days.

JARVIS should prioritise retrieval practice rather than passive listening.

---

## 8. Three-Month Intensive Improvement Programme

The system should support a 12-week programme.

### Weeks 1–2
- Answer structure.
- Speaking pace.
- Pausing.
- Fillers.
- 30–45 second answers.

### Weeks 3–4
- Build and harden Story Bank.
- Personal ownership.
- Specific project examples.

### Weeks 5–6
- Technical CV defence.
- Follow-up questioning.
- Deep technical explanations.

### Weeks 7–8
- Architecture storytelling.
- Distributed-systems trade-offs.
- System-design structure.

### Weeks 9–10
- Full mock interviews.
- Recruiter → manager → technical → behavioural → system design.

### Weeks 11–12
- Pressure testing.
- Interruptions.
- Ambiguous questions.
- Difficult interviewer mode.
- Fast recovery and concise answers.

Success target:

The user should be able to defend every major CV claim and explain real engineering work clearly without notes.

---

## 9. Mobile / Earphone Practice

JARVIS must support productive practice when the user is not at the laptop.

Examples:

### Morning commute
- 5 rapid technical questions.
- 1 project story.

### Break
- 10-minute CV Attack Mode.

### Journey home
- 20-minute Deep Dive.
- System-design verbal drill.

### Evening laptop session
- Coding.
- Diagrams.
- SQL.
- Detailed architecture.
- Code review.

A session should resume across devices.

Example:

> “Jarvis, continue my interview training.”

JARVIS should know the last weak topics and continue from them.

---

## 10. Proactive Coaching

JARVIS should proactively:

- Prepare the user for upcoming interviews.
- Surface weak stories before interviews.
- Schedule short voice drills.
- Produce daily priorities.
- Produce weekly progress summaries.
- Track whether practice actually occurred.
- Avoid “no wasted days” by offering a short mobile drill when a laptop session is missed.

Do not create constant intrusive notifications. Coaching should be useful and context-aware.

---

## 11. Interview Listening & Safety

A standalone room microphone may capture audible laptop interview audio and user speech only in an authorised/consented session.

Pipeline:

**Authorised capture → transcription → speaker segmentation where feasible → question extraction → storage → post-interview analysis → future drills**

Do not build covert live answer feeding.

Live AI-generated assistance during a real assessment should only be used when the assessment explicitly permits AI assistance.

---

## 12. Session Continuity

A logical session includes:

- Session ID.
- User ID.
- Activity type.
- Current task/question.
- Transcript checkpoint.
- Active audio device.
- Client presence.
- Learning progress.
- Weakness state.

Handoff flow:

**Request → checkpoint → target ready → transfer audio ownership → confirm → recover on failure**

---

## 13. Home / Lifestyle Integration

JARVIS should also support:

- Home Assistant.
- Reversible smart-bedroom devices.
- Calendar briefings.
- Timers.
- Cooking/baking guidance.
- Household routines.
- Practical planning.
- Activity-aware reminders.

Avoid unsafe unattended cooking/heating automation.

---

## 14. Privacy & Permissions

Required controls:

- Explicit listening-state indicator.
- Recording opt-in.
- Retention controls.
- Memory inspection/deletion.
- Camera permission.
- Tool permission.
- Sensitive-action confirmation.
- API-spend controls.
- Device-health monitoring.
- Secure secret storage.

No unnecessary continuous cloud streaming before activation unless explicitly enabled for an authorised session.

---

## 15. Near-Term Engineering Priority

### P0
1. Voice session loop.
2. Persistent memory/session store.
3. Communication & Lifestyle Speech Coach.
4. Interview Storytelling Coach.
5. Technical Interview Trainer.
6. Story Bank.
7. Weakness scoring.
8. Spaced repetition.
9. Mobile earphone practice.
10. Post-session transcript scoring.

### P1
- Home Assistant.
- Proactive daily/weekly coaching.
- Interview audio archive.
- Android companion improvements.
- Cross-device handoff.

### P2
- Meta Quest interview simulations.
- Rich spatial coaching.
- Dedicated far-field room voice hardware.
- Migration to dedicated mini-PC.

---

## 16. Primary Success Criterion

JARVIS should be able to take any major line from the user's CV or any senior-backend interview topic and keep questioning until the user can:

1. Explain it accurately.
2. Explain it simply.
3. Explain personal ownership.
4. Explain the trade-off.
5. Explain failure behaviour.
6. Explain the outcome.
7. Deliver the answer calmly and at a controlled pace.
8. Do so without sounding rehearsed, generic or rushed.

The long-term goal is not merely to pass interviews.

The goal is for clear, deliberate, confident communication to become the user's normal speaking style across engineering, work, social interaction and everyday life.
