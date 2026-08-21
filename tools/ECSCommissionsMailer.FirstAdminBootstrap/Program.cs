using ECS.CommissionsMailer.Infrastructure.Firestore;
using Google.Cloud.Firestore;

return await FirstAdminBootstrapProgram.RunAsync(args);

internal static class FirstAdminBootstrapProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var values = Parse(args);
            if (values.Help)
            {
                PrintUsage();
                return 0;
            }

            Validate(values);
            Console.WriteLine(values.Apply ? "MODO: APPLY" : "MODO: DRY-RUN (sin escrituras)");
            Console.WriteLine($"Proyecto: {values.ProjectId}");
            Console.WriteLine($"Database: {values.DatabaseId}");
            Console.WriteLine($"Documento: appUsers/{values.Uid}");
            Console.WriteLine($"Email: {values.Email}");
            Console.WriteLine($"DisplayName: {values.DisplayName}");
            Console.WriteLine("Role: admin; isActive: true; canUseCommissions: true");

            var database = new FirestoreConnectionFactory().Create(new FirestoreOptions
            {
                Enabled = true,
                ProjectId = values.ProjectId,
                DatabaseId = values.DatabaseId
            });
            var document = database.Collection("appUsers").Document(values.Uid);
            var snapshot = await document.GetSnapshotAsync();
            if (snapshot.Exists && !values.OverwriteExisting)
            {
                Console.Error.WriteLine(
                    "BLOQUEADO: appUsers/{uid} ya existe. No se sobrescribió. " +
                    "Revise el perfil; use --overwrite-existing solo con confirmación explícita.");
                return 4;
            }

            if (!values.Apply)
            {
                Console.WriteLine(snapshot.Exists
                    ? "DRY-RUN: el perfil existe y sería reemplazado porque se indicó --overwrite-existing."
                    : "DRY-RUN: el perfil no existe y puede crearse con --apply.");
                return 0;
            }

            var now = Timestamp.GetCurrentTimestamp();
            var fields = new Dictionary<string, object>
            {
                ["uid"] = values.Uid,
                ["email"] = values.Email,
                ["displayName"] = values.DisplayName,
                ["role"] = "admin",
                ["isActive"] = true,
                ["canUseCommissions"] = true,
                ["createdAtUtc"] = snapshot.Exists && snapshot.ContainsField("createdAtUtc")
                    ? snapshot.GetValue<Timestamp>("createdAtUtc")
                    : now,
                ["updatedAtUtc"] = now
            };
            if (snapshot.Exists)
                await document.SetAsync(fields, SetOptions.Overwrite);
            else
                await document.CreateAsync(fields);
            Console.WriteLine("APPLY COMPLETADO: primer perfil admin registrado.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string Next()
            {
                if (++index >= args.Length) throw new ArgumentException($"Falta el valor para {arg}.");
                return args[index];
            }
            switch (arg)
            {
                case "--project-id": options.ProjectId = Next(); break;
                case "--database-id": options.DatabaseId = Next(); break;
                case "--uid": options.Uid = Next(); break;
                case "--email": options.Email = Next(); break;
                case "--display-name": options.DisplayName = Next(); break;
                case "--apply": options.Apply = true; break;
                case "--overwrite-existing": options.OverwriteExisting = true; break;
                case "--help" or "-h": options.Help = true; break;
                default: throw new ArgumentException($"Argumento no reconocido: {arg}");
            }
        }
        return options;
    }

    private static void Validate(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId)) throw new ArgumentException("--project-id es obligatorio.");
        if (string.IsNullOrWhiteSpace(options.DatabaseId)) throw new ArgumentException("--database-id es obligatorio.");
        if (string.IsNullOrWhiteSpace(options.Uid) || options.Uid.Contains('/')) throw new ArgumentException("--uid es obligatorio y no puede contener '/'.");
        if (string.IsNullOrWhiteSpace(options.Email) || !options.Email.Contains('@')) throw new ArgumentException("--email no es válido.");
        if (string.IsNullOrWhiteSpace(options.DisplayName)) throw new ArgumentException("--display-name es obligatorio.");
        if (options.OverwriteExisting && !options.Apply)
            Console.WriteLine("AVISO: --overwrite-existing en DRY-RUN solo muestra la operación; no escribe.");
    }

    private static void PrintUsage() => Console.WriteLine(
        "Uso: dotnet run --project tools/ECSCommissionsMailer.FirstAdminBootstrap -- " +
        "--project-id <id> --database-id <id> --uid <firebase-auth-uid> " +
        "--email <correo> --display-name <nombre> [--apply] [--overwrite-existing]");

    private sealed class Options
    {
        public string ProjectId { get; set; } = string.Empty;
        public string DatabaseId { get; set; } = FirestoreOptions.DefaultDatabaseId;
        public string Uid { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public bool Apply { get; set; }
        public bool OverwriteExisting { get; set; }
        public bool Help { get; set; }
    }
}
