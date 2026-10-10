using System;
using System.Windows.Forms;

namespace SimpleFtp
{
    internal static class Program
    {
        /// <summary>Punto de entrada de la aplicación WinForms.</summary>
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
