using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Verification;

internal static class ModuleWindowUiSmokeRunner
{
    public static bool Run()
    {
        var bindingLogPath = Path.Combine(
            Path.GetTempPath(),
            "ECSCommissionsMailer-module-ui-binding-errors.log");
        File.WriteAllText(bindingLogPath, string.Empty);
        using var bindingListener = new TextWriterTraceListener(bindingLogPath);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        var previousShutdownMode = Application.Current.ShutdownMode;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var user = new AppUser
            {
                Uid = "ui-smoke",
                Email = "ui-smoke@example.test",
                DisplayName = "Usuario de prueba visual",
                Role = AppUserRole.Admin,
                IsActive = true,
                CanUseCommissions = true,
                CanUseExpirations = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            var selectorPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-module-selection-ui-smoke.png");
            var expirationsPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-ui-smoke.png");
            var expirationsMinimumPath = Path.Combine(
                Path.GetTempPath(),
                "ECSCommissionsMailer-expirations-ui-smoke-minimum.png");

            Render(new ModuleSelectionWindow(user), selectorPath);
            Render(
                new ExpirationsWindow(user, new NonOperationalAppUserRepository()),
                expirationsPath,
                expirationsMinimumPath);
            bindingListener.Flush();
            var hasBindingErrors = new FileInfo(bindingLogPath).Length > 0;
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-module-ui-smoke-result.txt"),
                string.Join(Environment.NewLine,
                    "VENTANAS_MODULO_INICIADAS=SI",
                    $"ERRORES_BINDING={(hasBindingErrors ? "SI" : "NO")}",
                    $"SELECTOR={selectorPath}",
                    $"VENCIMIENTOS={expirationsPath}",
                    $"VENCIMIENTOS_MINIMO={expirationsMinimumPath}",
                    $"LOG_BINDINGS={bindingLogPath}"));
            return !hasBindingErrors;
        }
        finally
        {
            Application.Current.ShutdownMode = previousShutdownMode;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
        }
    }

    private static void Render(Window window, string path, string? minimumPath = null)
    {
        window.Show();
        window.UpdateLayout();
        Capture(window, path);
        if (minimumPath is not null)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            Capture(window, minimumPath);
        }
        window.Close();
    }

    private static void Capture(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            encoder.Save(stream);
    }

    private sealed class NonOperationalAppUserRepository : IAppUserRepository
    {
        public Task<FirestoreStoredDocument<AppUser>?> GetAsync(
            string uid,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");

        public Task<IReadOnlyList<FirestoreStoredDocument<AppUser>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");

        public Task<FirestoreStoredDocument<AppUser>> CreateAsync(
            AppUser value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");

        public Task<FirestoreStoredDocument<AppUser>> UpdateAsync(
            AppUser value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("El smoke test visual no realiza operaciones Firestore.");
    }
}
