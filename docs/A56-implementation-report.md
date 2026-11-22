# A56 implementation report — to be completed after tests

Branch: `feature/a56-public-registration`
Baseline: updated `main` after A55 wiring fix, A54 and A55 merges.

## Business flow summary

| Operation | Preconditions | Atomic effects | Response |
|---|---|---|---|
| Register new | Flag on, valid fields | New HR Local user, TermsAcceptedAt, SHA-256 user token, encrypted outbox, audit | 202 generic |
| Register existing unverified | Local, active, not verified | Invalidate prior unused verification tokens, new token, encrypted outbox, audit, old password unchanged | 202 generic |
| Register existing verified/provider | Existing active account | Protected outbox informational email (max 3/h), audit; no password change | 202 generic |
| Resend verification | Local, active, unverified, email quota available | Rotate token + queue email | 202 generic |
| Verify email | Valid 24h token | Conditional consume, EmailVerifiedAt, Personal workspace, membership, active default prompt, audits; one transaction | 200 LoginResponse with JWT + smartcv_ws |
| Verify failure | Invalid/expired/consumed or provisioning fails | No account/workspace commit | 400 token_invalid/expired or 503 workspace_unavailable |
| Login | Correct password, unverified | No session | 403 email_not_verified |
| Login recovered | Correct password, verified non-Admin, no ws | Ensure personal workspace, then usual login | 200 isNewWorkspace |

## Mandatory review before merge

- [ ] A55 DI/Worker/API wiring fix is merged separately into `main`.
- [ ] A53/A54 tests PASS on updated main.
- [ ] `dotnet build SmartCv.slnx` PASS on A56.
- [ ] `dotnet test SmartCv.slnx --filter FullyQualifiedName~A56` PASS, no tests silently skipped.
- [ ] Full regression vs baseline PASS or preexisting failures documented.
- [ ] SQL integration: new account -> outbox -> .eml -> verify -> jwt smartcv_ws -> GET /api/workspace -> own job.
- [ ] SQL integration: register same email concurrently -> 1 user, test 2 verifications -> 1 success.
- [ ] SQL integration: deliberate provisioning exception -> rollback token consumption, EmailVerifiedAt and workspace.
- [ ] SQL integration: duplicate verified/unverified behavior, no password reset.
- [ ] 5/10min register, 10/10min verify; 3/h/email and 10/h/IP resend checks.
- [ ] Audit/log has no plaintext email and no tokens; raw token only in encrypted outbox and outgoing message.
- [ ] `Registration:Enabled` defaults false; Production startup guard on incomplete email.
- [ ] One-time token expiration and purpose isolation verified with SQL.
- [ ] Test confirmed legacy admin/HR login unaffected.
- [ ] No secret in Git: `git diff --cached`, grep User Secrets keys only.
- [ ] Report updated with actual test counts, request IDs, dates and known issues. Do not claim unrun tests PASS.

## Known design choices

1. Existing AuditEvents CHECK Outcome supports `Succeeded/NoChange/Denied/...`, not literal `created/existing/rate_limited`. We represent new=Succeeded, existing=NoChange, IP rejection=Denied/429 where rejection audit is configured, and preserve route action `auth.register`. Literal outcomes need a separately reviewed migration.
2. User consent version string is exposed by public settings but not saved on User row. Only TermsAcceptedAt persists under A51. Consider a separate append-only consent table before compliance use.
3. Per-IP limiter is in-memory per API process. Multi-instance deployment requires additional distributed/edge throttling.
4. SMTP crash-after-send duplicates remain possible; delivery is at-least-once.
5. A56 does not add any EF migration: UserTokens, Users.EmailVerifiedAt, SignupSource, TermsAcceptedAt, Workspaces, EmailOutbox already exist. New migration is only needed if schema is deliberately expanded after design review.

## Test execution (fill with actual outputs)

- Build: NOT RUN HERE
- Unit test A56: NOT RUN HERE
- Full regression: NOT RUN HERE
- SQL integration: NOT RUN HERE
- Local dev email .eml: NOT RUN HERE
- Production verification: NOT RUN HERE
