using System;
using System.Windows.Forms;

namespace MidiToKeyApp
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // 独立プロセスからのWinMM列挙リクエスト
            if (args != null && args.Length > 0 && Array.Exists(args, a => string.Equals(a, "--enum-winmm", StringComparison.OrdinalIgnoreCase)))
            {
                var ports = MidiDeviceDiagnostics.GetInProcessWinMmPorts();
                foreach (var p in ports)
                {
                    Console.WriteLine(p);
                }
                return;
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
