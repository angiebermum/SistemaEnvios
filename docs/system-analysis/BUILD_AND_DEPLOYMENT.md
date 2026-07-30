# Compilación, publicación y distribución

## Entorno detectado

```text
Windows 10.0.26200, win-x64
.NET SDK 10.0.302
MSBuild 18.6.11 (incluido con dotnet)
.NET Desktop Runtime 10.0.10
WiX 5.0.2
MSBuild clásico en PATH: no
Workloads adicionales: ninguno
```

La solución usa proyectos SDK-style; no requiere MSBuild clásico. WPF se compila con el SDK instalado.

## Resultados

| Validación | Resultado |
|---|---|
| `dotnet restore ECS.CommissionsMailer.slnx` | Correcta |
| Build aplicación Debug | Correcta, 0 advertencias, 0 errores |
| Build aplicación Release | Correcta, 0 advertencias, 0 errores |
| Build diagnóstico Debug | Correcta, 0 advertencias, 0 errores |
| Build WiX Release aislado | Correcta, 0 advertencias, 0 errores |
| `--self-test` | 34 comprobaciones correctas |
| Paquetes vulnerables | Ninguno reportado |
| Paquetes desactualizados | Ninguno reportado |

El build WiX de prueba se hizo en `%TEMP%\ECS-system-analysis-installer-78d3b12bf6cf40018418179d7ff50237`. No se reemplazó `artifacts`.

## Salidas de aplicación

### Framework-dependent

```text
bin\Debug\net10.0-windows\ECS Envío de Correos.exe
bin\Release\net10.0-windows\ECS Envío de Correos.exe
```

Necesitan, como conjunto:

- `.exe`;
- `ECS Envío de Correos.dll`;
- `.deps.json`;
- `.runtimeconfig.json`;
- carpeta `Data`;
- .NET 10 Desktop Runtime x64 instalado.

El PDB solo es necesario para diagnóstico.

### Autocontenida

```text
artifacts\publish\win-x64\ECS Envío de Correos.exe
```

Necesita toda la carpeta publicada, no solo el `.exe`. Incluye runtime .NET/WPF, recursos de idioma y `Data`. Outlook Classic continúa siendo obligatorio.

## Ejecutables relevantes encontrados

Los `.exe` de .NET moderno son apphosts nativos; por eso no contienen una Assembly Version administrada legible. La Assembly Version se obtuvo de la DLL principal asociada.

| Ejecutable | Ruta | Fecha observada | Tamaño | Assembly Version asociada | File Version | Product Version | Probable uso |
|---|---|---:|---:|---|---|---|---|
| ECS Envío de Correos | `bin\Debug\net10.0-windows\...exe` | 2026-07-25 17:47 | 189.440 B | 1.0.3.0 | 1.0.3.0 | 1.0.3 + commit | Desarrollo |
| ECS Envío de Correos | `bin\Release\net10.0-windows\...exe` | 2026-07-25 17:47 | 189.440 B | 1.0.3.0 | 1.0.3.0 | 1.0.3 + commit | Build Release, no autocontenido |
| ECS Envío de Correos | `artifacts\publish\win-x64\...exe` | 2026-07-20 13:22 | 189.440 B | 1.0.3.0 | 1.0.3.0 | 1.0.3 | Publicación usada por MSI 1.0.3 |
| ECS Envío de Correos | `C:\Program Files\ECS Envío de Correos\...exe` | 2026-07-18 18:19 | 189.440 B | 1.0.0.0 | 1.0.0.0 | 1.0.0 | Instalación actualmente abierta por accesos directos |
| ECS.CommissionsMailer | `publish\win-x64\...exe` | 2026-07-18 15:00 | 162.304 B | 1.0.0.0 | 1.0.0.0 | 1.0.0 | Publicación antigua, nombre anterior |
| ECS.CommissionsMailer | `publish\win-x64-self-contained\...exe` | 2026-07-18 17:02 | 189.440 B | 1.0.0.0 | 1.0.0.0 | 1.0.0 | Publicación antigua autocontenida |
| SistemaEnvios | `bin\Debug\net10.0-windows\SistemaEnvios.exe` | 2026-07-18 14:16 | 162.304 B | DLL asociada no vigente | 1.0.0.0 | 1.0.0 | Residuo de nombre/build anterior |
| OutlookComSmokeTest | `Diagnostics\OutlookComSmokeTest\bin\Debug\...\OutlookComSmokeTest.exe` | regenerado 2026-07-25 | 162.304 B | 1.0.0.0 | 1.0.0.0 | 1.0.0 + commit | Diagnóstico, no distribución |
| `createdump.exe` | varias publicaciones | runtime .NET | 71.512 B | N/A | runtime 10 | runtime 10 | Componente del runtime, no aplicación |

Los `obj\...\apphost.exe` son intermediarios, no distribuibles.

## MSI

Artefacto existente:

```text
artifacts\installer\ECS Envío de Correos - Instalador 1.0.3.msi
```

Fecha: 2026-07-20 13:24  
Tamaño: 50.500.600 bytes.

El MSI:

- es x64 y `perMachine`;
- instala por defecto en `ProgramFiles64Folder\ECS Envío de Correos`;
- crea acceso del menú Inicio;
- ofrece acceso de escritorio;
- usa `MajorUpgrade`;
- incorpora todo `$(PublishDir)\**`.

## Accesos directos e instalación observada

Se resolvieron dos accesos directos:

```text
C:\Users\Public\Desktop\ECS Envío de Correos.lnk
C:\ProgramData\Microsoft\Windows\Start Menu\Programs\ECS Envío de Correos\ECS Envío de Correos.lnk
```

Ambos apuntan a:

```text
C:\Program Files\ECS Envío de Correos\ECS Envío de Correos.exe
```

El registro de Windows reporta instalada la versión **1.0.0**, fecha 2026-07-18. El ejecutable y DLL instalados también son 1.0.0. No había proceso de la aplicación en ejecución durante la inspección.

## Diagnóstico de la versión desactualizada

### Causa confirmada en esta computadora

El acceso directo correcto del MSI abre una instalación antigua 1.0.0. El MSI 1.0.3 existe en el repositorio, pero no está instalado. Por tanto, abrir desde escritorio/Inicio produce de forma determinista la versión antigua.

### Factores que facilitan el error

1. Existen tres raíces de publicación: `publish\win-x64`, `publish\win-x64-self-contained` y `artifacts\publish\win-x64`.
2. Las dos primeras contienen 1.0.0 y usan el nombre anterior `ECS.CommissionsMailer.exe`.
3. `artifacts\publish\win-x64` contiene 1.0.3 con el nombre actual.
4. `bin\Debug`/`bin\Release` ahora contienen builds aún más recientes, pero no son el paquete instalado.
5. `publish` y `artifacts` están ignorados; Git no puede demostrar qué binarios había al crear un ZIP/MSI.
6. No hay ZIP presente que permita auditar un paquete copiado anteriormente.
7. El número 1.0.3 está repetido manualmente en proyecto, WiX, UI y README.
8. No hay actualización automática.

Los datos locales persisten en `%LOCALAPPDATA%`; por diseño, instalar una nueva versión no reemplaza sesión, configuración o historial. Parte de una apariencia “vieja” puede venir de esos JSON, aunque en esta máquina el binario instalado también es inequívocamente antiguo.

## Riesgo de archivos viejos mezclados

`Build-Installer.ps1` elimina `artifacts\publish\win-x64` antes de compilar, lo cual reduce el riesgo. Sin embargo, compilar directamente el `.wixproj` ejecuta `dotnet publish` sin limpiar previamente y luego incluye `$(PublishDir)\**`. Un archivo obsoleto que ya esté en la carpeta podría terminar dentro del MSI.

El script recomendado también ejecuta `Sync-InstallerData.ps1`, que modifica la semilla versionada a partir de `%LOCALAPPDATA%`. Esto hace que el contenido del instalador dependa de la computadora y perfil del operador.

## Ejecutable que debe distribuirse

Para usuarios finales no debe copiarse un `.exe` aislado ni comprimirse `bin`. Debe distribuirse:

```text
artifacts\installer\ECS Envío de Correos - Instalador 1.0.3.msi
```

después de reconstruirlo mediante un proceso limpio y controlado y verificar su hash/versión. El MSI instala el conjunto autocontenido completo, mantiene accesos directos y aplica `MajorUpgrade`.

Si excepcionalmente se distribuye de forma portátil, debe copiarse la carpeta completa `artifacts\publish\win-x64`, no solo el ejecutable.

## Proceso actual

Proceso documentado:

```powershell
dotnet tool install --global wix --version 5.0.2
.\Installer\Build-Installer.cmd
```

El script:

1. exige WiX 5.0.2;
2. sincroniza datos desde `%LOCALAPPDATA%`;
3. elimina la publicación de `artifacts`;
4. ejecuta el build WiX Release;
5. deja el MSI en `artifacts\installer`.

Es funcional, pero no plenamente reproducible por su entrada implícita local y por la ausencia de manifiesto/hash de release.

## Validaciones pendientes

- Instalar el MSI 1.0.3 en una máquina limpia o VM.
- Confirmar actualización in-place desde 1.0.0 y retiro de archivos antiguos.
- Verificar acceso directo, versión visible y datos preservados.
- Probar Outlook Classic real con cuenta controlada.
- Ejecutar un envío real solo a dirección expresamente autorizada.
- Confirmar elemento en Enviados y recepción.
- Comparar hashes del MSI distribuido con el aprobado.

