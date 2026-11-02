using System;
using System.Windows.Forms;

namespace SimpleRemoteKVM
{
    static class Program
    {
        /// <summary>
        /// Ponto de entrada principal da aplicação.
        /// Inicializa a interface gráfica moderna do SimpleRemoteKVM.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                System.IO.File.WriteAllText("thread_crash.log", e.Exception.ToString());
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                System.IO.File.WriteAllText("domain_crash.log", e.ExceptionObject.ToString());
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string autoMode = null;
            string autoIp = null;
            int autoPort = NetworkPeer.DEFAULT_PORT;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLower();
                if (arg == "--server" || arg == "-s")
                {
                    autoMode = "server";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        int.TryParse(args[++i], out autoPort);
                    }
                }
                else if (arg == "--client" || arg == "-c")
                {
                    autoMode = "client";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        autoIp = args[++i];
                    }
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        int.TryParse(args[++i], out autoPort);
                    }
                }
            }

            try
            {
                Application.Run(new MainForm(autoMode, autoIp, autoPort));
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("crash.log", ex.ToString());
                MessageBox.Show(ex.ToString(), "Crash Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
