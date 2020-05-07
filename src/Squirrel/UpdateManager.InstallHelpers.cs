using Microsoft.Win32;
using NuGet;
using Splat;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Squirrel
{
    public sealed partial class UpdateManager
    {
        internal class InstallHelperImpl : IEnableLogger
        {
            readonly string applicationName;
            readonly string rootAppDirectory;

            public InstallHelperImpl(string applicationName, string rootAppDirectory) {
                this.applicationName = applicationName;
                this.rootAppDirectory = rootAppDirectory;
            }

            public void UpdateUninstallRegisty() {
                var releaseContent = File.ReadAllText(Path.Combine(rootAppDirectory, "packages", "RELEASES"), Encoding.UTF8);
                var releases = ReleaseEntry.ParseReleaseFile(releaseContent);
                var latest = releases.Where(x => !x.IsDelta).OrderByDescending(x => x.Version).First();

                var pkgPath = Path.Combine(rootAppDirectory, "packages", latest.Filename);
                var zp = new ZipPackage(pkgPath);

                var installInfoFile = Path.Combine(this.rootAppDirectory, ".installInfo.json");
                var installInfoResult = Utility.GetInstallInfo(installInfoFile);
                if (installInfoResult.Item1) {
                    var verion = installInfoResult.Item2.installVersion as string;
                    var productCode = installInfoResult.Item2.productCode as string;
                    var appName = installInfoResult.Item2.appName as string;
                    var arch = installInfoResult.Item2.arch == "x86" ? "x86" : "x64";
                    var key = GetUninstallRegKey(productCode, appName, arch);
                    if (key != null) {
                        try {
                            key.SetValue("DisplayVersion", zp.Version.ToString());
                            key.Close();
                            this.Log().Info($"UpdateUninstallRegisty: DisplayVersion updated to {zp.Version.ToString()}");
                        } catch (Exception ex) {
                            this.Log().ErrorException("UpdateUninstallRegisty: Failed to write current version to registry.", ex);
                        }
                    } else {
                        this.Log().Error("UpdateUninstallRegisty: Registry key not found or no write access. Unable to update current version.");
                    }
                } else {
                    this.Log().Error("UpdateUninstallRegisty: Unable to update current version without installInfo.");
                }
            }

            public void KillAllProcessesBelongingToPackage() {
                var ourExe = Assembly.GetEntryAssembly();
                var ourExePath = ourExe != null ? ourExe.Location : null;

                UnsafeUtility.EnumerateProcesses()
                    .Where(x => {
                        // Processes we can't query will have an empty process name, we can't kill them
                        // anyways
                        if (String.IsNullOrWhiteSpace(x.Item1)) return false;

                        // Files that aren't in our root app directory are untouched
                        if (!x.Item1.StartsWith(rootAppDirectory, StringComparison.OrdinalIgnoreCase)) return false;

                        // Never kill our own EXE
                        if (ourExePath != null && x.Item1.Equals(ourExePath, StringComparison.OrdinalIgnoreCase)) return false;

                        var name = Path.GetFileName(x.Item1).ToLowerInvariant();
                        if (name == "msq.exe" || name == "squirrel.exe" || name == "update.exe") return false;

                        return true;
                    })
                    .ForEach(x => {
                        try {
                            this.WarnIfThrows(() => Process.GetProcessById(x.Item2).Kill());
                        } catch { }
                    });
            }

            RegistryKey GetUninstallRegKey(string productCode, string appName, string arch) {
                var keyPath = $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{{{productCode}}}.msisquirrel";

                bool validateKey(RegistryKey key) {
                    if (key == null) {
                        return false;
                    }

                    if (key.GetValueNames().Any(x => x == "InstallPath")) {
                        char[] trimChars = { '\\' };
                        var installPath = key.GetValue("InstallPath").ToString().TrimEnd(trimChars);
                        if (installPath == this.rootAppDirectory.TrimEnd(trimChars)) {
                            this.Log().Info($"GetUninstallRegKey: {key.ToString()} found.");
                            return true;
                        } else {
                            return false;
                        }
                    } else {
                        return false;
                    }
                }

                RegistryKey openWriteableKey(string path, RegistryHive hive, RegistryView view) {
                    try {
                        return RegistryKey.OpenBaseKey(hive, view).OpenSubKey(keyPath, true);
                    } catch (Exception ex) {
                        this.Log().ErrorException($"GetUninstallRegKey: Failed to open write acces to {hive} {view} {keyPath}", ex);
                    }
                    return null;
                }

                try {
                    var hive = RegistryHive.LocalMachine;
                    var view = arch == "x86" ? RegistryView.Registry32 : RegistryView.Registry64;
                    var regKey = RegistryKey.OpenBaseKey(hive, view)
                        .OpenSubKey(keyPath, false);
                    if (validateKey(regKey)) {
                        regKey.Close();
                        return openWriteableKey(keyPath, hive, view);
                    }
                } catch { }

                try {
                    var hive = RegistryHive.CurrentUser;
                    var view = RegistryView.Default;
                    var regKey = RegistryKey.OpenBaseKey(hive, view)
                        .OpenSubKey(keyPath, false);
                    if (validateKey(regKey)) {
                        regKey.Close();
                        return openWriteableKey(keyPath, hive, view);
                    }
                } catch { }

                this.Log().Error($"GetUninstallRegKey: {keyPath} not found in neither, HKLM nor HKCU.");
                return null;
            }
        }
    }
}
