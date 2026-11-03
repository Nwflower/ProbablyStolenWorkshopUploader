using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace ProbablyStolenWorkshopUploaderLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            string appFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
            string exe = Path.Combine(appFolder, "ProbablyStolenWorkshopUploader.exe");

            try
            {
                if (!File.Exists(exe))
                    throw new FileNotFoundException("找不到 app 文件夹中的上传器，请完整解压压缩包。", exe);

                var start = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = appFolder,
                    Arguments = OriginalArguments(),
                    UseShellExecute = false,
                };
                Process process = Process.Start(start);
                string[] args = Environment.GetCommandLineArgs();
                if (args.Length > 1 &&
                    (string.Equals(args[1], "--selftest", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(args[1], "--querytest", StringComparison.OrdinalIgnoreCase)))
                {
                    process.WaitForExit();
                    return process.ExitCode;
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动上传器：\n" + ex.Message, "Probably Stolen 创意工坊上传器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string OriginalArguments()
        {
            string commandLine = Environment.CommandLine;
            int i = 0;
            if (commandLine.Length > 0 && commandLine[0] == '"')
            {
                i = commandLine.IndexOf('"', 1);
                i = i < 0 ? commandLine.Length : i + 1;
            }
            else
            {
                while (i < commandLine.Length && !char.IsWhiteSpace(commandLine[i])) i++;
            }
            return commandLine.Substring(i).TrimStart();
        }
    }
}
