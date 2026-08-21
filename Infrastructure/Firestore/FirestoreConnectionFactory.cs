using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.Firestore;

/// <summary>
/// Construye conexiones mediante Application Default Credentials. No admite ni carga
/// credenciales empacadas dentro de la aplicación.
/// </summary>
public sealed class FirestoreConnectionFactory
{
    public FirestoreDb Create(FirestoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateForConnection();

        return new FirestoreDbBuilder
        {
            ProjectId = options.ProjectId,
            DatabaseId = options.DatabaseId
        }.Build();
    }
}
