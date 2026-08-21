# Migración JSON → Firestore (Fase 2)

## Alcance

`tools/ECSCommissionsMailer.FirestoreMigration` es una consola independiente. Lee
los cuatro JSON productivos desde el directorio indicado explícitamente y reutiliza
los DTOs/conversores de Fase 1. No está referenciada por el WPF, no forma parte de su
startup y no usa `Data/correos-iniciales.v3.json` como fuente.

Los originales nunca se mueven, editan ni eliminan. `FirestoreOptions.Enabled`
continúa siendo `false` por defecto; la consola construye una opción habilitada solo
después de recibir `--apply`, `--verify` o `--finalize-state`. La autenticación es
exclusivamente ADC.

## Comandos

```powershell
dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- `
  --dry-run `
  --source-directory "$env:LOCALAPPDATA\ECSCommissionsMailer"

dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- `
  --apply `
  --source-directory "$env:LOCALAPPDATA\ECSCommissionsMailer" `
  --project-id "PROJECT_ID" `
  --database-id "(default)"

dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- `
  --verify `
  --source-directory "$env:LOCALAPPDATA\ECSCommissionsMailer" `
  --project-id "PROJECT_ID"

dotnet run --project tools/ECSCommissionsMailer.FirestoreMigration -- `
  --finalize-state `
  --source-directory "$env:LOCALAPPDATA\ECSCommissionsMailer" `
  --project-id "PROJECT_ID" `
  --database-id "(default)"
```

`--apply` siempre repite internamente el dry-run local antes de permitir un backup o
una conexión. El dry-run puro no abre Firestore y no escribe archivos. `ProjectId`
también puede proceder de `ECS_FIRESTORE_PROJECT_ID` o `GOOGLE_CLOUD_PROJECT`;
`DatabaseId` usa `(default)` salvo argumento o `ECS_FIRESTORE_DATABASE_ID`.

`--finalize-state` es una reparación acotada del estado técnico: relee y verifica todos
los documentos operativos, exige cero missing/extra/different y que todos sean
idénticos, y luego realiza un único merge sobre `system/migrationState`. No ejecuta
bootstrap, create ni update sobre documentos operativos. Si la verificación falla, no
modifica `migrationState`.

Si falta ADC, instalar/configurar Google Cloud CLI fuera del repositorio y ejecutar:

```powershell
gcloud auth application-default login
```

No se admite `service-account.json` ni ninguna ruta de credenciales empacada.

## Preflight, backup y orden de escritura

Antes de escribir, la herramienta:

1. lee y deserializa los cuatro archivos requeridos;
2. valida UUIDs, referencias, snapshots financieros y round-trip decimal;
3. copia físicamente los archivos a
   `<source-directory>/migration-backups/<migrationId>/`;
4. confirma SHA256 del original y de cada copia;
5. crea `manifest.json` con rutas, tamaños, hashes, timestamp UTC, destino y
   versiones;
6. ejecuta y relee el bootstrap de `system/schema` y `system/migrationState`;
7. lee todas las colecciones operativas y clasifica cada documento como nuevo,
   idéntico o conflicto.

Un documento extra, diferente o con un campo LOCAL_ONLY es conflicto. Nunca se
ejecutan deletes ni reemplazos. El orden global es settings, brokers, encabezados de
generaciones, archivos de generaciones, sesión, brokerItems y recentSends. Cada alta
usa create; una carrera `AlreadyExists` se relee y solo se acepta si el contenido es
semánticamente idéntico.

`system/migrationState` pasa por `in-progress`/`false`, luego `completed`/`true` solo
después de verificación completa e idempotencia. Un error se registra como
`failed`/`false`; los documentos ya creados se conservan para reanudar con seguridad.
Una finalización posterior exitosa asigna en el mismo commit `completedAtUtc` y
`lastMigrationAtUtc`, y establece `errorSummary`/`failedAtUtc` en null para que un
fallo anterior no permanezca como estado activo.

## IDs determinísticos

Los UUID que ya existen en los JSON se conservan literalmente y se formatean con
`D` minúsculo como DocumentId.

Un `RecentSend` sin `Id` usa el formato canónico `ecs-recent-send-v1`. Participan,
en este orden, todos sus campos SHARED salvo `Id`:

- `BrokerId`, `BrokerName`;
- `BrokerPrimaryRecipients`, `AssistantRecipients`, `ToRecipients`, `CcRecipients`;
- `Subject`, `Body`, `SentAt` normalizado a UTC y microsegundos con formato `O`;
- `WasSuccessful`, `ErrorMessage`, `ResendOfRecordId`, `PaymentGenerationId`.

Un generation file usa `ecs-generation-file-v1` y, en orden:
`generationId`, `brokerId`, `worksheetName`, `sha256`.

La canonicalización conserva strings y orden de arrays sin trim, cambios de
mayúsculas ni normalización Unicode. Cada campo se representa como
`nombre=<longitud UTF-8>:<valor>\n`; null usa longitud `-1` y los arrays incluyen su
cantidad y cada índice. Se calcula SHA256 UTF-8 y los primeros 128 bits se expresan
como UUID `D`, compatible con los DTOs preparados en Fase 1. La misma entrada
produce exactamente el mismo ID. Ninguna ruta LOCAL_ONLY participa en los hashes.

## Precisión, protección local y verificación

Todos los montos pasan por `DecimalStringConverter`: nunca se convierten a
`double`/`float`. La comparación acepta escalas visuales equivalentes (`214336` y
`214336.00`) cuando representan el mismo `decimal`.

Los `DateTime`/`DateTimeOffset` de los JSON pueden conservar ticks de 100 ns, mientras
que Firestore persiste campos Timestamp solamente a precisión de microsegundos y
trunca hacia abajo cualquier precisión adicional. El boundary SHARED dominio → DTO
usa `FirestoreTimestampPrecision`: primero convierte el instante a UTC y luego elimina
exclusivamente los ticks sub-microsegundo (`ticks - ticks % 10`). Esto aplica a
`savedAtUtc`, `sentAtUtc`, `createdAtUtc` y `generatedAtUtc`.

La proyección esperada y el comparador usan la misma forma canónica. La igualdad sigue
siendo exacta después de truncar ambos valores; no se ignoran timestamps ni se aplica
una tolerancia arbitraria. Por tanto, dos valores que difieren solamente en los ticks
que Firestore no puede almacenar son equivalentes, pero una diferencia real de un
microsegundo continúa siendo conflicto. Los campos permanecen como Timestamp nativos;
no se convierten a string ni se duplican. Esta reducción documentada de resolución no
representa una pérdida funcional para los datos migrados.

Referencia: [tipos admitidos por Firestore](https://firebase.google.com/docs/firestore/manage-data/data-types)
y [Timestamp de Google.Cloud.Firestore](https://docs.cloud.google.com/dotnet/docs/reference/Google.Cloud.Firestore/latest/Google.Cloud.Firestore.Timestamp).

Antes de cada create se proyecta únicamente `[FirestoreProperty]` y se aplica un
guard recursivo contra:

`SignatureImagePath`, `GeneralWorkbookPath`, `GeneratedOutputDirectory`,
`AttachmentPaths`, `GeneratedAttachmentPaths`, `ArchivedAttachmentPaths`,
`SourceWorkbookPath`, `OutputDirectory`, `OutputPath`.

La verificación relee diccionarios Firestore completos, no solo DTOs ni conteos. Así
detecta campos missing, extra, diferentes y LOCAL_ONLY. Compara recursivamente
strings, null, bool, arrays en orden, objetos, UUIDs, enums, decimales, timestamps y
snapshots CRC/USD. No vuelve a abrir workbooks ni archivos generados para recalcular
sus hashes o resultados.

Después de una escritura exitosa se repite el mismo preflight en memoria como
simulación de apply: debe producir cero nuevos, cero conflictos y todos idénticos.
El directorio del backup recibe `migration-report.json` y `migration-report.txt` con
fuentes, hashes, conteos, creados, omitidos, conflictos y mismatches.

## Lo que permanece para Fase 3

La Fase 2 no conecta el WPF. Fase 3 deberá definir autenticación/reglas de producción,
activar explícitamente Firestore, sustituir o coordinar repositorios JSON, resolver
concurrencia/offline y decidir la estrategia de sincronización. No se implementaron
listeners, Vencimientos ni cambios en comisiones, rebajos, Excel u Outlook.
