# ShadowRead, Cutover Preflight y rollback

## Estado inicial

La implementación deja `RuntimeDataMode=JsonOnly`. No cambia `system/migrationState`, no reescribe los 1365 documentos y no elimina los JSON.

## 1. Preparar configuración local sin activar Primary

Cree `%LOCALAPPDATA%\ECSCommissionsMailer\firebase-runtime.json`:

```json
{
  "ProjectId": "essential-4eccd",
  "DatabaseId": "(default)",
  "FirebaseApiKey": "<WEB_API_KEY_RESTRINGIDA>",
  "RuntimeDataMode": "FirestoreShadowRead",
  "RememberSession": true
}
```

Antes debe habilitar Email/Password, crear el usuario Auth, bootstrapear `appUsers/{uid}` y desplegar manualmente las reglas revisadas.

## 2. Ejecutar ShadowRead

Inicie la aplicación normalmente:

```powershell
dotnet run --project .\ECS.CommissionsMailer.csproj
```

Inicie sesión. La UI sigue usando JSON y el comparador no escribe Firestore. El reporte queda en:

`%LOCALAPPDATA%\ECSCommissionsMailer\firestore-shadow-reports\shadow-read-*.json`

Resultado esperado antes del cutover, si los JSON no cambiaron desde Fase 2:

```text
LocalCount: 1365
FirestoreCount: 1365
Identical: 1365
MissingInFirestore: 0
MissingLocally: 0
Different: 0
```

Si la aplicación productiva cambió desde la migración, es correcto que aparezcan diferencias. ShadowRead no las repara ni escoge ganador.

## 3. Cutover Preflight explícito y read-only

Mantenga `RuntimeDataMode=FirestoreShadowRead` y cierre cualquier otra instancia. Ejecute:

```powershell
dotnet run --project .\ECS.CommissionsMailer.csproj -- --firestore-cutover-preflight
```

El comando autentica al usuario, lee ambas fuentes, genera `%LOCALAPPDATA%\ECSCommissionsMailer\firestore-cutover-reports\cutover-preflight-*.json`, muestra conteos y termina. No cambia el modo, JSON, documentos operativos ni migrationState.

El preflight solo está limpio cuando `MissingInFirestore=0`, `MissingLocally=0` y `Different=0`. Si hay diferencias, Primary queda bloqueado. No sobrescriba Firestore ni JSON para “arreglarlo” sin revisar cada documento y definir un procedimiento de reconciliación separado.

`sessions/current.savedAtUtc` es la única excepción explícita: representa metadata volátil de persistencia/autosave. ShadowRead y Cutover Preflight la reportan como `NON_BLOCKING_METADATA_DIFFERENCE`, la incluyen en `NonBlockingMetadataPaths` y mantienen el documento dentro de `Identical` funcional. No se elimina del modelo ni de la persistencia. Cualquier otro campo de `sessions/current`, cualquier brokerItem y cualquier otro timestamp continúan comparándose estrictamente y pueden bloquear el cutover.

## 4. Cutover

No activar `FirestorePrimary` todavía. La activación requiere una autorización posterior, reglas desplegadas, ShadowRead limpio y preflight limpio. Incluso con configuración explícita, el primer inicio de Primary vuelve a ejecutar el preflight si `firestore-runtime-state.json` no registra escrituras cloud.

## Rollback diseñado

Antes de la primera escritura SHARED en Primary, se puede cerrar la aplicación y volver el modo a `JsonOnly`; los JSON siguen intactos.

Después de la primera escritura cloud, `firestore-runtime-state.json` marca `HasCloudWrites=true`. Desde ese instante no es seguro volver simplemente a JSON: el JSON shared puede estar desactualizado y crearía una segunda fuente de verdad. El rollback posterior necesita una exportación/reconciliación cloud-to-local diseñada y validada en otra fase. No existe rollback destructivo ni dual-write silencioso.

Los paths y archivos físicos nunca participan en la reconciliación cloud: permanecen en `local-workspace.json` y en el filesystem de cada PC.
