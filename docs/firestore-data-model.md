# Firestore para comisiones — preparación de Fase 1

## Alcance y garantías

Esta fase prepara un modelo Firestore paralelo. No migra datos, no conecta el WPF a
Firestore y no sustituye la persistencia JSON. `ECS.CommissionsMailer.csproj` no
referencia el proyecto Firestore; por tanto, iniciar, trabajar sin Internet, guardar,
calcular, generar Excel y enviar por Outlook conserva el flujo actual.

No se añadió ninguna estructura de Vencimientos.

La separación física queda así:

- `Infrastructure/Firestore`: SDK, DTOs, conversores, repositorios y bootstrap técnico;
  no referencia al WPF.
- `Infrastructure/Firestore.Mapping`: adaptador puro entre los modelos actuales y
  los DTOs; tampoco es referenciado por el WPF.
- `tools/ECSCommissionsMailer.FirestoreBootstrap`: consola independiente que solo
  referencia la infraestructura Firestore base.
- `ECS.CommissionsMailer.csproj`: continúa sin referencias de proyecto y sin paquete
  Firestore.

## Estado persistente encontrado

La aplicación no tiene base de datos, API ni contenedor de inyección de dependencias.
`AppDataPaths` crea `%LOCALAPPDATA%\ECSCommissionsMailer` y la persistencia JSON pasa
por `AtomicJsonFile`, `ConfigurationService`, `SessionService` y
`GenerationHistoryService`.

| Origen actual | Contenido | Ubicación/responsabilidad |
|---|---|---|
| `configuracion.json` | Configuración, directorio de corredores, asistentes, pestañas y rebajos | LocalAppData; lectura/escritura productiva actual |
| `sesion-actual.json` | Asunto, mensaje, selección, estado de filas y rutas de trabajo | LocalAppData; lectura/escritura productiva actual |
| `envios-recientes.json` | Historial de envíos y rutas de archivos archivados | LocalAppData; lectura/escritura productiva actual |
| `generaciones-detalles-pago.json` | Encabezados, archivos, resultados CRC/USD, warnings y rutas | LocalAppData; lectura/escritura productiva actual |
| `Data/correos-iniciales.v3.json` | Semilla versionada de configuración/directorio | Empaquetada; se conserva intacta como fallback |
| `Logs/app.log` | Diagnóstico local | LOCAL_ONLY |
| `Assets/Firma/*` | Imagen física de firma | LOCAL_ONLY |
| `ArchivosEnviados/*` | Copias físicas de adjuntos enviados | LOCAL_ONLY |
| `Backups/*` | Respaldos de migraciones locales | LOCAL_ONLY |
| `TempEdits/*`, `TempView/*` | Copias temporales | LOCAL_ONLY |
| Escritorio/carpetas elegidas | Excel fuente y generados | LOCAL_ONLY |

Los modelos de análisis de workbook, solicitudes/resultados de Outlook, diagnósticos
y estados de edición son transitorios salvo cuando sus resultados se copian dentro
de los cuatro modelos JSON anteriores.

## Estructura Firestore preparada

```text
system
  schema
  migrationState
settings
  commissions
brokers
  {brokerId UUID existente}
sessions
  current
    brokerItems
      {brokerId UUID existente}
recentSends
  {sendId UUID existente}
paymentGenerations
  {generationId UUID existente}
    files
      {generationFileId UUID asignado en la futura migración}
```

La semilla no se duplica en Firestore. En Fase 2, `brokers/` es la copia del estado
productivo actual. No se crean documentos operativos vacíos ni `_placeholder`. La
implementación y canonicalización de la migración están en
[`firestore-migration.md`](firestore-migration.md).

## Clasificación campo por campo

`SHARED` significa que el DTO Firestore tiene representación. `LOCAL_ONLY` significa
que el mapper lo devuelve en un objeto de estado local sin atributos Firestore.

| Campo actual | Origen JSON | Destino futuro Firestore | Clase | Razón |
|---|---|---|---|---|
| `AppConfiguration.DefaultSubject` | `configuracion.json` | `settings/commissions.defaultSubject` | SHARED | Texto funcional común |
| `AppConfiguration.DefaultMessage` | `configuracion.json` | `settings/commissions.defaultMessage` | SHARED | Texto funcional común |
| `AppConfiguration.SignatureImagePath` | `configuracion.json` | Estado local | LOCAL_ONLY | Ruta e imagen propias de una PC |
| `AppConfiguration.DataSchemaVersion` | `configuracion.json` | `settings/commissions.dataSchemaVersion` | SHARED | Versión funcional común |
| `AppConfiguration.EmailDirectorySeedVersion` | `configuracion.json` | `settings/commissions.emailDirectorySeedVersion` | SHARED | Control del directorio común |
| `AppConfiguration.EmailDirectorySeedId` | `configuracion.json` | `settings/commissions.emailDirectorySeedId` | SHARED | Identidad del directorio común |
| `AppConfiguration.CommonCcAddresses` | `configuracion.json` | `settings/commissions.commonCcAddresses` | SHARED | Destinatarios comunes |
| `AppConfiguration.Brokers` | `configuracion.json` | `brokers/{brokerId}` | SHARED | Datos maestros, separados del documento settings |
| `Broker.Id` | `configuracion.json` | ID de `brokers/{brokerId}` y campo `id` | SHARED | UUID estable existente; no se regenera |
| `Broker.SeedKey` | `configuracion.json` | `brokers/{id}.seedKey` | SHARED | Identidad/fallback del maestro actual |
| `Broker.Name` | `configuracion.json` | `brokers/{id}.name` | SHARED | Dato maestro |
| `Broker.PrimaryEmailAddresses` | `configuracion.json` | `brokers/{id}.primaryEmailAddresses` | SHARED | Directorio común |
| `Broker.Assistants` | `configuracion.json` | `brokers/{id}.assistants` | SHARED | Directorio común embebido |
| `Broker.AssociatedWorksheetNames` | `configuracion.json` | `brokers/{id}.associatedWorksheetNames` | SHARED | Asociación funcional común |
| `Broker.Deductions` | `configuracion.json` | `brokers/{id}.deductions` | SHARED | Reglas configuradas; no cambia su cálculo |
| `Broker.IsActive` | `configuracion.json` | `brokers/{id}.isActive` | SHARED | Estado maestro |
| `Broker.RequiresReview` | `configuracion.json` | `brokers/{id}.requiresReview` | SHARED | Estado funcional |
| `Broker.ReviewNote` | `configuracion.json` | `brokers/{id}.reviewNote` | SHARED | Advertencia funcional |
| `BrokerAssistant.Id` | `configuracion.json` | `brokers/{id}.assistants[].id` | SHARED | UUID existente |
| `BrokerAssistant.Name` | `configuracion.json` | `brokers/{id}.assistants[].name` | SHARED | Directorio común |
| `BrokerAssistant.Email` | `configuracion.json` | `brokers/{id}.assistants[].email` | SHARED | Directorio común |
| `BrokerAssistant.IsActive` | `configuracion.json` | `brokers/{id}.assistants[].isActive` | SHARED | Estado maestro |
| `BrokerDeduction.Id` | `configuracion.json` | `brokers/{id}.deductions[].id` | SHARED | UUID existente |
| `BrokerDeduction.Description` | `configuracion.json` | `brokers/{id}.deductions[].description` | SHARED | Configuración funcional |
| `BrokerDeduction.Amount` | `configuracion.json` | `brokers/{id}.deductions[].amount` | SHARED | Monto exacto; cadena decimal canónica |
| `BrokerDeduction.Currency` | `configuracion.json` | `brokers/{id}.deductions[].currency` | SHARED | Semántica financiera |
| `BrokerDeduction.ApplicationType` | `configuracion.json` | `brokers/{id}.deductions[].applicationType` | SHARED | Regla funcional |
| `BrokerDeduction.TargetWorksheetName` | `configuracion.json` | `brokers/{id}.deductions[].targetWorksheetName` | SHARED | Asociación funcional |
| `BrokerDeduction.DisplayOrder` | `configuracion.json` | `brokers/{id}.deductions[].displayOrder` | SHARED | Presentación común |
| `CurrentSession.Subject` | `sesion-actual.json` | `sessions/current.subject` | SHARED | Estado funcional común |
| `CurrentSession.Message` | `sesion-actual.json` | `sessions/current.message` | SHARED | Estado funcional común |
| `CurrentSession.CommonCcText` | `sesion-actual.json` | `sessions/current.commonCcText` | SHARED | Estado funcional común |
| `CurrentSession.GeneralWorkbookPath` | `sesion-actual.json` | Estado local | LOCAL_ONLY | Ruta absoluta a Excel fuente |
| `CurrentSession.ActivePaymentGenerationId` | `sesion-actual.json` | `sessions/current.activePaymentGenerationId` | SHARED | Enlace lógico compartido |
| `CurrentSession.GeneratedOutputDirectory` | `sesion-actual.json` | Estado local | LOCAL_ONLY | Carpeta física de una PC |
| `CurrentSession.GeneratedPeriod` | `sesion-actual.json` | `sessions/current.generatedPeriod` | SHARED | Estado funcional común |
| `CurrentSession.SavedAt` | `sesion-actual.json` | `sessions/current.savedAtUtc` | SHARED | Timestamp UTC del estado |
| `CurrentSession.BrokerItems` | `sesion-actual.json` | `sessions/current/brokerItems/{brokerId}` | SHARED + LOCAL_ONLY | Se separa por corredor y por rutas locales |
| `BrokerSendItem.BrokerId` | `sesion-actual.json` | ID y campo `brokerId` del broker item | SHARED | UUID existente |
| `BrokerSendItem.BrokerName` | `sesion-actual.json` | `brokerItems/{id}.brokerName` | SHARED | Snapshot funcional |
| `BrokerSendItem.SeedKey` | `sesion-actual.json` | `brokerItems/{id}.seedKey` | SHARED | Identidad funcional |
| `BrokerSendItem.PrimaryRecipients` | `sesion-actual.json` | `brokerItems/{id}.primaryRecipients` | SHARED | Selección de destinatarios |
| `BrokerSendItem.Assistants` | `sesion-actual.json` | `brokerItems/{id}.assistants` | SHARED | Snapshot de destinatarios |
| `BrokerSendItem.RequiresReview` | `sesion-actual.json` | `brokerItems/{id}.requiresReview` | SHARED | Estado funcional |
| `BrokerSendItem.ReviewNote` | `sesion-actual.json` | `brokerItems/{id}.reviewNote` | SHARED | Warning persistido |
| `BrokerSendItem.RequiresBatchReview` | `sesion-actual.json` | `brokerItems/{id}.requiresBatchReview` | SHARED | Estado funcional |
| `BrokerSendItem.BatchReviewNote` | `sesion-actual.json` | `brokerItems/{id}.batchReviewNote` | SHARED | Warning persistido |
| `BrokerSendItem.IsSelected` | `sesion-actual.json` | `brokerItems/{id}.isSelected` | SHARED | Selección común futura |
| `BrokerSendItem.AttachmentPaths` | `sesion-actual.json` | Estado local por broker | LOCAL_ONLY | Rutas a adjuntos de una PC |
| `BrokerSendItem.GeneratedAttachmentPaths` | `sesion-actual.json` | Estado local por broker | LOCAL_ONLY | Rutas a generados de una PC |
| `BrokerSendItem.Status` | `sesion-actual.json` | `brokerItems/{id}.status` | SHARED | Estado de proceso |
| `BrokerSendItem.LastError` | `sesion-actual.json` | `brokerItems/{id}.lastError` | SHARED | Error persistido |
| `SentEmailRecord.Id` | `envios-recientes.json` | ID y campo `id` de `recentSends/{sendId}` | SHARED | UUID estable existente |
| `SentEmailRecord.BrokerId` | `envios-recientes.json` | `recentSends/{id}.brokerId` | SHARED | Enlace lógico |
| `SentEmailRecord.BrokerName` | `envios-recientes.json` | `recentSends/{id}.brokerName` | SHARED | Snapshot histórico |
| `SentEmailRecord.BrokerPrimaryRecipients` | `envios-recientes.json` | `recentSends/{id}.brokerPrimaryRecipients` | SHARED | Evidencia histórica |
| `SentEmailRecord.AssistantRecipients` | `envios-recientes.json` | `recentSends/{id}.assistantRecipients` | SHARED | Evidencia histórica |
| `SentEmailRecord.ToRecipients` | `envios-recientes.json` | `recentSends/{id}.toRecipients` | SHARED | Evidencia histórica |
| `SentEmailRecord.CcRecipients` | `envios-recientes.json` | `recentSends/{id}.ccRecipients` | SHARED | Evidencia histórica |
| `SentEmailRecord.Subject` | `envios-recientes.json` | `recentSends/{id}.subject` | SHARED | Evidencia histórica |
| `SentEmailRecord.Body` | `envios-recientes.json` | `recentSends/{id}.body` | SHARED | Evidencia histórica |
| `SentEmailRecord.SentAt` | `envios-recientes.json` | `recentSends/{id}.sentAtUtc` | SHARED | Timestamp UTC histórico |
| `SentEmailRecord.ArchivedAttachmentPaths` | `envios-recientes.json` | Estado local | LOCAL_ONLY | Copias físicas en AppData local |
| `SentEmailRecord.WasSuccessful` | `envios-recientes.json` | `recentSends/{id}.wasSuccessful` | SHARED | Resultado histórico |
| `SentEmailRecord.ErrorMessage` | `envios-recientes.json` | `recentSends/{id}.errorMessage` | SHARED | Error histórico |
| `SentEmailRecord.ResendOfRecordId` | `envios-recientes.json` | `recentSends/{id}.resendOfRecordId` | SHARED | Relación lógica |
| `SentEmailRecord.PaymentGenerationId` | `envios-recientes.json` | `recentSends/{id}.paymentGenerationId` | SHARED | Relación lógica |
| `PaymentGenerationBatch.Id` | `generaciones-detalles-pago.json` | ID y campo `id` de `paymentGenerations/{generationId}` | SHARED | UUID estable existente |
| `PaymentGenerationBatch.Period` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.period` | SHARED | Encabezado funcional |
| `PaymentGenerationBatch.CreatedAt` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.createdAtUtc` | SHARED | Timestamp UTC histórico |
| `PaymentGenerationBatch.SourceWorkbookPath` | `generaciones-detalles-pago.json` | Estado local | LOCAL_ONLY | Ruta absoluta a Excel fuente |
| `PaymentGenerationBatch.SourceWorkbookSha256` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.sourceWorkbookSha256` | SHARED | Identidad de contenido |
| `PaymentGenerationBatch.OutputDirectory` | `generaciones-detalles-pago.json` | Estado local | LOCAL_ONLY | Carpeta física de una PC |
| `PaymentGenerationBatch.Status` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.status` | SHARED | Estado histórico |
| `PaymentGenerationBatch.Files` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}/files/{fileId}` | SHARED + LOCAL_ONLY | Evita un documento gigante y separa rutas |
| `PaymentGenerationBatch.SentBrokerIds` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.sentBrokerIds` | SHARED | Estado histórico |
| `PaymentGenerationBatch.FailedBrokerIds` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.failedBrokerIds` | SHARED | Estado/error histórico |
| `PaymentGenerationBatch.Warnings` | `generaciones-detalles-pago.json` | `paymentGenerations/{id}.warnings` | SHARED | Warnings históricos |
| `GeneratedPaymentFile` (ID de migración) | No existe en JSON actual | ID y campo `id` de `files/{fileId}` | SHARED | UUID determinístico derivado de SHA256 canónico en Fase 2; no modifica el JSON |
| `GeneratedPaymentFile.BrokerId` | `generaciones-detalles-pago.json` | `files/{id}.brokerId` | SHARED | Enlace lógico |
| `GeneratedPaymentFile.BrokerName` | `generaciones-detalles-pago.json` | `files/{id}.brokerName` | SHARED | Snapshot histórico |
| `GeneratedPaymentFile.WorksheetName` | `generaciones-detalles-pago.json` | `files/{id}.worksheetName` | SHARED | Resultado funcional |
| `GeneratedPaymentFile.OutputPath` | `generaciones-detalles-pago.json` | Estado local por archivo | LOCAL_ONLY | Ruta física de una PC |
| `GeneratedPaymentFile.Sha256` | `generaciones-detalles-pago.json` | `files/{id}.sha256` | SHARED | Identidad de contenido |
| `GeneratedPaymentFile.AnalyzerName` | `generaciones-detalles-pago.json` | `files/{id}.analyzerName` | SHARED | Trazabilidad del resultado |
| `GeneratedPaymentFile.GeneratedAt` | `generaciones-detalles-pago.json` | `files/{id}.generatedAtUtc` | SHARED | Timestamp UTC histórico |
| `GeneratedPaymentFile.Crc` | `generaciones-detalles-pago.json` | `files/{id}.crc` | SHARED | Resultado financiero calculado |
| `GeneratedPaymentFile.Usd` | `generaciones-detalles-pago.json` | `files/{id}.usd` | SHARED | Resultado financiero calculado |
| `PaymentCurrencyCalculation.Currency` | `generaciones-detalles-pago.json` | `crc/usd.currency` | SHARED | Semántica del resultado |
| `PaymentCurrencyCalculation.HasCommission` | `generaciones-detalles-pago.json` | `crc/usd.hasCommission` | SHARED | Resultado calculado |
| `PaymentCurrencyCalculation.MinimumApplied` | `generaciones-detalles-pago.json` | `crc/usd.minimumApplied` | SHARED | Resultado calculado |
| `PaymentCurrencyCalculation.MinimumAmount` | `generaciones-detalles-pago.json` | `crc/usd.minimumAmount` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.GrossCommissionOriginal` | `generaciones-detalles-pago.json` | `crc/usd.grossCommissionOriginal` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.GrossDeductions` | `generaciones-detalles-pago.json` | `crc/usd.grossDeductions` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.AdjustedGrossCommission` | `generaciones-detalles-pago.json` | `crc/usd.adjustedGrossCommission` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.Vat` | `generaciones-detalles-pago.json` | `crc/usd.vat` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.InvoiceAmount` | `generaciones-detalles-pago.json` | `crc/usd.invoiceAmount` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.Withholding` | `generaciones-detalles-pago.json` | `crc/usd.withholding` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.PayableBeforeFinalDeductions` | `generaciones-detalles-pago.json` | `crc/usd.payableBeforeFinalDeductions` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.FinalDeductions` | `generaciones-detalles-pago.json` | `crc/usd.finalDeductions` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.DepositedAmount` | `generaciones-detalles-pago.json` | `crc/usd.depositedAmount` | SHARED | Decimal exacto |
| `PaymentCurrencyCalculation.Observation` | `generaciones-detalles-pago.json` | `crc/usd.observation` | SHARED | Resultado descriptivo |
| `PaymentCurrencyCalculation.Deductions` | `generaciones-detalles-pago.json` | `crc/usd.deductions` | SHARED | Snapshot completo del cálculo |
| `PaymentCurrencyCalculation.Warnings` | `generaciones-detalles-pago.json` | `crc/usd.warnings` | SHARED | Warnings calculados |
| `PaymentCurrencyCalculation.Errors` | `generaciones-detalles-pago.json` | `crc/usd.errors` | SHARED | Errores calculados |
| `AppliedDeductionSnapshot.Id` | `generaciones-detalles-pago.json` | `deductions[].id` | SHARED | UUID existente |
| `AppliedDeductionSnapshot.Description` | `generaciones-detalles-pago.json` | `deductions[].description` | SHARED | Snapshot histórico |
| `AppliedDeductionSnapshot.ConfiguredAmount` | `generaciones-detalles-pago.json` | `deductions[].configuredAmount` | SHARED | Decimal exacto |
| `AppliedDeductionSnapshot.AppliedAmount` | `generaciones-detalles-pago.json` | `deductions[].appliedAmount` | SHARED | Decimal exacto |
| `AppliedDeductionSnapshot.Currency` | `generaciones-detalles-pago.json` | `deductions[].currency` | SHARED | Semántica financiera |
| `AppliedDeductionSnapshot.ApplicationType` | `generaciones-detalles-pago.json` | `deductions[].applicationType` | SHARED | Regla aplicada |
| `AppliedDeductionSnapshot.TargetWorksheetName` | `generaciones-detalles-pago.json` | `deductions[].targetWorksheetName` | SHARED | Asociación histórica |
| `AppliedDeductionSnapshot.DisplayOrder` | `generaciones-detalles-pago.json` | `deductions[].displayOrder` | SHARED | Orden histórico |

Las propiedades `[JsonIgnore]` calculadas (`IdentityText`, textos de UI, `IsValid`,
`IsSending`, etc.) no son estado persistente y no se duplican.

## Precisión y timestamps

Firestore soporta enteros de 64 bits y `double`, pero no `decimal`. Todos los montos
se escriben mediante `DecimalStringConverter` como cadena invariante y se reconstruyen
como `decimal`; esto evita usar `float`/`double` y conserva precisión y escala. Los
tests cubren `15000`, `3589.15`, `214336.00` y `615524.44` comparando también los bits
del `decimal` tras round-trip.

Los DTOs usan `DateTimeOffset` normalizado a UTC. Los JSON pueden representar los siete
dígitos de ticks de .NET (100 ns), pero Firestore almacena Timestamp con precisión de
microsegundos. `FirestoreTimestampPrecision` trunca —nunca redondea— sólo los ticks
sub-microsegundo en el mapping SHARED (`savedAtUtc`, `sentAtUtc`, `createdAtUtc` y
`generatedAtUtc`). Verify compara ambos lados con esa misma precisión exacta, sin una
tolerancia temporal. Una diferencia de un microsegundo sigue siendo distinta. Los
campos continúan siendo Timestamp nativos y la representación JSON actual no se
modifica; la menor resolución persistida no supone pérdida funcional.

## Repositorios y concurrencia futura

Se prepararon contratos async para brokers, settings, sesión/broker items, envíos y
generaciones/archivos. `CreateAsync` exige que el documento no exista. `UpdateAsync`
lee dentro de una transacción, compara `updateTime` con el valor esperado y falla con
`FirestoreConcurrencyException` si otro cliente modificó el documento. No se añadieron
listeners, sincronización automática ni métodos destructivos.

## Configuración y seguridad

`FirestoreOptions.Enabled` es `false` por defecto, `DatabaseId` es `(default)` y
`ProjectId` debe venir de `ECS_FIRESTORE_PROJECT_ID`, `GOOGLE_CLOUD_PROJECT` o un
argumento de la herramienta. `FirestoreConnectionFactory` usa exclusivamente
Application Default Credentials. No carga rutas de credenciales ni contiene secretos.

`firestore.rules` deniega toda lectura y escritura. Antes de activar clientes se deben
diseñar Firebase Authentication y reglas por usuario/rol.

## Bootstrap técnico

La herramienta separada está en `tools/ECSCommissionsMailer.FirestoreBootstrap`.
Sin `--apply` solo hace dry-run y no abre conexión. Con ADC configurado:

```powershell
$env:ECS_FIRESTORE_PROJECT_ID = "mi-project-id"
dotnet run --project tools/ECSCommissionsMailer.FirestoreBootstrap -- --apply
```

Solo intenta `CreateAsync` sobre `system/schema` y `system/migrationState`. Si existen,
los deja intactos. Nunca borra ni sobrescribe, y no crea brokers, sesiones, envíos,
generaciones ni placeholders.

## Pendiente para Fase 2

1. Definir autenticación del cliente y reglas de producción antes de habilitar acceso.
2. Diseñar el proceso de migración con backup, dry-run, validación y rollback.
3. Asignar IDs estables a los `GeneratedPaymentFile` que hoy no tienen ID.
4. Leer los cuatro JSON y escribir datos reales respetando las particiones documentadas.
5. Validar conteos, UUID, hashes, decimales, referencias y documentos mayores al límite.
6. Resolver conflictos iniciales y estrategia multi-PC de concurrencia/sincronización.
7. Incorporar estado local por dispositivo para rutas sin globalizarlas.
8. Conectar repositorios al runtime detrás de una selección explícita y reversible.
9. Mantener dual-read/rollback durante la transición y decidir cuándo retirar JSON.
10. Probar con emulador/entorno controlado, fallos de red y reglas autenticadas.

Nada de esta lista se ejecuta en la Fase 1.
