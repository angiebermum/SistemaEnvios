# Runtime Firestore de ECS Comisiones

## Alcance y arquitectura

La Fase 3 agrega persistencia compartida sin modificar la lógica que calcula comisiones, rebajos, IVA, retenciones, mínimos, facturas, CRC/USD, archivos Excel ni correos Outlook.

```text
WPF / code-behind
    -> IRuntimeDataService (async)
        -> JsonOnlyRuntimeDataService
        -> ShadowReadRuntimeDataService
        -> FirestorePrimaryRuntimeDataService
            -> repositorios tipados
                -> FirestoreRestClient (HttpClient)
                    -> Firebase ID token
                        -> Security Rules
```

El runtime referencia `Infrastructure/FirebaseClient`, que no contiene `Google.Cloud.Firestore`, ADC ni IAM. `Infrastructure/Firestore` conserva `Google.Cloud.Firestore` únicamente para bootstrap, migración y herramientas administrativas.

## Configuración externa

El archivo local opcional es:

`%LOCALAPPDATA%\ECSCommissionsMailer\firebase-runtime.json`

Ejemplo seguro, dejando el modo productivo actual sin cambios:

```json
{
  "ProjectId": "essential-4eccd",
  "DatabaseId": "(default)",
  "FirebaseApiKey": "REEMPLAZAR_LOCALMENTE",
  "RuntimeDataMode": "JsonOnly",
  "RememberSession": true
}
```

También se admiten `ECS_FIREBASE_PROJECT_ID`, `ECS_FIRESTORE_DATABASE_ID`, `ECS_FIREBASE_API_KEY` y `ECS_RUNTIME_DATA_MODE`. Las variables de entorno tienen precedencia. Si no existe archivo ni variable, el modo es `JsonOnly`.

`FirebaseApiKey` identifica el proyecto ante Firebase Authentication; no es una credencial IAM ni concede acceso a Firestore. No se incluye una clave real en el repositorio. Debe ser una Web API Key del proyecto y restringirse a `identitytoolkit.googleapis.com` (Identity Toolkit API) y `securetoken.googleapis.com` (Token Service API). La autorización de documentos depende del ID token, `appUsers` y Security Rules.

## Modos

### JsonOnly

- Es el valor predeterminado.
- Usa sin cambios `configuracion.json`, `sesion-actual.json`, `envios-recientes.json` y `generaciones-detalles-pago.json`.
- No crea cliente Firebase, no muestra login y no requiere internet, gcloud ni API key.

### FirestoreShadowRead

- Requiere login Firebase y un `appUsers/{uid}` autorizado.
- La UI continúa leyendo y escribiendo con los servicios JSON existentes.
- Firestore se lee en paralelo solo para comparar datos SHARED.
- No hay escrituras operativas, reparación ni dual-write hacia Firestore.
- El reporte se guarda en `%LOCALAPPDATA%\ECSCommissionsMailer\firestore-shadow-reports`.

Estados por documento: `MATCH`, `MISSING_IN_FIRESTORE`, `MISSING_LOCALLY`, `DIFFERENT`.

### FirestorePrimary

- Nunca se activa automáticamente.
- SHARED se lee y escribe solo en Firestore.
- LOCAL_ONLY se lee y escribe solo en `local-workspace.json`.
- Los cuatro JSON históricos se conservan, pero sus campos SHARED no se consultan como fuente autoritativa.
- Si falta ProjectId, DatabaseId o FirebaseApiKey, el inicio se bloquea; no existe fallback writable a JSON.
- Antes de la primera escritura exige un Cutover Preflight limpio. Después de la primera escritura, `firestore-runtime-state.json` registra que un regreso directo al JSON antiguo ya no es seguro.

## SHARED y LOCAL_ONLY

SHARED: settings funcionales, brokers, asistentes, correos, hojas, deducciones, revisión, sesión compartida, estados, destinatarios, recientes, encabezados y archivos lógicos de generaciones, hashes y snapshots financieros completos.

LOCAL_ONLY:

- `SignatureImagePath`
- `GeneralWorkbookPath`
- `GeneratedOutputDirectory`
- `AttachmentPaths`
- `GeneratedAttachmentPaths`
- `ArchivedAttachmentPaths`
- `SourceWorkbookPath`
- `OutputDirectory`
- `OutputPath`
- Excel, firma, temporales, logs, backups y archivados físicos

Los mapeadores REST no exponen esos campos. `local-workspace.json` asocia metadata compartida por ID con rutas del equipo actual. Una PC sin asociación local puede ver brokers, hashes y resultados financieros; al intentar abrir un Excel muestra “Este archivo no está disponible en este equipo”. No inventa ni descarga rutas.

## Precisión, concurrencia e idempotencia

- `FirestoreTimestampPrecision` es compartido por cliente desktop y herramientas administrativas; trunca a microsegundos.
- Los `decimal` financieros se serializan como strings canónicos invariantes mediante `FirestoreCanonicalDecimal`; nunca se convierten a `double` o `float`.
- Cada lectura conserva `updateTime`. Updates y deletes envían `currentDocument.updateTime`.
- Un 409/412 produce un conflicto visible y no sobrescribe el documento.
- Creaciones usan IDs existentes/determinísticos. No se reintentan writes automáticamente.
- Reads pueden reintentarse de forma acotada ante timeout, 429 y 5xx. Un 401 permite un refresh y un único retry. 403, conflicto y validación no se reintentan.

## Red, async y rendimiento

Las llamadas de red son async y aceptan `CancellationToken`. En `FirestorePrimary` no existe cola offline ni promesa de sincronización posterior: una escritura SHARED requiere conexión. Las cargas iniciales listan colecciones en páginas y solo cargan los archivos de la generación activa; no descargan inmediatamente los 1211 archivos históricos.

### Limitación conocida de `sessions/current`

Se conserva el modelo aprobado: un documento `sessions/current` y su subcolección `brokerItems`. Cada documento tiene precondición propia, por lo que dos PCs no pueden sobrescribir silenciosamente la misma versión. Sin embargo, un guardado que abarque `current` y varios brokerItems no constituye una única transacción REST; un conflicto tardío puede dejar aplicado un documento previo del mismo intento. La UI informa el conflicto y exige recargar. Una fase posterior puede evaluar una transacción o un rediseño de sesión por usuario/equipo; esta fase no cambia el modelo silenciosamente.

## Logging

Se registra modo, login/logout, refresh, lecturas/escrituras, conflictos, permisos, red y mismatches. Nunca se registra password, ID token, refresh token, header Authorization ni respuestas de Auth con tokens.
