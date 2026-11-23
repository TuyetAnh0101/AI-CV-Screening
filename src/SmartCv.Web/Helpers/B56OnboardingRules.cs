using SmartCv.Core.Workspaces;

namespace SmartCv.Web.Helpers;

/// <summary>Pure B56 onboarding rules; never treats failed API calls as completed steps.</summary>
public static class B56OnboardingRules
{
    public static bool Show(WorkspaceDto? ws, bool? hasRun, bool welcome, bool hidden) =>
        !hidden && ws is { Kind: WorkspaceKinds.Personal } &&
        (ws.JobCount == 0 || hasRun == false || welcome);

    public static int CompletedSteps(WorkspaceDto? ws, bool? hasRun) =>
        (ws?.JobCount > 0 ? 1 : 0) + (ws?.CvCount > 0 ? 1 : 0) + (hasRun == true ? 1 : 0);
}
