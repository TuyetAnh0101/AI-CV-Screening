/* A41: READ ONLY. No INSERT, UPDATE, DELETE, DDL or cleanup.
   Run on an ISOLATED test database BEFORE and AFTER calling:
     POST /api/applications/{id}/ai-preview   (no body, { "systemPrompt": ... }, { "promptVersionId": ... })
     POST /api/prompts/assembled-preview
     GET  /api/applications/ai-preview/quota
   Every number must stay the same: trying a draft prompt never saves a prompt, run, evaluation, HR label or stage event,
   and never changes the active prompt.
   The first four columns are the check of the A41 guide; the other columns are extra A41 safety checks. */
SELECT
    (SELECT COUNT(*) FROM dbo.PromptVersions)                              AS PromptVersions,
    (SELECT COUNT(*) FROM dbo.EvaluationRuns)                              AS EvaluationRuns,
    (SELECT COUNT(*) FROM dbo.Evaluations)                                 AS Evaluations,
    (SELECT COUNT(*) FROM dbo.EvaluationSkills)                            AS EvaluationSkills,
    (SELECT COUNT(*) FROM dbo.HrLabels)                                    AS HrLabels,
    (SELECT COUNT(*) FROM dbo.ApplicationEvents)                           AS ApplicationEvents,
    (SELECT TOP (1) PromptVersionId FROM dbo.PromptVersions WHERE IsActive = 1
        ORDER BY PromptVersionId)                                          AS ActivePromptVersionId;
