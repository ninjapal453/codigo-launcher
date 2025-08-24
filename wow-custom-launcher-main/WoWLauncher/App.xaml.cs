// App.xaml.cs — handler global de excepciones (WPF)
using System;
using System.Windows;

namespace WoWLauncher
{
    public partial class App : Application
    {
        public App()
        {
            // Excepciones no capturadas en el hilo de UI
            this.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(
                    e.Exception.ToString(),
                    "Error no controlado (UI)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                e.Handled = true; // evita que cierre la app
            };

            // Excepciones no observadas en Tasks en segundo plano
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    MessageBox.Show(
                        ex?.ToString() ?? "Excepción no controlada (non-UI).",
                        "Error no controlado (BG)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { /* último recurso: nada */ }
            };
        }
    }
}
