# Instalador de ECS Envío de Correos

El proyecto crea un MSI de 64 bits, autocontenido y en español. Permite seleccionar la carpeta de instalación y decidir si se crea un acceso directo en el escritorio. También crea un acceso directo normal en el menú Inicio.

## Construcción

Requiere .NET SDK 10 y WiX Toolset 5.0.2:

```powershell
dotnet tool install --global wix --version 5.0.2
.\actualizar-instalador.bat
```

El resultado se guarda en `artifacts\installer`.

La versión se mantiene una sola vez en `Directory.Build.props`, mediante la propiedad
`EcsApplicationVersion`. Esa versión se aplica a la aplicación, al MSI, al nombre del archivo y
a los textos visibles.

El proceso usa exclusivamente la semilla y la firma aprobadas y versionadas en `Data`. El build
se detiene si esos archivos tienen cambios locales, para que datos del perfil del operador no
entren accidentalmente al MSI. No se incorporan sesiones, historial, adjuntos ni registros de
envío.

La sincronización de una nueva semilla es una operación manual y separada. Requiere indicar
explícitamente la configuración aprobada:

```powershell
.\Installer\Sync-InstallerData.ps1 -ConfigurationPath C:\ruta\aprobada\configuracion.json
```

La aplicación se publica como `win-x64` autocontenida, por lo que no requiere instalar .NET en la computadora de destino. Outlook Classic sí debe estar instalado y configurado para las funciones de correo.

El script no abre la aplicación ni copia el MSI a carpetas compartidas. También verifica que la
publicación no incluya configuración Firebase local, refresh tokens, ADC ni archivos de service
account.
