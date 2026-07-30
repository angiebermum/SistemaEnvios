# Contexto autocontenido para continuar el trabajo

## 1. Qué es la aplicación

`ECS Envío de Correos` (proyecto/solución `ECS.CommissionsMailer`) es una aplicación de escritorio WPF para Windows que prepara y envía por Outlook Classic correos de detalle de comisiones a corredores de seguros y sus asistentes.

Repositorio analizado:

```text
C:\Users\angie\source\repos\SistemaEnvios
```

## 2. Problema que resuelve

Centraliza un directorio de corredores, permite adjuntar archivos Excel distintos por corredor, agrega CC general, asunto, cuerpo y firma, valida el lote, pide confirmaciones de revisión y crea un correo Outlook por cada corredor. Conserva sesión, historial y copias de adjuntos enviados.

## 3. Tecnologías

- C# / .NET 10 (`net10.0-windows`)
- WPF
- Windows x64
- Outlook Classic COM
- JSON local con `System.Text.Json`
- WiX Toolset 5.0.2 para MSI
- NuGet `Microsoft.Office.Interop.Outlook` 15.0.4797.1004 con tipos embebidos

No hay API, servicio, web, base de datos, SMTP directo ni autenticación.

## 4. Arquitectura

Es un monolito WPF con binding y modelos observables, pero no MVVM completo. `MainWindow.xaml.cs` funciona como vista, ViewModel y coordinador. Los servicios son clases concretas construidas manualmente; no hay contenedor DI.

Flujo real:

```text
App.OnStartup
  -> MainWindow/code-behind
    -> validación/configuración/sesión/firma
    -> OutlookEmailService en hilo STA
      -> Outlook Classic COM / MailItem.Send
    -> archivo local + historial JSON + log
```

## 5. Proyectos principales

1. `ECS.CommissionsMailer.csproj`: WPF `WinExe`, entrada principal, .NET 10 Windows x64.
2. `Installer/ECS.EnvioDeCorreos.Installer.wixproj`: MSI x64 per-machine, incluido en la solución.
3. `Diagnostics/OutlookComSmokeTest/OutlookComSmokeTest.csproj`: consola de diagnóstico, fuera de la solución pero documentada.

La solución es `ECS.CommissionsMailer.slnx`. El inicio personalizado está en `App.xaml.cs`, método `OnStartup`; la UI principal es `MainWindow`.

## 6. Funcionalidades actuales

- importar/migrar directorio inicial versionado;
- crear, editar, eliminar, activar y desactivar corredores;
- administrar asistentes y marcas de revisión;
- configurar firma PNG/JPEG;
- guardar asunto, mensaje y CC general;
- seleccionar múltiples Excel por corredor;
- buscar/seleccionar corredores;
- validar destinatarios, duplicados, archivos, firma y revisión;
- conectar/diagnosticar Outlook Classic;
- enviar seleccionados o todos, secuencialmente;
- mostrar progreso y errores por fila;
- archivar copias de Excel;
- guardar sesión e historial;
- consultar, limpiar y reenviar desde historial;
- ejecutar self-test, UI smoke y diagnósticos Outlook por argumentos.

## 7. Flujo principal

1. `App` crea rutas y logger.
2. `MainWindow` carga configuración, sesión e historial.
3. `DirectoryMigrationService` valida/aplica `Data/correos-iniciales.v3.json`, crea respaldo si corresponde y guarda cambios.
4. Se muestran corredores activos y se carga firma.
5. Se comprueba Outlook en un hilo STA.
6. El usuario configura texto/CC, selecciona corredores y adjunta Excel.
7. Se resuelven principales, asistentes y CC; TO tiene precedencia sobre CC.
8. Se exige confirmación para registros con revisión.
9. Se confirma el lote.
10. `OutlookEmailService` fija la cuenta, crea `MailItem`, resuelve destinatarios, adjunta Excel/firma y llama `Send`.
11. La UI guarda resultados, archiva adjuntos y persiste sesión/historial.
12. Al cerrar se vuelve a guardar la sesión.

## 8. Almacenamiento y configuraciones

Todo el estado del usuario está bajo:

```text
%LOCALAPPDATA%\ECSCommissionsMailer\
```

Archivos/carpetas:

- `configuracion.json`
- `sesion-actual.json`
- `envios-recientes.json`
- `Logs\app.log`
- `ArchivosEnviados\`
- `Assets\Firma\`
- `Backups\`

No hay `appsettings.json`, `App.config`, conexión DB ni variables de entorno propias. La semilla y firma inicial se empaquetan desde `Data\`.

La configuración inspeccionada tenía semilla 3, 64 corredores activos, una CC y firma. No se deben copiar correos, nombres ni secretos a informes.

## 9. Integraciones externas

Outlook Classic es la única integración operacional. La aplicación usa automatización COM, requiere perfil/cuenta y asigna explícitamente la cuenta de envío. Los tipos Interop están embebidos; la DLL NuGet no se copia. En una publicación no autocontenida también se requiere .NET 10 Desktop Runtime x64. El MSI autocontenido lleva .NET, pero no Outlook.

## 10. Estado de compilación

Entorno:

```text
.NET SDK 10.0.302
.NET Desktop Runtime 10.0.10
WiX 5.0.2
Windows win-x64
```

Resultados del 2026-07-25:

- restore de `ECS.CommissionsMailer.slnx`: correcto;
- build app Debug: correcto, 0 warnings/0 errores;
- build app Release: correcto, 0 warnings/0 errores;
- build diagnóstico: correcto, 0 warnings/0 errores;
- build WiX en carpeta temporal: correcto, 0 warnings/0 errores;
- `--self-test`: 34 comprobaciones correctas;
- NuGet vulnerable/outdated del proyecto principal: ninguno reportado.

No se hizo envío real ni instalación en otra PC.

## 11. Proceso actual de publicación

Comando documentado:

```powershell
.\Installer\Build-Installer.cmd
```

Requiere SDK .NET 10 y WiX 5.0.2. El script sincroniza datos desde `%LOCALAPPDATA%`, elimina `artifacts\publish\win-x64`, publica Release `win-x64` autocontenida y construye:

```text
artifacts\installer\ECS Envío de Correos - Instalador 1.0.3.msi
```

El MSI instala en `C:\Program Files\ECS Envío de Correos` y crea accesos directos. No existe actualización automática.

## 12. Explicación probable/confirmada de la versión desactualizada

En la computadora analizada, los accesos directos de escritorio y menú Inicio apuntan a:

```text
C:\Program Files\ECS Envío de Correos\ECS Envío de Correos.exe
```

Ese ejecutable y su DLL son versión 1.0.0; el registro también informa instalada 1.0.0. El repositorio tiene publicación/MSI 1.0.3, pero ese MSI no está instalado.

Además existen carpetas antiguas:

```text
publish\win-x64\ECS.CommissionsMailer.exe                   1.0.0
publish\win-x64-self-contained\ECS.CommissionsMailer.exe    1.0.0
```

La publicación vigente para MSI está en:

```text
artifacts\publish\win-x64\ECS Envío de Correos.exe          1.0.3
```

Por tanto, abrir desde el acceso directo ejecuta realmente 1.0.0 y copiar una carpeta `publish` antigua también distribuye 1.0.0. No se encontraron ZIP para auditar.

Los JSON de `%LOCALAPPDATA%` sobreviven upgrades, por lo que datos viejos también pueden persistir aunque el binario cambie.

## 13. Bugs conocidos o riesgos funcionales

1. Si `MailItem.Send` funciona pero luego falla guardar historial/archivar, el correo puede estar enviado sin registro durable; reintentar puede duplicarlo.
2. El archivo fallido no cambia `WasSuccessful`; el resumen puede decir “enviado” aunque falte la copia local.
3. “Enviado” significa que Outlook aceptó `Send`, no que el destinatario recibió.
4. Compilar `.wixproj` directamente no limpia `PublishDir`; el wrapper sí.
5. La lógica de envío/reenvío está duplicada entre `MainWindow` y `ResendWindow`.
6. No hay manejador global de excepciones UI posterior al inicio.
7. La validación de Excel verifica extensión/existencia/apertura, no contenido.

## 14. Riesgos críticos

1. Instalación activa 1.0.0 frente a release 1.0.3.
2. Build del instalador dependiente del perfil local: puede empaquetar destinatarios/firma incorrectos.
3. Falta de estado durable/idempotencia alrededor de envíos ya aceptados por Outlook.

## 15. Deuda técnica principal

- `MainWindow.xaml.cs`: 1.284 líneas y responsabilidades mezcladas.
- `OutlookEmailService.cs`: 636 líneas y dependencia COM concreta.
- `DirectoryMigrationService.cs`: 423 líneas con reglas específicas.
- Sin ViewModels separados, DI ni interfaces de infraestructura.
- Sin proyecto estándar de pruebas ni cobertura medible.
- Versiones hardcoded en múltiples archivos.
- Múltiples raíces de salida ignoradas con artefactos antiguos.
- Datos personales en la semilla versionada/instalador.
- Log sin rotación, retención ni protección.
- Perfil `.pubxml` importante ignorado por Git.

## 16. Mejoras recomendadas primero

1. Probar e instalar el MSI 1.0.3 en VM/equipo controlado y confirmar accesos/upgrade.
2. Establecer una única carpeta de release y manifiesto con versión, commit y hashes.
3. Eliminar la dependencia implícita de `%LOCALAPPDATA%` en el build.
4. Implementar un registro durable por lote/correo antes y después de `Send`.
5. Centralizar versión y mostrarla en UI.
6. Extraer casos de uso de envío/reenvío.
7. Crear proyecto de pruebas y automatizar upgrade MSI.

## 17. Preguntas que necesitan respuesta humana

- ¿Quién es propietario y aprobador del directorio de correos?
- ¿Es aceptable almacenar esos datos en Git y dentro del MSI?
- ¿Qué versión debe considerarse release oficial hoy?
- ¿Qué significa “enviado” para el negocio: aceptado por Outlook o entrega confirmada?
- ¿Cómo se debe actuar ante estado ambiguo después de `Send`?
- ¿Cuánto tiempo deben conservarse historial, adjuntos, backups y logs?
- ¿Debe el instalador preservar siempre `%LOCALAPPDATA%`?
- ¿Cuál es el canal oficial de distribución y quién instala upgrades?
- ¿Se requiere firma de código?
- ¿Qué versiones/arquitecturas de Outlook Classic deben soportarse?

## 18. Archivos más importantes

```text
ECS.CommissionsMailer.slnx
ECS.CommissionsMailer.csproj
App.xaml.cs
MainWindow.xaml
MainWindow.xaml.cs
Services/OutlookEmailService.cs
Services/EmailValidationService.cs
Services/ConfigurationService.cs
Services/DirectoryMigrationService.cs
Services/AtomicJsonFile.cs
Services/SessionService.cs
Services/AttachmentArchiveService.cs
Services/AppDataPaths.cs
Data/correos-iniciales.v3.json
Installer/Build-Installer.ps1
Installer/Sync-InstallerData.ps1
Installer/ECS.EnvioDeCorreos.Installer.wixproj
Installer/Package.wxs
Verification/SelfTestRunner.cs
Diagnostics/OUTLOOK-VALIDATION.md
```

## 19. Comandos para compilar y ejecutar

```powershell
dotnet restore .\ECS.CommissionsMailer.slnx
dotnet build .\ECS.CommissionsMailer.csproj -c Debug
dotnet build .\ECS.CommissionsMailer.csproj -c Release
.\bin\Debug\net10.0-windows\ECS Envío de Correos.exe
.\bin\Debug\net10.0-windows\ECS Envío de Correos.exe --self-test
dotnet run --project .\Diagnostics\OutlookComSmokeTest\OutlookComSmokeTest.csproj -c Debug
.\Installer\Build-Installer.cmd
```

No ejecutar pruebas de envío real sin destinatario expresamente autorizado.

## 20. Estado exacto del repositorio al finalizar el análisis

- Rama: `master`.
- HEAD analizado: `fcce0ef`.
- Estado inicial: limpio.
- Código fuente modificado: no.
- Configuración/dependencias modificadas: no.
- Únicos archivos nuevos intencionales: siete Markdown bajo `docs/system-analysis/`.
- Las compilaciones actualizaron salidas ignoradas en `bin`/`obj`; la prueba WiX se escribió en `%TEMP%`.
- No se hicieron commit, push, pull, merge, reset ni cambio de rama.
