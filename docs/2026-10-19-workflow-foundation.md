# Workflow Recorder foundation changes

Phase 4.2 keeps native capture, profiles, map authority and pure Perception interpretation in Atlas. Continuous input, recording sessions, historical RPC messages, workflow analysis and review remain in private MapLock projects.

The selective port uses Recorder `5a5bec6c19cf0a331dc4f37ff0101fcb51199543`, Perception `442887aa7efb353353e79183facd34844f353003` and Protocol `e21ff6827bec690fa479d39b41135386c16de360`. The provenance ledger retains each original source identity and records the current adapted bytes; these are adaptations, not a wholesale replacement of the newer Atlas foundation.

The public changes add cancellation-aware pure interpretation entry points, validation of retained native evidence, explicit unavailable geometry, neutral stream/provenance fields, canonical immutable map hashing and bounded provider-process containment. Private adapters invoke those entry points in isolated historical contexts. No public project references Workflow.Contracts.

Existing `SurfaceSceneReconstructor` and `OfficeMsaaGeometry` behavior from Atlas checkpoint `7b6a0493b6a355f510a8d497425309d269023cd9` is retained. The previous provenance hashes predated that checkpoint. Review confirms observation-local ownership, popup reconstruction and native-anchor coordinate reconciliation; historical normalization still uses the same canonical scene reconstruction. Raw source packets are not corrected in place.

Qualification caught a provider-port regression that omitted MSAA/raw response retention and the unlimited native timeout in Deep Survey. The adapter now preserves those Atlas behaviors while retaining process-first admission, complete waiter cleanup, cooperative bounded deadlines and caller cancellation. The three existing `DeepSurveyCaptureTests` pass on the combined source; failed intermediate binaries are not the release candidate.

`AtlasExactRevisionTests` builds, reviews and publishes through Atlas and checks canonical hashes, exact bytes, Ready eligibility, archival and subsequent publication. Recorder containment/policy tests and private historical Perception tests exercise the shared changes. Full integrated release qualification is recorded separately in the private Phase 4.2 report; this note alone is not release qualification.

The existing Excel documentation images were visually reviewed against the expanded-recording report. They show blank Book1 UI and Microsoft dialogs/galleries. The ledger records them as third-party UI evidence, not newly licensed product assets. Local `output/` concept artifacts and `tmp/` working files are outside the repository's product/package boundary.
