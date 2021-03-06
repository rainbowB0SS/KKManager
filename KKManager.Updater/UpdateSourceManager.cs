﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KKManager.Updater.Data;
using KKManager.Updater.Properties;
using KKManager.Updater.Sources;
using KKManager.Util;

namespace KKManager.Updater
{
    public static class UpdateSourceManager
    {
        public static UpdateSourceBase GetUpdater(Uri link, int listingPriority)
        {
            switch (link.Scheme)
            {
                case "file":
                    return new ZipUpdater(new FileInfo(link.LocalPath), listingPriority);
                case "ftp":
                    return new FtpUpdater(link, listingPriority);
                case "https":
                    if (link.Host.ToLower() == "mega.nz")
                        return new MegaUpdater(link, listingPriority);
                    throw new NotSupportedException("Host is not supported as an update source: " + link.Host);
                default:
                    throw new NotSupportedException("Link format is not supported as an update source: " + link.Scheme);
            }
        }

        public static async Task<List<UpdateTask>> GetUpdates(CancellationToken cancellationToken, UpdateSourceBase[] updateSources, string[] filterByGuids = null)
        {
            Console.WriteLine("Starting update search...");
            var results = new ConcurrentBag<UpdateTask>();

            var ignoreListPath = "ignorelist.txt";
            var ignoreList = File.Exists(ignoreListPath) ? File.ReadAllLines(ignoreListPath) : new string[0];

            var anySuccessful = false;

            // First start all of the sources, then wait until they all finish
            var concurrentTasks = updateSources.Select(source => new
            {
                task = RetryHelper.RetryOnExceptionAsync(
                async () =>
                {
                    foreach (var task in await source.GetUpdateItems(cancellationToken))
                    {
                        // todo move further inside or decouple getting update tasks and actually processing them
                        if (filterByGuids != null && filterByGuids.Length > 0 && !filterByGuids.Contains(task.Info.GUID))
                            continue;

                        if (task.Items.Any()) anySuccessful = true;

                        task.Items.RemoveAll(x => x.UpToDate || (x.RemoteFile != null && ignoreList.Any(x.RemoteFile.Name.Contains)));
                        results.Add(task);
                    }
                },
                3, TimeSpan.FromSeconds(3), cancellationToken),
                source
            }).ToList();

            foreach (var task in concurrentTasks)
            {
                try
                {
                    await task.task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[ERROR] Unexpected error while collecting updates from source {task.source.Origin} - skipping the source. Error: {e.ToStringDemystified()}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if(!anySuccessful) throw new InvalidDataException("No valid update sources were found. Your UpdateSources file might be corrupted or in an old format.");

            var filteredTasks = new List<UpdateTask>();
            foreach (var modGroup in results.GroupBy(x => x.Info.GUID))
            {
                var ordered = modGroup.OrderByDescending(x => x.Info.Source.DiscoveryPriority).ThenByDescending(x => x.ModifiedTime ?? DateTime.MinValue).ToList();
                if (ordered.Count > 1)
                {
                    ordered[0].AlternativeSources.AddRange(ordered.Skip(1));
                    Console.WriteLine($"Found {ordered.Count} sources for mod GUID {modGroup.Key} - choosing {ordered[0].Info.Source.Origin} as latest");
                }
                filteredTasks.Add(ordered[0]);
            }

            Console.WriteLine($"Update search finished. Found {filteredTasks.Count} update tasks.");
            return filteredTasks;
        }

        public static UpdateSourceBase[] FindUpdateSources(string searchDirectory)
        {
            var updateSourcesPath = Path.Combine(searchDirectory, "UpdateSources");

            var updateSources = new string[0];
            if (File.Exists(updateSourcesPath) && File.ReadAllText(updateSourcesPath).Length > 0)
            {
                Console.WriteLine("Found UpdateSources file at " + updateSourcesPath);

                updateSources = File.ReadAllLines(updateSourcesPath).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            }
            else
            {
                Console.WriteLine("Looking for update sources...");
                try
                {
                    updateSources = new WebClient().DownloadString(PathTools.AdjustFormat(Resources.Test)).Split();
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }

            return GetUpdateSources(updateSources.Select(PathTools.AdjustFormat).ToArray());
        }

        /// <summary>
        /// Order is important. First items have higher priority in determining if there are updates, lower priorities are only used as mirrors
        /// </summary>
        public static UpdateSourceBase[] GetUpdateSources(string[] updateSourceUrls)
        {
            var results = new List<UpdateSourceBase>(updateSourceUrls.Length);
            // Higher on the list means higher priority
            for (var index = 0; index < updateSourceUrls.Length; index++)
            {
                var updateSource = updateSourceUrls[index];
                try
                {
                    results.Add(GetUpdater(new Uri(updateSource), -index));
#if DEBUG
                    Console.WriteLine($"Added {updateSource}");
#endif
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not open update source: {updateSource} - {ex.ToStringDemystified()}");
                }
            }

            if (results.Count < updateSourceUrls.Length)
                Console.WriteLine($"Could not open {updateSourceUrls.Length - results.Count} out of {updateSourceUrls.Length} update sources, check log for details");

            return results.ToArray();
        }

        public static IEnumerable<UpdateItem> FileInfosToDeleteItems(IEnumerable<FileSystemInfo> localContents)
        {
            var results = new List<UpdateItem>();
            foreach (var localItem in localContents)
            {
                if (!localItem.Exists) continue;

                switch (localItem)
                {
                    case FileInfo fi:
                        results.Add(new DeleteFileUpdateItem(fi));
                        break;

                    case DirectoryInfo di:
                        results.AddRange(di.GetFiles("*", SearchOption.AllDirectories).Select(subFi => new DeleteFileUpdateItem(subFi)));
                        break;
                }
            }
            return results;
        }
    }
}
