using NuGet;
using Splat;
using Squirrel.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Squirrel.Update
{
    enum UpdateAction
    {
        Unset = 0, Install, Uninstall, Download, Update, Releasify, Shortcut,
        Deshortcut, ProcessStart, UpdateSelf, CheckForUpdate
    }

    class Program : IEnableLogger
    {
        static StartupOption opt;

        public static int Main(string[] args) {
            //  var appName = getAppNameFromDirectory("C:\\Program Files (x86)\\HelloWix");

            // var mgr = new UpdateManager("http://localhost", appName, "C:\\Program Files (x86)");
            //mgr.CreateUninstallerRegistryEntry();


            var pg = new Program();
            try {
                return pg.main(args);
            } catch (Exception ex) {
                // NB: Normally this is a terrible idea but we want to make
                // sure Setup.exe above us gets the nonzero error code
                Console.Error.WriteLine(ex);
                return -1;
            }
        }

        int main(string[] args) {
            try {
                opt = new StartupOption(args);
            } catch (Exception ex) {
                using (var logger = new SetupLogLogger(true, "OptionParsing") { Level = LogLevel.Info }) {
                    Locator.CurrentMutable.Register(() => logger, typeof(Splat.ILogger));
                    logger.Write($"Failed to parse command line options. {ex.Message}", LogLevel.Error);
                }
                throw;
            }

            // NB: Trying to delete the app directory while we have Setup.log
            // open will actually crash the uninstaller
            bool isUninstalling = opt.updateAction == UpdateAction.Uninstall;

            using (var logger = new SetupLogLogger(isUninstalling, opt.updateAction.ToString()) { Level = LogLevel.Info }) {
                Locator.CurrentMutable.Register(() => logger, typeof(Splat.ILogger));

                try {
                    return executeCommandLine(args);
                } catch (Exception ex) {
                    logger.Write("Finished with unhandled exception: " + ex, LogLevel.Fatal);
                    throw;
                }
            }
        }

        int executeCommandLine(string[] args) {
            var animatedGifWindowToken = new CancellationTokenSource();

#if !MONO
            // Uncomment to test Gifs
            /*
            var ps = new ProgressSource();
            int i = 0; var t = new Timer(_ => ps.Raise(i += 10), null, 0, 1000);
            AnimatedGifWindow.ShowWindow(TimeSpan.FromMilliseconds(0), animatedGifWindowToken.Token, ps);
            Thread.Sleep(10 * 60 * 1000);
            */
#endif

            using (Disposable.Create(() => animatedGifWindowToken.Cancel())) {
                this.Log().Info(" Squirrel Updater: " + String.Join(" ", args));

                if (args.Any(x => x.StartsWith("/squirrel", StringComparison.OrdinalIgnoreCase))) {
                    // NB: We're marked as Squirrel-aware, but we don't want to do
                    // anything in response to these events
                    return 0;
                }

                if (opt.updateAction == UpdateAction.Unset) {
                    ShowHelp();
                    return -1;
                }

                switch (opt.updateAction) {
#if !MONO
                    case UpdateAction.Install:
                        this.Log().Warn("Uninstall command not supported in Msi Squirrel");
                        break;
                    case UpdateAction.Uninstall:
                        this.Log().Warn("Uninstall command not supported in Msi Squirrel");
                        break;
                    case UpdateAction.Download:
                        Console.WriteLine(Download(opt.target).Result);
                        break;
                    case UpdateAction.Update:
                        Update(opt.target).Wait();
                        break;
                    case UpdateAction.CheckForUpdate:
                        Console.WriteLine(CheckForUpdate(opt.target).Result);
                        break;
                    case UpdateAction.UpdateSelf:
                        UpdateSelf().Wait();
                        break;
                    case UpdateAction.Shortcut:
                        this.Log().Warn("Shortcut command not supported in Msi Squirrel");
                        break;
                    case UpdateAction.Deshortcut:
                        this.Log().Warn("Deshortcut command not supported in Msi Squirrel");
                        break;
                    case UpdateAction.ProcessStart:
                        this.Log().Warn("ProcessStart command not supported in Msi Squirrel");
                        break;
#endif
                    case UpdateAction.Releasify:
                        this.Log().Warn("Releasify command not supported in Msi Squirrel");
                        // Releasify(opt.target, opt.releaseDir, opt.packagesDir, opt.bootstrapperExe, opt.backgroundGif, opt.signingParameters, opt.baseUrl, opt.setupIcon, !opt.noMsi, opt.packageAs64Bit, opt.frameworkVersion, !opt.noDelta);
                        break;
                }
            }
            this.Log().Info("Finished Squirrel Updater");
            return 0;
        }

        public async Task Update(string updateUrl, string appName = null) {
            appName = appName ?? getAppNameFromDirectory();

            this.Log().Info("Starting update, downloading from " + updateUrl);

            using (var mgr = new UpdateManager(updateUrl, appName)) {
                bool ignoreDeltaUpdates = false;
                this.Log().Info("About to update to: " + mgr.RootAppDirectory);

            retry:
                try {
                    var updateInfo = await mgr.CheckForUpdate(intention: UpdaterIntention.Update, ignoreDeltaUpdates: ignoreDeltaUpdates, progress: x => Console.WriteLine(x / 3));
                    await mgr.DownloadReleases(updateInfo.ReleasesToApply, x => Console.WriteLine(33 + x / 3));
                    await mgr.ApplyReleases(updateInfo, x => Console.WriteLine(66 + x / 3));
                } catch (Exception ex) {
                    if (ignoreDeltaUpdates) {
                        this.Log().ErrorException("Really couldn't apply updates!", ex);
                        throw;
                    }

                    this.Log().WarnException("Failed to apply updates, falling back to full updates", ex);
                    ignoreDeltaUpdates = true;
                    goto retry;
                }

                var updateTarget = Path.Combine(mgr.RootAppDirectory, "Update.exe");

                this.ErrorIfThrows(() =>
                    mgr.UpdateUninstallerVersionRegistryEntry(),
                    "Failed to update uninstaller registry entry");
            }
        }

        public async Task UpdateSelf() {
            waitForParentToExit();
            var src = Assembly.GetExecutingAssembly().Location;
            var updateDotExeForOurPackage = Path.Combine(
                Path.GetDirectoryName(src),
                "..", "Update.exe");

            await Task.Run(() => {
                File.Copy(src, updateDotExeForOurPackage, true);
            });
        }

        public async Task<string> Download(string updateUrl, string appName = null) {
            appName = appName ?? getAppNameFromDirectory();

            this.Log().Info("Fetching update information, downloading from " + updateUrl);
            using (var mgr = new UpdateManager(updateUrl, appName)) {
                var updateInfo = await mgr.CheckForUpdate(intention: UpdaterIntention.Update, progress: x => Console.WriteLine(x / 3));
                await mgr.DownloadReleases(updateInfo.ReleasesToApply, x => Console.WriteLine(33 + x / 3));

                var releaseNotes = updateInfo.FetchReleaseNotes();

                var sanitizedUpdateInfo = new {
                    currentVersion = updateInfo.CurrentlyInstalledVersion.Version.ToString(),
                    futureVersion = updateInfo.FutureReleaseEntry.Version.ToString(),
                    releasesToApply = updateInfo.ReleasesToApply.Select(x => new {
                        version = x.Version.ToString(),
                        releaseNotes = releaseNotes.ContainsKey(x) ? releaseNotes[x] : "",
                    }).ToArray(),
                };

                return SimpleJson.SerializeObject(sanitizedUpdateInfo);
            }
        }

        public async Task<string> CheckForUpdate(string updateUrl, string appName = null) {
            appName = appName ?? getAppNameFromDirectory();

            this.Log().Info("Fetching update information, downloading from " + updateUrl);
            using (var mgr = new UpdateManager(updateUrl, appName)) {
                var updateInfo = await mgr.CheckForUpdate(intention: UpdaterIntention.Update, progress: x => Console.WriteLine(x));
                var releaseNotes = updateInfo.FetchReleaseNotes();
                var currentVersion = ensureCurrentVersion(updateInfo.CurrentlyInstalledVersion);

                var sanitizedUpdateInfo = new {
                    currentVersion = currentVersion.ToString(),
                    futureVersion = updateInfo.FutureReleaseEntry.Version.ToString(),
                    releasesToApply = updateInfo.ReleasesToApply
                        .Where(x => x.Version > currentVersion)
                        .Select(x => new {
                            version = x.Version.ToString(),
                            releaseNotes = releaseNotes.ContainsKey(x) ? releaseNotes[x] : "",
                        }).ToArray(),
                };

                return SimpleJson.SerializeObject(sanitizedUpdateInfo);
            }
        }

        public void Releasify(string package, string targetDir = null, string packagesDir = null, string bootstrapperExe = null, string backgroundGif = null, string signingOpts = null, string baseUrl = null, string setupIcon = null, bool generateMsi = true, bool packageAs64Bit = false, string frameworkVersion = null, bool generateDeltas = true) {
            ensureConsole();

            if (baseUrl != null) {
                if (!Utility.IsHttpUrl(baseUrl)) {
                    throw new Exception(string.Format("Invalid --baseUrl '{0}'. A base URL must start with http or https and be a valid URI.", baseUrl));
                }

                if (!baseUrl.EndsWith("/")) {
                    baseUrl += "/";
                }
            }

            targetDir = targetDir ?? Path.Combine(".", "Releases");
            packagesDir = packagesDir ?? ".";
            bootstrapperExe = bootstrapperExe ?? Path.Combine(".", "Setup.exe");

            if (!Directory.Exists(targetDir)) {
                Directory.CreateDirectory(targetDir);
            }

            if (!File.Exists(bootstrapperExe)) {
                bootstrapperExe = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetEntryAssembly().Location),
                    "Setup.exe");
            }

            this.Log().Info("Bootstrapper EXE found at:" + bootstrapperExe);

            var di = new DirectoryInfo(targetDir);
            File.Copy(package, Path.Combine(di.FullName, Path.GetFileName(package)), true);

            var allNuGetFiles = di.EnumerateFiles()
                .Where(x => x.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));

            var toProcess = allNuGetFiles.Where(x => !x.Name.Contains("-delta") && !x.Name.Contains("-full"));
            var processed = new List<string>();

            var releaseFilePath = Path.Combine(di.FullName, "RELEASES");
            var previousReleases = new List<ReleaseEntry>();
            if (File.Exists(releaseFilePath)) {
                previousReleases.AddRange(ReleaseEntry.ParseReleaseFile(File.ReadAllText(releaseFilePath, Encoding.UTF8)));
            }

            foreach (var file in toProcess) {
                this.Log().Info("Creating release package: " + file.FullName);

                var rp = new ReleasePackage(file.FullName);
                rp.CreateReleasePackage(Path.Combine(di.FullName, rp.SuggestedReleaseFileName), packagesDir, contentsPostProcessHook: pkgPath => {
                    new DirectoryInfo(pkgPath).GetAllFilesRecursively()
                        .Where(x => x.Name.ToLowerInvariant().EndsWith(".exe"))
                        .Where(x => !x.Name.ToLowerInvariant().Contains("squirrel.exe"))
                        .Where(x => Utility.IsFileTopLevelInPackage(x.FullName, pkgPath))
                        .Where(x => Utility.ExecutableUsesWin32Subsystem(x.FullName))
                        .ForEachAsync(x => createExecutableStubForExe(x.FullName))
                        .Wait();

                    if (signingOpts == null) return;

                    new DirectoryInfo(pkgPath).GetAllFilesRecursively()
                        .Where(x => Utility.FileIsLikelyPEImage(x.Name))
                        .ForEachAsync(async x => {
                            if (isPEFileSigned(x.FullName)) {
                                this.Log().Info("{0} is already signed, skipping", x.FullName);
                                return;
                            }

                            this.Log().Info("About to sign {0}", x.FullName);
                            await signPEFile(x.FullName, signingOpts);
                        }, 1)
                        .Wait();
                });

                processed.Add(rp.ReleasePackageFile);

                var prev = ReleaseEntry.GetPreviousRelease(previousReleases, rp, targetDir);
                if (prev != null && generateDeltas) {
                    var deltaBuilder = new DeltaPackageBuilder(null);

                    var dp = deltaBuilder.CreateDeltaPackage(prev, rp,
                        Path.Combine(di.FullName, rp.SuggestedReleaseFileName.Replace("full", "delta")));
                    processed.Insert(0, dp.InputPackageFile);
                }
            }

            foreach (var file in toProcess) { File.Delete(file.FullName); }

            var newReleaseEntries = processed
                .Select(packageFilename => ReleaseEntry.GenerateFromFile(packageFilename, baseUrl))
                .ToList();
            var distinctPreviousReleases = previousReleases
                .Where(x => !newReleaseEntries.Select(e => e.Version).Contains(x.Version));
            var releaseEntries = distinctPreviousReleases.Concat(newReleaseEntries).ToList();

            ReleaseEntry.WriteReleaseFile(releaseEntries, releaseFilePath);

            var targetSetupExe = Path.Combine(di.FullName, "Setup.exe");
            var newestFullRelease = releaseEntries.MaxBy(x => x.Version).Where(x => !x.IsDelta).First();

            File.Copy(bootstrapperExe, targetSetupExe, true);
            var zipPath = createSetupEmbeddedZip(Path.Combine(di.FullName, newestFullRelease.Filename), di.FullName, backgroundGif, signingOpts, setupIcon).Result;

            var writeZipToSetup = Utility.FindHelperExecutable("WriteZipToSetup.exe");

            try {
                var arguments = String.Format("\"{0}\" \"{1}\" \"--set-required-framework\" \"{2}\"", targetSetupExe, zipPath, frameworkVersion);
                var result = Utility.InvokeProcessAsync(writeZipToSetup, arguments, CancellationToken.None).Result;
                if (result.Item1 != 0) throw new Exception("Failed to write Zip to Setup.exe!\n\n" + result.Item2);
            } catch (Exception ex) {
                this.Log().ErrorException("Failed to update Setup.exe with new Zip file", ex);
            } finally {
                File.Delete(zipPath);
            }

            Utility.Retry(() =>
                setPEVersionInfoAndIcon(targetSetupExe, new ZipPackage(package), setupIcon).Wait());

            if (signingOpts != null) {
                signPEFile(targetSetupExe, signingOpts).Wait();
            }
        }

        public void ShowHelp() {
            ensureConsole();
            opt.WriteOptionDescriptions();
        }

        SemanticVersion ensureCurrentVersion(ReleaseEntry currentlyInstalledVersion) {
            var currentVersionString = currentlyInstalledVersion?.Version != null ? currentlyInstalledVersion.Version.ToString() : "0.0.0";
            if (currentVersionString == "0.0.0") {
                this.Log().Info("ensureCurrentVersion: No locally installed release package found. This is likely the first update of an MSI install. Looking for latest version via App folder name.");
                try {
                    var appFolderPrefix = "app-";
                    var subDirs = Directory.EnumerateDirectories(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)).Select(x => new DirectoryInfo(x));
                    var appDirNames = subDirs
                        .Where(x => x.Name.StartsWith(appFolderPrefix))
                        .Where(x => x.GetFiles(".dead").Count() == 0)
                        .Select(x => x.Name);
                    var currentVersion = SemanticVersion.Parse(currentVersionString);

                    if (appDirNames.Count() == 0) {
                        this.Log().Error("ensureCurrentVersion: No App folders found or all marked as .dead. Unable to retrieve version.");
                    }
                    foreach (var appDirName in appDirNames) {
                        SemanticVersion version;
                        if (SemanticVersion.TryParse(appDirName.Substring(appFolderPrefix.Length, appDirName.Length - appFolderPrefix.Length), out version)) {
                            currentVersion = version > currentVersion ? version : currentVersion;
                        }
                    }
                    currentVersionString = currentVersion.ToString();
                } catch (Exception ex) {
                    this.Log().ErrorException("ensureCurrentVersion: Failed to retrieve latest version via App folder name.", ex);
                }
            }

            // If we still have no version then lets look into the installInfo file. This should not happen btw.
            if (currentVersionString == "0.0.0") {
                this.Log().Warn("ensureCurrentVersion: No local release package found and version retrieval via App folder naming failed. Looking for version in MSI installInfo file.");
                var installInfoFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), ".installInfo.json");
                if (File.Exists(installInfoFile)) {
                    try {
                        dynamic x = SimpleJson.DeserializeObject(File.ReadAllText(installInfoFile));
                        currentVersionString = x.baseVersion;
                    } catch (Exception ex) {
                        this.Log().ErrorException("ensureCurrentVersion: Failed to read version from installInfo file. Gonna report version 0.0.0", ex);
                    }
                } else {
                    this.Log().Warn("ensureCurrentVersion: installInfo file not found. Gonna report version 0.0.0");
                }
            }

            return SemanticVersion.Parse(currentVersionString);
        }

        void waitForParentToExit() {
            // Grab a handle the parent process
            var parentPid = NativeMethods.GetParentProcessId();
            var handle = default(IntPtr);

            // Wait for our parent to exit
            try {
                handle = NativeMethods.OpenProcess(ProcessAccess.Synchronize, false, parentPid);
                if (handle != IntPtr.Zero) {
                    this.Log().Info("About to wait for parent PID {0}", parentPid);
                    NativeMethods.WaitForSingleObject(handle, 0xFFFFFFFF /*INFINITE*/);
                } else {
                    this.Log().Info("Parent PID {0} no longer valid - ignoring", parentPid);
                }
            } finally {
                if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
            }
        }

        async Task<string> createSetupEmbeddedZip(string fullPackage, string releasesDir, string backgroundGif, string signingOpts, string setupIcon) {
            string tempPath;

            this.Log().Info("Building embedded zip file for Setup.exe");
            using (Utility.WithTempDirectory(out tempPath, null)) {
                this.ErrorIfThrows(() => {
                    File.Copy(Assembly.GetEntryAssembly().Location.Replace("-Mono.exe", ".exe"), Path.Combine(tempPath, "Update.exe"));
                    File.Copy(fullPackage, Path.Combine(tempPath, Path.GetFileName(fullPackage)));
                }, "Failed to write package files to temp dir: " + tempPath);

                if (!String.IsNullOrWhiteSpace(backgroundGif)) {
                    this.ErrorIfThrows(() => {
                        File.Copy(backgroundGif, Path.Combine(tempPath, "background.gif"));
                    }, "Failed to write animated GIF to temp dir: " + tempPath);
                }

                if (!String.IsNullOrWhiteSpace(setupIcon)) {
                    this.ErrorIfThrows(() => {
                        File.Copy(setupIcon, Path.Combine(tempPath, "setupIcon.ico"));
                    }, "Failed to write icon to temp dir: " + tempPath);
                }

                var releases = new[] { ReleaseEntry.GenerateFromFile(fullPackage) };
                ReleaseEntry.WriteReleaseFile(releases, Path.Combine(tempPath, "RELEASES"));

                var target = Path.GetTempFileName();
                File.Delete(target);

                // Sign Update.exe so that virus scanners don't think we're
                // pulling one over on them
                if (signingOpts != null) {
                    var di = new DirectoryInfo(tempPath);

                    var files = di.EnumerateFiles()
                        .Where(x => x.Name.ToLowerInvariant().EndsWith(".exe"))
                        .Select(x => x.FullName);

                    await files.ForEachAsync(x => signPEFile(x, signingOpts));
                }

                this.ErrorIfThrows(() =>
                    ZipFile.CreateFromDirectory(tempPath, target, CompressionLevel.Optimal, false),
                    "Failed to create Zip file from directory: " + tempPath);

                return target;
            }
        }

        static async Task signPEFile(string exePath, string signingOpts) {
            // Try to find SignTool.exe
            var exe = @".\signtool.exe";
            if (!File.Exists(exe)) {
                exe = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "signtool.exe");

                // Run down PATH and hope for the best
                if (!File.Exists(exe)) exe = "signtool.exe";
            }

            var processResult = await Utility.InvokeProcessAsync(exe,
                String.Format("sign {0} \"{1}\"", signingOpts, exePath), CancellationToken.None);

            if (processResult.Item1 != 0) {
                var optsWithPasswordHidden = new Regex(@"/p\s+\w+").Replace(signingOpts, "/p ********");
                var msg = String.Format("Failed to sign, command invoked was: '{0} sign {1} {2}'",
                    exe, optsWithPasswordHidden, exePath);

                throw new Exception(msg);
            } else {
                Console.WriteLine(processResult.Item2);
            }
        }
        bool isPEFileSigned(string path) {
#if MONO
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
#else
            try {
                return AuthenticodeTools.IsTrusted(path);
            } catch (Exception ex) {
                this.Log().ErrorException("Failed to determine signing status for " + path, ex);
                return false;
            }
#endif
        }

        async Task createExecutableStubForExe(string fullName) {
            var exe = Utility.FindHelperExecutable(@"StubExecutable.exe");

            var target = Path.Combine(
                Path.GetDirectoryName(fullName),
                Path.GetFileNameWithoutExtension(fullName) + "_ExecutionStub.exe");

            await Utility.CopyToAsync(exe, target);

            await Utility.InvokeProcessAsync(
                Utility.FindHelperExecutable("WriteZipToSetup.exe"),
                String.Format("--copy-stub-resources \"{0}\" \"{1}\"", fullName, target),
                CancellationToken.None);
        }

        static async Task setPEVersionInfoAndIcon(string exePath, IPackage package, string iconPath = null) {
            var realExePath = Path.GetFullPath(exePath);
            var company = String.Join(",", package.Authors);
            var verStrings = new Dictionary<string, string>() {
                { "CompanyName", company },
                { "LegalCopyright", package.Copyright ?? "Copyright © " + DateTime.Now.Year.ToString() + " " + company },
                { "FileDescription", package.Summary ?? package.Description ?? "Installer for " + package.Id },
                { "ProductName", package.Description ?? package.Summary ?? package.Id },
            };

            var args = verStrings.Aggregate(new StringBuilder("\"" + realExePath + "\""), (acc, x) => { acc.AppendFormat(" --set-version-string \"{0}\" \"{1}\"", x.Key, x.Value); return acc; });
            args.AppendFormat(" --set-file-version {0} --set-product-version {0}", package.Version.ToString());
            if (iconPath != null) {
                args.AppendFormat(" --set-icon \"{0}\"", Path.GetFullPath(iconPath));
            }

            // Try to find rcedit.exe
            string exe = Utility.FindHelperExecutable("rcedit.exe");

            var processResult = await Utility.InvokeProcessAsync(exe, args.ToString(), CancellationToken.None);

            if (processResult.Item1 != 0) {
                var msg = String.Format(
                    "Failed to modify resources, command invoked was: '{0} {1}'\n\nOutput was:\n{2}",
                    exe, args, processResult.Item2);

                throw new Exception(msg);
            } else {
                Console.WriteLine(processResult.Item2);
            }
        }

        static string getAppNameFromDirectory(string path = null) {
            path = path ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return (new DirectoryInfo(path)).Name;
        }

        static int consoleCreated = 0;
        static void ensureConsole() {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            if (Interlocked.CompareExchange(ref consoleCreated, 1, 0) == 1) return;

            if (!NativeMethods.AttachConsole(-1)) {
                NativeMethods.AllocConsole();
            }

            NativeMethods.GetStdHandle(StandardHandles.STD_ERROR_HANDLE);
            NativeMethods.GetStdHandle(StandardHandles.STD_OUTPUT_HANDLE);
        }
    }

    public class ProgressSource
    {
        public event EventHandler<int> Progress;

        public void Raise(int i) {
            if (Progress != null)
                Progress.Invoke(this, i);
        }
    }

    class SetupLogLogger : Splat.ILogger, IDisposable
    {
        TextWriter inner;
        readonly object gate = 42;
        public Splat.LogLevel Level { get; set; }

        public SetupLogLogger(bool saveInTemp, string commandSuffix = null) {
            for (int i = 0; i < 10; i++) {
                try {
                    var dir = saveInTemp ?
                        Path.GetTempPath() :
                        Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
                    var fileName = commandSuffix == null ? String.Format($"Squirrel.{i}.log", i) : String.Format($"Squirrel-{commandSuffix}.{i}.log", i);
                    var file = Path.Combine(dir, fileName.Replace(".0.log", ".log"));
                    var str = File.Open(file, FileMode.Append, FileAccess.Write, FileShare.Read);
                    inner = new StreamWriter(str, Encoding.UTF8, 4096, false) { AutoFlush = true };
                    return;
                } catch (Exception ex) {
                    // Didn't work? Keep going
                    Console.Error.WriteLine("Couldn't open log file, trying new file: " + ex.ToString());
                }
            }

            inner = Console.Error;
        }

        public void Write(string message, LogLevel logLevel) {
            if (logLevel < Level) {
                return;
            }

            lock (gate) inner.WriteLine($"[{DateTime.Now.ToString("dd/MM/yy HH:mm:ss")}] {logLevel.ToString().ToLower()}: {message}");
        }

        public void Dispose() {
            lock (gate) {
                inner.Flush();
                inner.Dispose();
            }
        }
    }
}
