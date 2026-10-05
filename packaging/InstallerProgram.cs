using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("剧本改稿同步 MOD  一键安装")]
[assembly: AssemblyProduct("剧本改稿同步 MOD  一键安装")]
[assembly: AssemblyDescription("剧本改稿同步 MOD 一键安装与卸载")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]

namespace AzureArchive.RevisionCompare.Installation
{
    internal static class Program
    {
        internal static byte[] Resource(string name)
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (input == null) throw new IOException("安装器缺少内嵌文件，请重新获取完整安装包。");
                using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }
        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        internal static string DetectRoot()
        {
            var candidates = new List<string> { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory };
            string host = AppDomain.CurrentDomain.GetData("AARevisionCompare.InstallerHost") as string;
            string folder = String.IsNullOrEmpty(host) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(host);
            for (int i = 0; i < 5 && !String.IsNullOrEmpty(folder); i++)
            {
                candidates.Add(folder);
                folder = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar));
            }
            foreach (string drive in Directory.GetLogicalDrives())
                foreach (string name in new[] { "AzureArchive_100_fix", "AzureArchive" })
                    candidates.Add(Path.Combine(drive, name));
            foreach (string candidate in candidates)
                try
                {
                    if (File.Exists(Path.Combine(candidate, "AzureArchive.exe")) && Directory.Exists(Path.Combine(candidate, "AzureArchive_Data")))
                        return Path.GetFullPath(candidate);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            return "";
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            {
                try
                {
                    if (args.Length == 1 && args[0] == "--verify")
                    {
                        Console.WriteLine(new JavaScriptSerializer().Serialize(new {
                            Product = InstallerCore.ProductName, Version = InstallerCore.Version,
                            PluginSha256 = Hash(Resource("revisioncompare.plugin")),
                            ManifestSha256 = Hash(Resource("revisioncompare.manifest")),
                            Title = "剧本改稿同步 MOD  一键安装"
                        }));
                        return 0;
                    }
                    if (args.Length != 2 || (args[0] != "--install" && args[0] != "--uninstall"))
                        throw new ArgumentException("用法：--install AA目录、--uninstall AA目录或 --verify");
                    var result = args[0] == "--install"
                        ? InstallerCore.Install(args[1], Resource("revisioncompare.plugin"), Resource("revisioncompare.manifest"))
                        : InstallerCore.Uninstall(args[1]);
                    Console.WriteLine(result.ToString());
                    return result.Preserved.Count == 0 ? 0 : 2;
                }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new InstallerWindow(args.Length == 1 ? args[0] : DetectRoot()));
            return 0;
        }
    }
}
