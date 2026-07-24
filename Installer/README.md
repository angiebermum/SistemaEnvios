# Instalador de ECS Envío de Correos 1.0.3

El proyecto crea un MSI de 64 bits, autocontenido y en español. Permite seleccionar la carpeta de instalación y decidir si se crea un acceso directo en el escritorio. También crea un acceso directo normal en el menú Inicio.

## Construcción

Requiere .NET SDK 10 y WiX Toolset 5.0.2:

```powershell
dotnet tool install --global wix --version 5.0.2
.\Installer\Build-Installer.cmd
```

El resultado se guarda en `artifacts\installer`.

Antes de publicar, el proceso toma la configuración activa de
`%LOCALAPPDATA%\ECSCommissionsMailer\configuracion.json`, elimina las rutas locales y genera una
semilla portátil en `Data\correos-iniciales.v3.json`. La firma configurada se copia como recurso
del instalador. No se incorporan sesiones, historial, adjuntos ni registros de envío.

La aplicación se publica como `win-x64` autocontenida, por lo que no requiere instalar .NET en la computadora de destino. Outlook Classic sí debe estar instalado y configurado para las funciones de correo.
