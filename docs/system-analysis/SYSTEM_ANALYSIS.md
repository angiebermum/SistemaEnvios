# Análisis técnico del sistema

Fecha del análisis: 2026-07-25  
Repositorio: `C:\Users\angie\source\repos\SistemaEnvios`  
Rama inspeccionada: `master`  
Commit inspeccionado: `fcce0ef` (`Agregar archivos del proyecto`, 2026-07-23)

## Alcance y método

El análisis fue exclusivamente de inspección, compilación segura y documentación. No se modificó código fuente, configuración, dependencias, instaladores ni datos. Las compilaciones generaron únicamente salidas ignoradas por Git. La prueba del instalador se dirigió a una carpeta temporal aislada.

Validaciones ejecutadas:

```powershell
dotnet --info
dotnet restore .\ECS.CommissionsMailer.slnx --nologo
dotnet build .\ECS.CommissionsMailer.csproj --configuration Debug --no-restore --nologo
dotnet build .\ECS.CommissionsMailer.csproj --configuration Release --no-restore --nologo
dotnet build .\Diagnostics\OutlookComSmokeTest\OutlookComSmokeTest.csproj --configuration Debug --nologo
dotnet list .\ECS.CommissionsMailer.csproj package --vulnerable --include-transitive
dotnet list .\ECS.CommissionsMailer.csproj package --outdated --include-transitive
.\bin\Debug\net10.0-windows\ECS Envío de Correos.exe --self-test
```

También se compiló el proyecto WiX con `PublishDir` y `OutputPath` redirigidos a:

```text
C:\Users\angie\AppData\Local\Temp\ECS-system-analysis-installer-78d3b12bf6cf40018418179d7ff50237
```

No se ejecutaron envíos reales, pruebas interactivas de Outlook, instalación/desinstalación del MSI ni pruebas en otra computadora.

## Identificación general

La solución se llama `ECS.CommissionsMailer` y está declarada en `ECS.CommissionsMailer.slnx`.

| Proyecto | Tipo | Framework/plataforma | Participación |
|---|---|---|---|
| `ECS.CommissionsMailer.csproj` | Aplicación de escritorio WPF (`WinExe`) | `net10.0-windows`, x64 | Proyecto principal y punto de entrada |
| `Installer/ECS.EnvioDeCorreos.Installer.wixproj` | Instalador MSI WiX | WiX Toolset 5.0.2, x64 | Incluido en la solución |
| `Diagnostics/OutlookComSmokeTest/OutlookComSmokeTest.csproj` | Consola diagnóstica | `net10.0-windows`, x64 | Fuera de la solución; utilidad intencional documentada |

El punto de entrada WPF se genera a partir de `App.xaml`. La inicialización personalizada está en `App.xaml.cs:12`, método `App.OnStartup`. La ventana de operación es `MainWindow`, creada en `App.xaml.cs:104`.

No hay API, servicio, base de datos, autenticación ni componente web. Tampoco hay proyectos de pruebas basados en MSTest, NUnit o xUnit.

## Estructura del repositorio

| Ruta | Responsabilidad |
|---|---|
| `App.xaml*`, `MainWindow.xaml*` | Inicio, recursos globales y pantalla principal |
| `Views/` | Diálogos de corredores, asistentes, inactivos, reenvíos y mensajes |
| `Models/` | Configuración, directorio de corredores, sesión, solicitudes y resultados |
| `Services/` | Persistencia JSON, migraciones, validación, firma, archivo de adjuntos, logging e integración Outlook |
| `Helpers/` | `INotifyPropertyChanged`, hilo STA y liberación COM |
| `Themes/` | Diccionarios XAML de estilos |
| `Data/` | Semilla versionada de destinatarios y firma inicial |
| `Verification/` | Batería interna de autocomprobación ejecutable por parámetro |
| `Diagnostics/` | Prueba COM independiente y guía de validación Outlook |
| `Installer/` | Proyecto, UI y scripts WiX |
| `Properties/PublishProfiles/` | Perfil local de publicación; está ignorado por Git |
| `bin/`, `obj/`, `publish/`, `artifacts/`, `.vs/` | Salidas/cachés locales ignoradas; contienen varias generaciones |

`Diagnostics/OutlookComSmokeTest` no parece abandonado: está documentado en `Diagnostics/OUTLOOK-VALIDATION.md`, pero no forma parte de `ECS.CommissionsMailer.slnx`. `SistemaEnvios.csproj.user`, el ejecutable residual `SistemaEnvios.exe` y las publicaciones `ECS.CommissionsMailer.exe` 1.0.0 sí son rastros de nombres o builds anteriores.

## Propósito y flujo de negocio

La aplicación prepara y envía por Outlook Classic correos de detalle de comisiones a corredores y sus asistentes. Cada corredor puede tener uno o más destinatarios principales, asistentes activos, archivos Excel propios y una marca de revisión manual. La aplicación agrega copias generales, asunto, cuerpo y firma, valida el lote, solicita confirmación y crea un `MailItem` por corredor.

Flujo principal real:

```text
App.OnStartup
  -> crea rutas y logger
  -> MainWindow carga configuración, aplica migración de semilla y restaura sesión/historial
  -> comprueba Outlook en un hilo STA
  -> usuario administra destinatarios, firma y adjuntos
  -> MainWindow valida y confirma el lote
  -> OutlookEmailService crea y envía MailItem por corredor
  -> SessionService guarda historial/sesión
  -> AttachmentArchiveService copia los Excel enviados a LocalAppData
```

No existe contenedor de inyección de dependencias. `App` crea `AppDataPaths` y `FileLogger`; `MainWindow` crea manualmente los demás servicios. La aplicación utiliza un estilo híbrido: modelos observables y binding XAML, pero la lógica de presentación y coordinación está en code-behind. No hay ViewModels separados.

## Configuración y almacenamiento

No existen `appsettings.json`, `App.config`, cadena de conexión, base de datos, servidor SMTP, API HTTP ni variables de entorno de aplicación.

Los datos de usuario se guardan en:

```text
%LOCALAPPDATA%\ECSCommissionsMailer\
  configuracion.json
  sesion-actual.json
  envios-recientes.json
  Logs\app.log
  ArchivosEnviados\...
  Assets\Firma\...
  Backups\...
```

La ruta se define en `Services/AppDataPaths.cs:5-18`. La persistencia es JSON con archivo temporal y reemplazo en `Services/AtomicJsonFile.cs:51-82`. Los JSON corruptos se renombran como `.broken-<fecha>` y se cargan valores seguros.

La configuración inspeccionada del usuario tenía semilla 3, 64 corredores activos, una CC general y firma configurada. No se copiaron direcciones, nombres ni rutas personales al informe.

La semilla empaquetada `Data/correos-iniciales.v3.json` contiene 64 corredores, 65 correos principales, 10 asistentes y una CC. Contiene datos personales/empresariales reales y está versionada en Git. No se encontraron contraseñas, claves privadas, tokens de API ni cadenas de conexión; las coincidencias de “token” pertenecen a APIs Win32 de token de proceso.

## Dependencias externas

| Dependencia | Uso y referencia | Requisito en destino | Riesgo |
|---|---|---|---|
| .NET 10 Desktop Runtime | WPF; `ECS.CommissionsMailer.csproj:5` | Sí para `bin/Debug` o `bin/Release`; no para publicación autocontenida | Una salida framework-dependent no funciona sin runtime |
| Outlook Classic COM | Crear, resolver y enviar `MailItem`; `Services/OutlookEmailService.cs` | Outlook Classic instalado, registrado y con perfil/cuenta | Dependencia crítica; New Outlook no ofrece la misma automatización COM |
| `Microsoft.Office.Interop.Outlook` 15.0.4797.1004 | Tipos de interoperabilidad | Tipos embebidos; la DLL NuGet no se copia | Paquete antiguo por numeración, pero NuGet no reportó actualización ni vulnerabilidad en las fuentes consultadas |
| WiX Toolset 5.0.2 | Construcción MSI | Solo en máquina de build | Versión exacta exigida por script |
| `WixToolset.UI.wixext` 5.0.2 | UI del MSI | Se incorpora al build | Sin vulnerabilidades verificadas específicamente para el proyecto WiX |
| Windows Registry y Win32 | Diagnóstico de Outlook/procesos | Windows | Código específico de Windows |
| Archivos Excel `.xlsx`/`.xls` | Adjuntos; no se abren mediante Office Interop | Deben existir y ser legibles | Solo se valida extensión, existencia y apertura; no el contenido real |
| JSON y firma PNG/JPEG | Configuración y firma inline | Deben acompañar la publicación inicial | La ausencia de semilla impide iniciar |

No hay DLL manuales, driver propio, servidor SMTP, base de datos, API web ni ejecutables auxiliares llamados por la aplicación.

## Estado de compilación y pruebas

- SDK disponible: .NET SDK 10.0.302, runtime Desktop 10.0.10, Windows x64.
- WiX disponible: 5.0.2.
- Restauración de la solución: correcta.
- Aplicación Debug: correcta, 0 advertencias, 0 errores.
- Aplicación Release: correcta, 0 advertencias, 0 errores.
- Diagnóstico Outlook: compila, 0 advertencias, 0 errores.
- Instalador WiX en carpeta temporal: compila, 0 advertencias, 0 errores.
- Batería `--self-test`: 34 comprobaciones informadas como correctas.
- Paquetes vulnerables: ninguno reportado por NuGet para el proyecto principal.
- Paquetes desactualizados: ninguno reportado por las fuentes configuradas.

La batería interna cubre validación, persistencia temporal, migración, selección de lote, HTML, firma y archivo. No mide cobertura y no sustituye pruebas unitarias aisladas. La comprobación de Outlook que incluye no envía correo. No se verificó entrega real, cuenta concreta, interfaz interactiva completa ni actualización MSI en otra PC.

## Seguridad

Hallazgos positivos:

- No hay secretos de autenticación ni cadenas de conexión en el código inspeccionado.
- No hay SQL ni deserialización polimórfica insegura.
- Las direcciones se validan con `MailAddress`.
- Los adjuntos se limitan a `.xlsx` y `.xls`, deben existir y deben poder abrirse.
- La cuenta de envío se asigna y vuelve a comprobar antes de enviar.
- Los objetos COM se liberan explícitamente.

Riesgos:

- La semilla versionada y empaquetada contiene nombres y correos reales (`Data/correos-iniciales.v3.json`).
- El historial local guarda destinatarios, asunto, cuerpo y rutas de adjuntos en texto claro (`Models/SentEmailRecord.cs:7-20`).
- El log puede guardar cuenta, nombres de corredores, rutas y trazas completas (`Services/FileLogger.cs:15-54`), sin rotación ni política de retención.
- `Sync-InstallerData.ps1` toma datos del perfil local del operador y los convierte en contenido del instalador; un build desde el perfil equivocado puede distribuir datos incorrectos.

## Git

- Rama: `master`.
- Estado inicial: limpio.
- Commits disponibles: `fcce0ef` y `c9df617`.
- `bin`, `obj`, `.vs`, `publish` y `artifacts` están correctamente ignorados y no rastreados.
- No hay ZIP rastreado ni presente.
- La documentación nueva no está ignorada.
- `Properties/PublishProfiles/win-x64-self-contained.pubxml` y `SistemaEnvios.csproj.user` están ignorados. El perfil es importante para reproducir una ruta de publicación y actualmente depende de estado local.
- `Data/correos-iniciales.v3.json` y `Data/firma-inicial.png` sí están versionados.

## Conclusión ejecutiva

La aplicación compila y su lógica local tiene una batería de comprobación considerable, pero la distribución no tiene una única fuente inequívoca. El acceso directo real abre una instalación 1.0.0, mientras que el instalador y publicación más recientes del repositorio son 1.0.3. Dos carpetas `publish` antiguas conservan ejecutables 1.0.0 con el nombre anterior. Esa evidencia explica directamente la percepción de “versión desactualizada”.

Los siguientes riesgos requieren atención inmediata:

1. Instalar y verificar el MSI 1.0.3 correcto, eliminando la ambigüedad entre salidas.
2. Convertir el build del instalador en un proceso limpio, trazable y basado en entradas explícitas, no en el perfil local implícito.
3. Hacer durable el registro de cada envío inmediatamente después de `MailItem.Send`, para evitar reenvíos duplicados si falla el archivado o la persistencia posterior.

