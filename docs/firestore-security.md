# Seguridad Firestore de ECS Comisiones

`firestore.rules` contiene la propuesta de producción. No se despliega automáticamente.

## Principios

- `isSignedIn()` comprueba Firebase Auth.
- `currentAppUser()` consulta `appUsers/{request.auth.uid}`.
- `isActive()` exige perfil existente y activo.
- `canUseCommissions()` exige el permiso funcional.
- `isAdmin()` exige acceso a Comisiones y role `admin`.
- `system/**` permanece denegado para clientes WPF.
- settings, brokers, sessions, recentSends y paymentGenerations requieren perfil activo y permiso de Comisiones.
- Un usuario lee su propio perfil; un admin puede listar, crear y actualizar perfiles.
- Crear perfiles exige un admin activo con `canUseCommissions=true`, forma válida y coincidencia exacta entre `appUsers/{uid}` y el campo `uid`.
- Crear perfiles queda denegado a usuarios no autenticados y operators. Eliminar perfiles continúa denegado para todos los clientes.
- El bootstrap inicial usa Admin SDK/ADC exclusivamente para establecer el primer admin; el WPF no incorpora esas credenciales.
- En updates de `appUsers`, `uid` y `createdAtUtc` son inmutables, hay allowlist de campos y role solo puede ser `admin` u `operator`.

Las bibliotecas servidor/Admin SDK autenticadas por IAM omiten Security Rules, por lo que bootstrap y migración administrativa siguen funcionando sin abrir acceso al WPF. Referencias: [condiciones de reglas](https://firebase.google.com/docs/firestore/security/rules-conditions), [campos protegidos](https://firebase.google.com/docs/firestore/security/rules-fields), [consultas y bypass de SDK servidor](https://firebase.google.com/docs/firestore/security/rules-query).

## Tests en emulador

Los tests cubren usuario anónimo, sin perfil, inactive, sin permiso, operator válido, auto-promoción, modificación de otro perfil, admin, denegación de `system/schema`, denegación de `system/migrationState` y bypass administrativo del emulador. También cubren create de `appUsers`: no autenticado, operator, admin válido, UID discordante, role inválido, shape inválido y delete siempre denegado.

```powershell
npm install --prefix .\Tests\FirestoreRules
npm test --prefix .\Tests\FirestoreRules
```

Esto usa el proyecto demo local `demo-ecs-commissions`; no toca `essential-4eccd`.

## Despliegue manual futuro

Después de revisar el diff, ejecutar tests y autorizar expresamente el cambio:

```powershell
firebase deploy --only firestore:rules --project essential-4eccd
```

No ejecutar ese comando como parte de la implementación. Antes del despliegue, conservar una copia de las reglas actuales y verificar que el CLI apunta a `(default)`.
