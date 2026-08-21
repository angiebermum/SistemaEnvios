using Google.Cloud.Firestore;
using Grpc.Core;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Bootstrap;

public sealed record FirestoreBootstrapResult(bool SchemaCreated, bool MigrationStateCreated);

/// <summary>
/// Crea únicamente los dos documentos técnicos de fase 1. CreateAsync evita
/// sobrescrituras y AlreadyExists se trata como un resultado idempotente.
/// </summary>
public sealed class FirestoreSchemaBootstrapper(FirestoreDb database)
{
    private readonly FirestoreDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<FirestoreBootstrapResult> PrepareAsync(CancellationToken cancellationToken = default)
    {
        var system = _database.Collection(FirestorePaths.SystemCollection);
        var schemaCreated = await CreateIfMissingAsync(
            system.Document(FirestorePaths.SchemaDocument),
            new Dictionary<string, object>
            {
                ["schemaVersion"] = 1L,
                ["application"] = "ECSCommissionsMailer",
                ["module"] = "commissions",
                ["state"] = "prepared",
                ["dataMigrated"] = false,
                ["createdAtUtc"] = FieldValue.ServerTimestamp
            },
            cancellationToken).ConfigureAwait(false);

        var migrationStateCreated = await CreateIfMissingAsync(
            system.Document(FirestorePaths.MigrationStateDocument),
            new Dictionary<string, object>
            {
                ["source"] = "local-json",
                ["status"] = "not-started",
                ["dataMigrated"] = false,
                ["lastMigrationAtUtc"] = null!
            },
            cancellationToken).ConfigureAwait(false);

        return new FirestoreBootstrapResult(schemaCreated, migrationStateCreated);
    }

    private static async Task<bool> CreateIfMissingAsync(
        DocumentReference document,
        object value,
        CancellationToken cancellationToken)
    {
        try
        {
            await document.CreateAsync(value, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            return false;
        }
    }
}
