using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;

namespace ECS.CommissionsMailer.Services;

internal enum CutoverPreflightExecution
{
    None,
    Manual,
    RequiredLegacyTransition
}

internal static class CutoverPreflightStartupPolicy
{
    public const string ManualCommandLineArgument = "--firestore-cutover-preflight";

    public static bool IsManualCommandRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(
            argument,
            ManualCommandLineArgument,
            StringComparison.OrdinalIgnoreCase));

    public static CutoverPreflightExecution Determine(
        bool manualCommandRequested,
        FirebaseClientOptions options,
        FirestoreRuntimeState? runtimeState = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (manualCommandRequested)
        {
            return CutoverPreflightExecution.Manual;
        }

        if (options.RuntimeDataMode == RuntimeDataMode.FirestorePrimary &&
            options.RequireLegacyCutoverPreflight &&
            runtimeState?.HasCloudWrites != true)
        {
            return CutoverPreflightExecution.RequiredLegacyTransition;
        }

        return CutoverPreflightExecution.None;
    }

    public static void DemandLegacyPreflightPassed(
        CutoverPreflightExecution execution,
        bool canCutOver,
        string reportPath)
    {
        if (execution != CutoverPreflightExecution.RequiredLegacyTransition || canCutOver)
        {
            return;
        }

        throw new InvalidOperationException(
            "FirestorePrimary fue bloqueado porque el Cutover Preflight encontró diferencias. " +
            $"Revise el reporte: {reportPath}");
    }
}
