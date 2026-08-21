# Firebase Authentication para ECS Comisiones

## Flujo

1. `JsonOnly` abre la aplicación sin Firebase.
2. ShadowRead/Primary intenta recuperar un refresh token protegido.
3. Si no existe o no es válido, muestra `LoginWindow`.
4. `FirebaseAuthenticationService` usa Email/Password por HTTPS REST.
5. Obtiene `uid`, `email`, `idToken`, `refreshToken` y `expiresAtUtc`.
6. Lee `appUsers/{uid}` y exige `isActive=true` y `canUseCommissions=true`.
7. El ID token se envía como `Authorization: Bearer {idToken}` a Firestore REST.

El password solo vive durante la llamada de login y nunca se persiste. El ID token se mantiene en memoria. El refresh token se protege con DPAPI `CurrentUser` en `%LOCALAPPDATA%\ECSCommissionsMailer\firebase-refresh-token.dat`; otro usuario Windows no puede descifrarlo.

El token se renueva cinco minutos antes del vencimiento. Un 401 permite exactamente un refresh y un retry. Logout limpia identidad, ID token y refresh token protegido; no borra JSON, configuración ni LOCAL_ONLY.

## Configuración manual en Firebase Console

1. Abra el proyecto `essential-4eccd`.
2. En **Security > Authentication > Sign-in method**, habilite **Email/Password** y guarde.
3. En **Authentication > Users**, use **Add user** para crear el primer usuario con su correo y una contraseña individual.
4. Copie el **User UID**. No use la contraseña en ninguna herramienta administrativa.
5. Si el proyecto no tiene una app Web, en **Project settings > General > Your apps** registre una app Web interna. No necesita Hosting.
6. Copie el valor `apiKey` de la configuración Web y colóquelo solo en `firebase-runtime.json` o `ECS_FIREBASE_API_KEY`.
7. En Google Cloud Console, **APIs & Services > Credentials**, revise la key. Restrinja su allowlist a los servicios requeridos por Authentication: **Identity Toolkit API** y **Token Service API**. No agregue APIs no Firebase.

Firebase documenta que sus API keys identifican el proyecto, no autorizan datos; Security Rules es la barrera de acceso. Referencias: [API keys de Firebase](https://firebase.google.com/docs/projects/api-keys), [Email/Password](https://firebase.google.com/docs/auth/web/password-auth), [Auth REST](https://firebase.google.com/docs/reference/rest/auth).

## `appUsers`

Cada usuario Auth necesita un documento `appUsers/{uid}`:

```text
uid, email, displayName
role: admin | operator
isActive
canUseCommissions
createdAtUtc
updatedAtUtc
```

Un `operator` usa Comisiones, pero no administra accesos. Un `admin` activo con `canUseCommissions=true` puede listar perfiles, registrar un perfil nuevo con el UID copiado de Firebase Console, activar/inactivar, cambiar rol y cambiar `canUseCommissions`. La UI nunca ve ni cambia passwords de otros usuarios y no crea/elimina cuentas Auth.

Para usuarios posteriores al primero:

1. Un responsable crea manualmente la cuenta Email/Password en Firebase Console.
2. Copia el UID generado por Firebase Authentication.
3. Un admin autorizado abre **Administración de accesos > Registrar perfil** e ingresa UID, correo, nombre, rol y permisos.
4. El WPF crea únicamente `appUsers/{uid}` mediante Firestore REST con el ID token del admin. Security Rules exige que el ID del documento coincida con `uid` y valida todos los campos.

El flujo no comprueba administrativamente que el UID exista en Authentication porque eso requeriría una credencial privilegiada; por eso el UID debe copiarse con cuidado desde Firebase Console.

## Bootstrap del primer admin

La herramienta separada usa ADC fuera del WPF, lee primero y es dry-run por defecto. No se ejecuta automáticamente y queda reservada al primer admin: siempre genera `role=admin`, `isActive=true` y `canUseCommissions=true`, por lo que no es la herramienta de administración para perfiles posteriores.

Dry-run exacto:

```powershell
dotnet run --project .\tools\ECSCommissionsMailer.FirstAdminBootstrap\ECSCommissionsMailer.FirstAdminBootstrap.csproj -- --project-id essential-4eccd --database-id "(default)" --uid "<UID_FIREBASE_AUTH>" --email "<CORREO>" --display-name "<NOMBRE>"
```

Aplicación posterior, únicamente tras revisar el dry-run:

```powershell
dotnet run --project .\tools\ECSCommissionsMailer.FirstAdminBootstrap\ECSCommissionsMailer.FirstAdminBootstrap.csproj -- --project-id essential-4eccd --database-id "(default)" --uid "<UID_FIREBASE_AUTH>" --email "<CORREO>" --display-name "<NOMBRE>" --apply
```

Si el documento ya existe, la herramienta se bloquea. `--overwrite-existing` es una confirmación explícita separada y no debe usarse para el primer bootstrap normal.

Crear, eliminar o deshabilitar cuentas Auth desde el WPF requeriría un backend privilegiado (Cloud Functions/Cloud Run). Esa capacidad queda fuera de esta fase para no incrustar un service account.
