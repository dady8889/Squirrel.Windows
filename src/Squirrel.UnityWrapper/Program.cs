using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Squirrel.UnityWrapper
{
    class Program
    {
        public const string UpdateURL = "YourUpdateURl";
        public const string ProjectName = "YourProjectName";
        public const string ExeName = ProjectName+".exe";
        public const string DataPath = ProjectName + "_Data";

        static void Main(string[] args)
        {
            using (var mgr = new UpdateManager(UpdateURL))
            {
                // Note, in most of these scenarios, the app exits after this method
                // completes!
                SquirrelAwareApp.HandleEvents(
                    onInitialInstall: v =>
                    {
                        FixupFiles();
                        mgr.CreateShortcutsForExecutable(
                            ExeName, ShortcutLocation.Desktop | ShortcutLocation.StartMenu, false);

                    },
                    onAppUpdate: v =>
                    {
                        FixupFiles();
                        mgr.CreateShortcutsForExecutable(
                            ExeName, ShortcutLocation.Desktop | ShortcutLocation.StartMenu, true);
                    },
                    onAppUninstall: v =>
                    {
                        mgr.RemoveShortcutsForExecutable(ExeName, ShortcutLocation.Desktop | ShortcutLocation.StartMenu);
                    },
                    onFirstRun: () =>
                    {
                        //Do something
                    });

            }
        }

        static string DataDir
        {
            get
            {
                Type type = (new Program()).GetType();
                string currentDirectory = Path.GetDirectoryName(type.Assembly.Location);
                return Path.Combine(currentDirectory, DataPath);
            }
        }

        static void FixupFiles()
        {
            FixupSpaces(
                Path.Combine(DataDir, "Resources"),
                "unity default resources"
            );
        }

        static void FixupSpaces(string dir, string file)
        {
            var newFile = Path.Combine(dir, file);
            var oldFile = Path.Combine(dir, file.Replace(" ", "%20"));
            if (File.Exists(oldFile))
            {
                File.Delete(newFile);
                File.Move(oldFile, newFile);
            }
        }

        static async Task Update(UpdateManager mgr)
        {
            var updateInfo = await mgr.CheckForUpdate();
            if (updateInfo == null || !updateInfo.ReleasesToApply.Any())
            {
                return;
            }
            var releases = updateInfo.ReleasesToApply;
            await mgr.DownloadReleases(releases);
            await mgr.ApplyReleases(updateInfo);
        }
    }
}
