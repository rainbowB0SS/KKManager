using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KKManager.Functions;
using KKManager.Util;
using KKManager.Util.ProcessWaiter;

namespace KKManager.Updater.Data
{
    public class UpdateItem
    {
        public UpdateItem(FileSystemInfo targetPath, IRemoteItem remoteFile, bool upToDate)
        {
            TargetPath = targetPath ?? throw new ArgumentNullException(nameof(targetPath));
            RemoteFile = remoteFile;
            UpToDate = upToDate;
        }

        public static async Task<FileInfo> GetTempDownloadFilename()
        {
            var tempPath = Path.Combine(InstallDirectoryHelper.KoikatuDirectory.FullName, "temp\\KKManager_downloads");

            retryCreate:
            try
            {
                Directory.CreateDirectory(tempPath);

                retry:
                var fileName = Path.Combine(tempPath, Path.GetRandomFileName());
                if (File.Exists(fileName))
                    goto retry;

                return new FileInfo(fileName);
            }
            catch (IOException ex)
            {
                if (await ProcessWaiter.CheckForProcessesBlockingKoiDir() != true)
                    throw new IOException($"Failed to create file in directory {tempPath} because of an IO issue - {ex.Message}", ex);

                goto retryCreate;
            }
            catch (SecurityException ex)
            {
                if (MessageBox.Show($"Failed to create file in directory {tempPath} because of a security issue - {ex.Message}\n\nDo you want KK Manager to attempt to fix the issue? Click cancel if you want to abort.",
                        "Could not apply update", MessageBoxButtons.OKCancel, MessageBoxIcon.Error) == DialogResult.OK)
                {
                    var fixPermissions = ProcessTools.FixPermissions(InstallDirectoryHelper.KoikatuDirectory.FullName);
                    if (fixPermissions == null) throw new IOException($"Failed to create file in directory {tempPath} because of a security issue - {ex.Message}", ex);
                    fixPermissions.WaitForExit();
                    goto retryCreate;
                }
                throw;
            }
        }

        /// <exception cref="IOException">Failed to apply the update.</exception>
        public async Task Update(Progress<double> progressCallback, CancellationToken cancellationToken)
        {
            var downloadTarget = await GetTempDownloadFilename();
            // Need to store the filename because MoveTo changes it to the new filename
            var downloadFilename = downloadTarget.FullName;

            if (RemoteFile != null)
            {
                Console.WriteLine($"Attempting download from source {RemoteFile.Source.Origin}");
                await RetryHelper.RetryOnExceptionAsync(async () => await RemoteFile.Download(downloadTarget, progressCallback, cancellationToken), 2, TimeSpan.FromSeconds(10), cancellationToken);

                downloadTarget.Refresh();
                if (!downloadTarget.Exists || downloadTarget.Length != RemoteFile.ItemSize)
                    throw new IOException($"Failed to download the update file {RemoteFile.Name} - the downloaded file doesn't exist or is corrupted");

                Console.WriteLine($"Downloaded {downloadTarget.Length} bytes successfully");
            }

            retryDelete:
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TargetPath.FullName));
                if (TargetPath.Exists)
                {
                    Console.WriteLine($"Deleting old file {TargetPath.FullName}");
                    TargetPath.Delete();
                }
                if (RemoteFile != null)
                    downloadTarget.MoveTo(TargetPath.FullName);
            }
            catch (IOException ex)
            {
                if (await ProcessWaiter.CheckForProcessesBlockingKoiDir() != true)
                    throw new IOException($"Failed to apply update {TargetPath.FullName} because of an IO issue - {ex.Message}", ex);

                goto retryDelete;
            }
            catch (SecurityException ex)
            {
                if (MessageBox.Show($"Failed to apply update {TargetPath.FullName} because of a security issue - {ex.Message}\n\nDo you want KK Manager to attempt to fix the issue? Click cancel if you want to abort.",
                        "Could not apply update", MessageBoxButtons.OKCancel, MessageBoxIcon.Error) != DialogResult.OK)
                    throw;

                var fixPermissions = ProcessTools.FixPermissions(InstallDirectoryHelper.KoikatuDirectory.FullName);
                if (fixPermissions == null) throw new IOException($"Failed to create file in directory {TargetPath.FullName} because of a security issue - {ex.Message}", ex);
                fixPermissions.WaitForExit();
                goto retryDelete;
            }
            finally
            {
                try { File.Delete(downloadFilename); }
                catch (SystemException ex) { Console.WriteLine(ex); }
            }
        }

        public FileSystemInfo TargetPath { get; }
        public IRemoteItem RemoteFile { get; }
        public bool UpToDate { get; }

        public FileSize GetDownloadSize()
        {
            if (RemoteFile == null) return FileSize.Empty;
            return FileSize.FromBytes(RemoteFile.ItemSize);
        }
    }
}