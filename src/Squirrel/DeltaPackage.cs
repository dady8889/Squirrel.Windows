using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Splat;
using DeltaCompressionDotNet.MsDelta;
using System.ComponentModel;
using Squirrel.Bsdiff;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Writers;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Compressors.Deflate;
using VCDiff.Decoders;
using VCDiff.Encoders;
using VCDiff.Includes;

namespace Squirrel
{
    public interface IDeltaPackageBuilder
    {
        ReleasePackage CreateDeltaPackage(ReleasePackage basePackage, ReleasePackage newPackage, string outputFile);
        ReleasePackage ApplyDeltaPackage(ReleasePackage basePackage, ReleasePackage deltaPackage, string outputFile);
    }

    public class DeltaPackageBuilder : IEnableLogger, IDeltaPackageBuilder
    {
        readonly string localAppDirectory;
        public DeltaPackageBuilder(string localAppDataOverride = null)
        {
            this.localAppDirectory = localAppDataOverride;
        }

        public ReleasePackage CreateDeltaPackage(ReleasePackage basePackage, ReleasePackage newPackage, string outputFile)
        {
            Contract.Requires(basePackage != null);
            Contract.Requires(!String.IsNullOrEmpty(outputFile) && !File.Exists(outputFile));

            if (basePackage.Version > newPackage.Version) {
                var message = String.Format(
                    "You cannot create a delta package based on version {0} as it is a later version than {1}",
                    basePackage.Version,
                    newPackage.Version);
                throw new InvalidOperationException(message);
            }

            if (basePackage.ReleasePackageFile == null) {
                throw new ArgumentException("The base package's release file is null", "basePackage");
            }

            if (!File.Exists(basePackage.ReleasePackageFile)) {
                throw new FileNotFoundException("The base package release does not exist", basePackage.ReleasePackageFile);
            }

            if (!File.Exists(newPackage.ReleasePackageFile)) {
                throw new FileNotFoundException("The new package release does not exist", newPackage.ReleasePackageFile);
            }

            string baseTempPath = null;
            string tempPath = null;

            using (Utility.WithTempDirectory(out baseTempPath, null))
            using (Utility.WithTempDirectory(out tempPath, null)) {
                var baseTempInfo = new DirectoryInfo(baseTempPath);
                var tempInfo = new DirectoryInfo(tempPath);

                this.Log().Info("Extracting {0} and {1} into {2}", 
                    basePackage.ReleasePackageFile, newPackage.ReleasePackageFile, tempPath);

                Utility.ExtractZipToDirectory(basePackage.ReleasePackageFile, baseTempInfo.FullName).Wait();
                Utility.ExtractZipToDirectory(newPackage.ReleasePackageFile, tempInfo.FullName).Wait();

                // Collect a list of relative paths under 'lib' and map them
                // to their full name. We'll use this later to determine in
                // the new version of the package whether the file exists or
                // not.
                var baseLibFiles = baseTempInfo.GetAllFilesRecursively()
                    .Where(x => x.FullName.ToLowerInvariant().Contains("lib" + Path.DirectorySeparatorChar))
                    .ToDictionary(k => k.FullName.Replace(baseTempInfo.FullName, ""), v => v.FullName);

                var newLibDir = tempInfo.GetDirectories().First(x => x.Name.ToLowerInvariant() == "lib");

                foreach (var libFile in newLibDir.GetAllFilesRecursively()) {
                    createDeltaForSingleFile(libFile, tempInfo, baseLibFiles);
                }

                ReleasePackage.addDeltaFilesToContentTypes(tempInfo.FullName);
                Utility.CreateZipFromDirectory(outputFile, tempInfo.FullName).Wait();
            }

            return new ReleasePackage(outputFile);
        }

        public ReleasePackage ApplyDeltaPackage(ReleasePackage basePackage, ReleasePackage deltaPackage, string outputFile)
        {
            Contract.Requires(deltaPackage != null);
            Contract.Requires(!String.IsNullOrEmpty(outputFile) && !File.Exists(outputFile));

            string workingPath;
            string deltaPath;

            using (Utility.WithTempDirectory(out deltaPath, localAppDirectory))
            using (Utility.WithTempDirectory(out workingPath, localAppDirectory)) {
                var opts = new ExtractionOptions() { ExtractFullPath = true, Overwrite = true, PreserveFileTime = true };

                using (var za = ZipArchive.Open(deltaPackage.InputPackageFile))
                using (var reader = za.ExtractAllEntries()) {
                    reader.WriteAllToDirectory(deltaPath, opts);
                }
                using (var za = ZipArchive.Open(basePackage.InputPackageFile))
                using (var reader = za.ExtractAllEntries()) {
                    reader.WriteAllToDirectory(workingPath, opts);
                }

                var pathsVisited = new List<string>();

                var deltaPathRelativePaths = new DirectoryInfo(deltaPath).GetAllFilesRecursively()
                    .Select(x => x.FullName.Replace(deltaPath + Path.DirectorySeparatorChar, ""))
                    .ToArray();

                // Apply all of the .diff files
                deltaPathRelativePaths
                    .Where(x => x.StartsWith("lib", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !x.EndsWith(".shasum", StringComparison.InvariantCultureIgnoreCase))
                    .Where(x => !x.EndsWith(".diff", StringComparison.InvariantCultureIgnoreCase) ||
                                !deltaPathRelativePaths.Contains(x.Replace(".diff", ".bsdiff")))
                    .ForEach(file => {
                        pathsVisited.Add(Regex.Replace(file, @"\.(bs)?diff$", "").ToLowerInvariant());
                        applyDiffToFile(deltaPath, file, workingPath);
                    });

                // Delete all of the files that were in the old package but
                // not in the new one.
                new DirectoryInfo(workingPath).GetAllFilesRecursively()
                    .Select(x => x.FullName.Replace(workingPath + Path.DirectorySeparatorChar, "").ToLowerInvariant())
                    .Where(x => x.StartsWith("lib", StringComparison.InvariantCultureIgnoreCase) && !pathsVisited.Contains(x))
                    .ForEach(x => {
                        this.Log().Info("{0} was in old package but not in new one, deleting", x);
                        File.Delete(Path.Combine(workingPath, x));
                    });

                // Update all the files that aren't in 'lib' with the delta
                // package's versions (i.e. the nuspec file, etc etc).
                deltaPathRelativePaths
                    .Where(x => !x.StartsWith("lib", StringComparison.InvariantCultureIgnoreCase))
                    .ForEach(x => {
                        this.Log().Info("Updating metadata file: {0}", x);
                        File.Copy(Path.Combine(deltaPath, x), Path.Combine(workingPath, x), true);
                    });

                this.Log().Info("Repacking into full package: {0}", outputFile);
                using (var za = ZipArchive.Create())
                using (var tgt = File.OpenWrite(outputFile)) {
                    za.DeflateCompressionLevel = CompressionLevel.BestSpeed;
                    za.AddAllFromDirectory(workingPath);
                    za.SaveTo(tgt);
                }
            }

            return new ReleasePackage(outputFile);
        }

        void createDeltaForSingleFile(FileInfo targetFile, DirectoryInfo workingDirectory, Dictionary<string, string> baseFileListing)
        {
            // NB: There are three cases here that we'll handle:
            //
            // 1. Exists only in new => leave it alone, we'll use it directly.
            // 2. Exists in both old and new => write a dummy file so we know
            //    to keep it.
            // 3. Exists in old but changed in new => create a delta file
            //
            // The fourth case of "Exists only in old => delete it in new"
            // is handled when we apply the delta package
            var relativePath = targetFile.FullName.Replace(workingDirectory.FullName, "");

            if (!baseFileListing.ContainsKey(relativePath)) {
                this.Log().Info("{0} not found in base package, marking as new", relativePath);
                return;
            }
            
            //var oldData = File.ReadAllBytes(baseFileListing[relativePath]);
            //var newData = File.ReadAllBytes(targetFile.FullName);
            var oldFile = baseFileListing[relativePath];
            var newFile = targetFile.FullName;

            if (FileEquals(baseFileListing[relativePath], targetFile.FullName)) {
                this.Log().Info("{0} hasn't changed, writing dummy file", relativePath);

                File.Create(targetFile.FullName + ".diff").Dispose();
                File.Create(targetFile.FullName + ".shasum").Dispose();
                targetFile.Delete();
                return;
            }

            this.Log().Info("Delta patching {0} => {1}", baseFileListing[relativePath], targetFile.FullName);
            var msDelta = new MsDeltaCompression();

            if (targetFile.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || 
                targetFile.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                targetFile.Extension.Equals(".node", StringComparison.OrdinalIgnoreCase)) {
                try {
                    msDelta.CreateDelta(baseFileListing[relativePath], targetFile.FullName, targetFile.FullName + ".diff");
                    goto exit;
                } catch (Exception) {
                    this.Log().Warn("We couldn't create a delta for {0}, attempting to create bsdiff", targetFile.Name);
                }
            }
            
            try {
                using (FileStream output = new FileStream(targetFile.FullName + ".bsdiff", FileMode.Create, FileAccess.Write))
                using (FileStream dict = new FileStream(oldFile, FileMode.Open, FileAccess.Read))
                using (FileStream target = new FileStream(newFile, FileMode.Open, FileAccess.Read))
                {
                    VCCoder coder = new VCCoder(dict, target, output);
                    VCDiffResult result = coder.Encode(); //encodes with no checksum and not interleaved
                    if (result != VCDiffResult.SUCCESS)
                    {
                        //error was not able to encode properly
                        throw new Exception($"VCDiff {result == VCDiffResult.ERRROR}");
                    }
                    // NB: Create a dummy corrupt .diff file so that older 
                    // versions which don't understand bsdiff will fail out
                    // until they get upgraded, instead of seeing the missing
                    // file and just removing it.
                    File.WriteAllText(targetFile.FullName + ".diff", "1");
                }
            } catch (Exception ex) {
                this.Log().WarnException(String.Format("We really couldn't create a delta for {0}", targetFile.Name), ex);

                Utility.DeleteFileHarder(targetFile.FullName + ".bsdiff", true);
                Utility.DeleteFileHarder(targetFile.FullName + ".diff", true);
                return;
            }

        exit:
            using (var newFileStream = new FileStream(targetFile.FullName, FileMode.Open))
            {
                var rl = ReleaseEntry.GenerateFromFile(newFileStream, targetFile.Name + ".shasum");
                File.WriteAllText(targetFile.FullName + ".shasum", rl.EntryAsString, Encoding.UTF8);
            }
            targetFile.Delete();
        }

        static bool FileEquals(string fileName1, string fileName2)
        {
            // Check the file size and CRC equality here.. if they are equal...    
            using (var file1 = new FileStream(fileName1, FileMode.Open))
            using (var file2 = new FileStream(fileName2, FileMode.Open))
                return FileStreamEquals(file1, file2);
        }

        static bool FileStreamEquals(Stream stream1, Stream stream2)
        {
            const int bufferSize = 2048;
            byte[] buffer1 = new byte[bufferSize]; //buffer size
            byte[] buffer2 = new byte[bufferSize];
            while (true)
            {
                int count1 = stream1.Read(buffer1, 0, bufferSize);
                int count2 = stream2.Read(buffer2, 0, bufferSize);

                if (count1 != count2)
                    return false;

                if (count1 == 0)
                    return true;

                // You might replace the following with an efficient "memcmp"
                if (!buffer1.Take(count1).SequenceEqual(buffer2.Take(count2)))
                    return false;
            }
        }

        void applyDiffToFile(string deltaPath, string relativeFilePath, string workingDirectory)
        {
            var inputFile = Path.Combine(deltaPath, relativeFilePath);
            var finalTarget = Path.Combine(workingDirectory, Regex.Replace(relativeFilePath, @"\.(bs)?diff$", ""));

            var tempTargetFile = default(string);
            Utility.WithTempFile(out tempTargetFile, localAppDirectory);

            try {
                // NB: Zero-length diffs indicate the file hasn't actually changed
                if (new FileInfo(inputFile).Length == 0) {
                    this.Log().Info("{0} exists unchanged, skipping", relativeFilePath);
                    return;
                }

                 if (relativeFilePath.EndsWith(".bsdiff", StringComparison.InvariantCultureIgnoreCase)) {
                     using (FileStream output = File.OpenWrite(tempTargetFile))
                    using (FileStream dict = File.OpenRead(finalTarget))
                    using (FileStream target = File.OpenRead(inputFile))
                    {
                        this.Log().Info("Applying BSDiff to {0}", relativeFilePath);
                        VCDecoder decoder = new VCDecoder(dict, target, output);

                        //You must call decoder.Start() first. The header of the delta file must be available before calling decoder.Start()

                        VCDiffResult result = decoder.Start();

                        if (result != VCDiffResult.SUCCESS)
                        {
                            //error abort
                            throw new Exception($"VCDiff {result == VCDiffResult.ERRROR}");
                        }
                        long bytesWritten = 0;
                        result = decoder.Decode(out bytesWritten);

                        if (result != VCDiffResult.SUCCESS)
                        {
                            //error decoding
                            throw new Exception($"VCDiff {result == VCDiffResult.ERRROR}");
                        }
                        //if success bytesWritten will contain the number of bytes that were decoded
                    }

                    verifyPatchedFile(relativeFilePath, inputFile, tempTargetFile);
                 } else if (relativeFilePath.EndsWith(".diff", StringComparison.InvariantCultureIgnoreCase)) {
                    this.Log().Info("Applying MSDiff to {0}", relativeFilePath);
                    var msDelta = new MsDeltaCompression();
                    msDelta.ApplyDelta(inputFile, finalTarget, tempTargetFile);

                    verifyPatchedFile(relativeFilePath, inputFile, tempTargetFile);
                } else {
                    using (var of = File.OpenWrite(tempTargetFile))
                    using (var inf = File.OpenRead(inputFile)) {
                        this.Log().Info("Adding new file: {0}", relativeFilePath);
                        inf.CopyTo(of);
                    }
                }

                if (File.Exists(finalTarget)) File.Delete(finalTarget);

                var targetPath = Directory.GetParent(finalTarget);
                if (!targetPath.Exists) targetPath.Create();

                File.Move(tempTargetFile, finalTarget);
            } finally {
                if (File.Exists(tempTargetFile)) Utility.DeleteFileHarder(tempTargetFile, true);
            }
        }

        void verifyPatchedFile(string relativeFilePath, string inputFile, string tempTargetFile)
        {
            var shaFile = Regex.Replace(inputFile, @"\.(bs)?diff$", ".shasum");
            var expectedReleaseEntry = ReleaseEntry.ParseReleaseEntry(File.ReadAllText(shaFile, Encoding.UTF8));
            var actualReleaseEntry = ReleaseEntry.GenerateFromFile(tempTargetFile);

            if (expectedReleaseEntry.Filesize != actualReleaseEntry.Filesize) {
                this.Log().Warn("Patched file {0} has incorrect size, expected {1}, got {2}", relativeFilePath,
                    expectedReleaseEntry.Filesize, actualReleaseEntry.Filesize);
                throw new ChecksumFailedException() {Filename = relativeFilePath};
            }

            if (expectedReleaseEntry.SHA1 != actualReleaseEntry.SHA1) {
                this.Log().Warn("Patched file {0} has incorrect SHA1, expected {1}, got {2}", relativeFilePath,
                    expectedReleaseEntry.SHA1, actualReleaseEntry.SHA1);
                throw new ChecksumFailedException() {Filename = relativeFilePath};
            }
        }

        bool bytesAreIdentical(byte[] oldData, byte[] newData)
        {
            if (oldData == null || newData == null) {
                return oldData == newData;
            }
            if (oldData.LongLength != newData.LongLength) {
                return false;
            }

            for(long i = 0; i < newData.LongLength; i++) {
                if (oldData[i] != newData[i]) {
                    return false;
                }
            }

            return true;
        }
    }
}
