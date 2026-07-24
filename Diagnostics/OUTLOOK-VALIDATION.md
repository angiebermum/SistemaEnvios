# Validación de Outlook Classic

Estas pruebas no llaman `MailItem.Send` y no envían correo.

## Prueba COM independiente

Con Outlook Classic abierto y sin cuadros de diálogo modales:

```powershell
dotnet run --project .\Diagnostics\OutlookComSmokeTest\OutlookComSmokeTest.csproj -c Debug
```

La prueba crea un hilo STA, resuelve `Outlook.Application`, obtiene `Session` y el espacio de nombres MAPI, comprueba la cuenta actual, crea un `MailItem`, ejecuta `Display(false)` y lo cierra con `olDiscard`.

## Prueba del ejecutable publicado

```powershell
.\publish\win-x64-self-contained\ECS.CommissionsMailer.exe --outlook-diagnostic
```

El resultado se guarda en:

```text
%TEMP%\ECSCommissionsMailer-outlook-diagnostic-result.txt
```

El diagnóstico técnico completo se agrega a:

```text
%LOCALAPPDATA%\ECSCommissionsMailer\Logs\app.log
```

La aplicación también ofrece el botón **Probar conexión con Outlook**. Esa opción deja abierto el borrador de prueba para que el usuario lo revise y lo cierre manualmente; tampoco lo envía.

## Envío real controlado

Un envío real debe hacerse solamente después de confirmar expresamente el destinatario de prueba. Use una dirección propia o interna, un asunto que contenga `PRUEBA` y un archivo `.xlsx` inofensivo. Después confirme el elemento en **Elementos enviados**, la recepción, el estado **Enviado** de la fila y el archivo local de registro/adjuntos.
