/* A35: read-only inspection. Does not change stages, history or trigger state.
   Apply ApplicationStages separately after reviewing the generated migration SQL. */
SELECT ApplicationId, JobId, Stage, RejectReason, StageUpdatedAt, StageVersion
FROM dbo.Applications ORDER BY ApplicationId;

SELECT TOP (100) EventId, ApplicationId, FromStage, ToStage, RejectReason, Note, Actor, CreatedAt
FROM dbo.ApplicationEvents ORDER BY CreatedAt DESC, EventId DESC;

-- Every current decision must agree with the most recent committed event, including the reason.
SELECT a.ApplicationId, a.Stage, a.RejectReason, last.ToStage, last.RejectReason AS LastRejectReason,
       CASE WHEN last.EventId IS NULL AND a.Stage = N'New' AND a.RejectReason IS NULL
                      AND a.StageUpdatedAt IS NULL THEN N'New - no decision'
            WHEN last.ToStage = a.Stage
                 AND (last.RejectReason = a.RejectReason OR (last.RejectReason IS NULL AND a.RejectReason IS NULL))
                 AND last.CreatedAt = a.StageUpdatedAt THEN N'Consistent'
            ELSE N'MISMATCH' END AS HistoryCheck
FROM dbo.Applications a
OUTER APPLY (SELECT TOP (1) EventId, ToStage, RejectReason, CreatedAt
             FROM dbo.ApplicationEvents e WHERE e.ApplicationId = a.ApplicationId
             ORDER BY e.EventId DESC) last
ORDER BY a.ApplicationId;

SELECT Stage, COUNT(*) AS ApplicationCount FROM dbo.Applications GROUP BY Stage;
SELECT name, is_disabled, is_not_trusted, definition FROM sys.check_constraints
WHERE parent_object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents'));
SELECT name, is_disabled, is_instead_of_trigger FROM sys.triggers
WHERE object_id = OBJECT_ID(N'dbo.TR_ApplicationEvents_NoChange');
SELECT name FROM sys.indexes
WHERE object_id IN (OBJECT_ID(N'dbo.Applications'), OBJECT_ID(N'dbo.ApplicationEvents'));

-- Destructive UPDATE/DELETE rejection and rollback checks belong exclusively to the
-- disposable SQL integration tests, never to this inspection script.
